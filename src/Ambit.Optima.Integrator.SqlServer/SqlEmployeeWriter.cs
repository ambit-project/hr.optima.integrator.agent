using System.Data;
using System.Globalization;
using System.Text.Json;
using Ambit.Optima.Integrator.Core;
using Ambit.Optima.Integrator.Core.Employees;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>
/// Adds one employee to one company, or to every active company on Optima's list. Each company is handled on its own,
/// with its own transaction and its own row in <c>integrator.employee_call</c>, so one failure does not stop the others.
/// </summary>
/// <remarks>
/// <para>The order of work:</para>
/// <list type="number">
/// <item>Validate the request. A bad request is refused before any company is touched.</item>
/// <item>List the companies.</item>
/// <item>For each company: claim its row and commit, so a later rollback cannot erase the record; skip it without access,
/// with a different tax number, or when the employee is already there; otherwise add the employee in one transaction on
/// the company database and record the outcome.</item>
/// <item>Return one row per company, read from the record, so it also covers companies a previous call finished.</item>
/// </list>
/// <para>Status of a company's row: <c>Started</c>, <c>Succeeded</c>, <c>AlreadyExists</c>, <c>NoAccess</c>,
/// <c>Failed</c> or <c>Unknown</c> (cut off when the Worker stopped). Calling again with the same operation id repeats
/// only <c>NoAccess</c>, <c>Failed</c> and <c>Unknown</c>.</para>
/// </remarks>
public sealed class SqlEmployeeWriter(SqlConnections connections, OptimaCompanies companies)
    : IEmployeeWriter
{
    private const string MatchedByAcronymOnly = "MATCHED_BY_ACRONYM_ONLY";
    private const string PositionNotFound = "POSITION_NOT_FOUND";
    private const string DocumentNotStored = "DOCUMENT_NOT_STORED";

    /// <summary>
    /// Comarch's <c>CDN.DodajPracownika</c> adds the employee. <c>PRA_ImportRowId</c> holds the HR application's employee
    /// id and <c>PRA_Kod</c> the acronym. Dates are <c>YYYYMMDD</c> text, the only form the procedure reads correctly.
    /// </summary>
    private const string AddEmployeeProcedure = "CDN.DodajPracownika";

    /// <summary>The columns <c>CDN.DodajPracownika</c> has no parameter for.</summary>
    private const string CompleteContractSql = """
        UPDATE CDN.PracEtaty
        SET PRE_Plec = N'',
            PRE_Obywatelstwo = @CitizenshipName,
            PRE_ObywatelKodKraju = @CitizenshipCode,
            PRE_MLDKodKraju = @AddressCountryCode,
            PRE_DowOsobisty = @IdentityCard,
            PRE_Paszport = @Passport,
            PRE_DowWaznyDo = @DocumentValidUntil,
            PRE_ZatrudnionyDo = @EndsOn
        WHERE PRE_PreId = @ContractId;

        SELECT PRE_PraId FROM CDN.PracEtaty WHERE PRE_PreId = @ContractId;
        """;

    public async Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
        EmployeeAddRequest request,
        CancellationToken cancellationToken
    )
    {
        var employee = EmployeeValidation.Validate(request);
        var values = OptimaEmployeeValues.From(employee);
        var operationId = employee.OperationId!.Value;

        var listed = await companies.ListActiveAsync(employee.DatabaseName, cancellationToken);
        if (listed.Count == 0)
            throw new EmployeeRejectedException(
                RejectionCodes.NoCompanyMatches,
                "No active Optima company database matches this request."
            );

        await using var integrator = await connections.OpenIntegratorAsync(cancellationToken);

        foreach (var company in listed)
        {
            if (!await ClaimAsync(integrator, employee, values, company, cancellationToken))
                continue;

            var outcome = await AddToCompanyAsync(employee, values, company, cancellationToken);
            await RecordAsync(integrator, operationId, company, outcome, cancellationToken);
        }

        var results = await integrator.QueryAsync<EmployeeAddResult>(
            new CommandDefinition(
                """
                SELECT
                    database_name AS DatabaseName, status AS Status, attempt AS Attempt,
                    optima_employee_id AS OptimaEmployeeId, optima_contract_id AS OptimaContractId,
                    warning_codes AS WarningCodes, error_number AS ErrorNumber, error_procedure AS ErrorProcedure,
                    optima_error_codes AS OptimaErrorCodes
                FROM integrator.employee_call
                WHERE operation_id = @operationId AND database_name IN @names
                ORDER BY database_name;
                """,
                new { operationId, names = listed.Select(company => company.DatabaseName) },
                commandTimeout: connections.CommandTimeout,
                cancellationToken: cancellationToken
            )
        );
        return results.AsList();
    }

    /// <summary>
    /// Takes the company's row for this call, committed on its own. The locks make two simultaneous calls with one
    /// operation id take turns, so only one of them proceeds. False for a row that is <c>Started</c>,
    /// <c>Succeeded</c> or <c>AlreadyExists</c>: that company is skipped and its row reported as it is.
    /// </summary>
    private async Task<bool> ClaimAsync(
        SqlConnection integrator,
        EmployeeAddRequest employee,
        OptimaEmployeeValues values,
        OptimaCompany company,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = (SqlTransaction)
            await integrator.BeginTransactionAsync(cancellationToken);
        var key = new { employee.OperationId, company.DatabaseName };

        var previous = await integrator.QuerySingleOrDefaultAsync<string?>(
            Command(
                """
                SELECT status FROM integrator.employee_call WITH (UPDLOCK, HOLDLOCK)
                WHERE operation_id = @OperationId AND database_name = @DatabaseName;
                """,
                key,
                transaction,
                cancellationToken
            )
        );

        var proceed = true;
        if (previous is null)
            await integrator.ExecuteAsync(
                Command(
                    """
                    INSERT integrator.employee_call
                        (operation_id, database_name, employee_id, employee_code, status, masked_parameters)
                    VALUES
                        (@OperationId, @DatabaseName, @EmployeeId, @EmployeeCode, 'Started', @MaskedParameters);
                    """,
                    new
                    {
                        employee.OperationId,
                        company.DatabaseName,
                        employee.EmployeeId,
                        EmployeeCode = new DbString
                        {
                            Value = employee.EmployeeCode,
                            IsAnsi = true,
                            Length = 20,
                        },
                        values.MaskedParameters,
                    },
                    transaction,
                    cancellationToken
                )
            );
        else if (previous is "Failed" or "Unknown" or "NoAccess")
            await integrator.ExecuteAsync(
                Command(
                    """
                    UPDATE integrator.employee_call
                    SET status = 'Started', attempt = attempt + 1, started_at = SYSUTCDATETIME(), finished_at = NULL,
                        error_number = NULL, error_procedure = NULL, error_line = NULL, optima_error_codes = NULL,
                        warning_codes = NULL
                    WHERE operation_id = @OperationId AND database_name = @DatabaseName;
                    """,
                    key,
                    transaction,
                    cancellationToken
                )
            );
        else
            proceed = false;

        await transaction.CommitAsync(cancellationToken);
        return proceed;
    }

    private async Task<CompanyOutcome> AddToCompanyAsync(
        EmployeeAddRequest employee,
        OptimaEmployeeValues values,
        OptimaCompany company,
        CancellationToken cancellationToken
    )
    {
        if (!await companies.HasAccessAsync(company.DatabaseName, cancellationToken))
            return new CompanyOutcome("NoAccess");

        SqlTransaction? transaction = null;
        try
        {
            await using var connection = await connections.OpenCompanyAsync(
                company.DatabaseName,
                cancellationToken
            );

            if (!await companies.IsSameCompanyAsync(connection, company, cancellationToken))
                return new CompanyOutcome("Failed", ErrorNumber: FailureCodes.TaxNumberMismatch);

            // The acronym decides; the employee id also finds one renamed in Optima. When both match different rows, the
            // row with the employee id wins. This check is what stops a repeated call from adding a second employee.
            var existing = await connection.QuerySingleOrDefaultAsync<ExistingEmployee>(
                Command(
                    """
                    SELECT TOP (1) PRA_PraId AS Id, PRA_ImportRowId AS ImportRowId
                    FROM CDN.PracKod
                    WHERE PRA_Kod = @Code OR PRA_ImportRowId = @ImportRowId
                    ORDER BY CASE WHEN PRA_ImportRowId = @ImportRowId THEN 0 ELSE 1 END;
                    """,
                    new { Code = employee.EmployeeCode, values.ImportRowId },
                    null,
                    cancellationToken
                )
            );
            if (existing is not null)
                return new CompanyOutcome(
                    "AlreadyExists",
                    OptimaEmployeeId: existing.Id,
                    WarningCodes: string.Equals(
                        existing.ImportRowId,
                        values.ImportRowId,
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? null
                        : MatchedByAcronymOnly
                );

            // A position or document Optima cannot take does not stop the employee: it becomes a warning.
            var warnings = new List<string>();
            int? positionId = null;
            if (employee.PositionName is { } positionName)
            {
                positionId = await connection.QuerySingleOrDefaultAsync<int?>(
                    Command(
                        """
                        SELECT TOP (1) DKM_DkmId FROM CDN.DaneKadMod
                        WHERE DKM_Rodzaj = 1 AND DKM_Nazwa = @positionName
                        ORDER BY DKM_DkmId;
                        """,
                        new { positionName },
                        null,
                        cancellationToken
                    )
                );
                if (positionId is null)
                    warnings.Add(PositionNotFound);
            }
            if (
                employee.DocumentNumber != ""
                && employee.DocumentKind is "ResidenceCard" or "Other"
            )
                warnings.Add(DocumentNotStored);

            await connection.ExecuteAsync(
                Command("SET XACT_ABORT ON;", null, null, cancellationToken)
            );
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var parameters = AddEmployeeParameters(employee, values, positionId);
            await connection.ExecuteAsync(
                new CommandDefinition(
                    AddEmployeeProcedure,
                    parameters,
                    transaction,
                    connections.CommandTimeout,
                    CommandType.StoredProcedure,
                    cancellationToken: cancellationToken
                )
            );
            var contractId = parameters.Get<int?>("@SlowId");

            var employeeRowId = await connection.ExecuteScalarAsync<int?>(
                Command(
                    CompleteContractSql,
                    new
                    {
                        values.CitizenshipName,
                        CitizenshipCode = employee.CitizenshipCode ?? "",
                        values.AddressCountryCode,
                        values.IdentityCard,
                        values.Passport,
                        DocumentValidUntil = employee.DocumentValidUntil?.ToDateTime(
                            TimeOnly.MinValue
                        ),
                        EndsOn = employee.EndsOn?.ToDateTime(TimeOnly.MinValue),
                        ContractId = contractId,
                    },
                    transaction,
                    cancellationToken
                )
            );

            await transaction.CommitAsync(cancellationToken);

            warnings.InsertRange(
                0,
                OptimaMessages.Codes(parameters.Get<string?>("@Warnings"))?.Split(',') ?? []
            );
            return new CompanyOutcome(
                "Succeeded",
                OptimaEmployeeId: employeeRowId,
                OptimaContractId: contractId,
                WarningCodes: warnings.Count == 0 ? null : string.Join(',', warnings)
            );
        }
        catch (SqlException exception)
        {
            // The message text is read only for its OPT codes and never stored, because it can hold personal data.
            await RollBackAsync(transaction);
            return new CompanyOutcome(
                "Failed",
                ErrorNumber: exception.Number,
                ErrorProcedure: exception.Procedure is "" ? null : exception.Procedure,
                ErrorLine: exception.LineNumber,
                OptimaErrorCodes: OptimaMessages.Codes(
                    exception.Errors.Cast<SqlError>().Select(error => error.Message)
                )
            );
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Every parameter is passed, as Comarch's examples do. Text sizes are Optima's; validation has already refused longer
    /// values, so nothing is truncated here.
    /// </summary>
    private static DynamicParameters AddEmployeeParameters(
        EmployeeAddRequest employee,
        OptimaEmployeeValues values,
        int? positionId
    )
    {
        var p = new DynamicParameters();
        p.Add("@OnlyNew", 1);
        p.Add("@ConfigDBName", "");
        p.Add("@ServerName", "");
        p.Add("@PRA_ImportRowId", values.ImportRowId);
        p.Add("@PRA_Kod", employee.EmployeeCode);
        p.Add("@PRA_Archiwalny", 0);
        p.Add("@PRE_Nazwisko", employee.LastName);
        p.Add("@PRE_Imie1", employee.FirstName);
        p.Add("@PRE_Imie2", "");
        p.Add("@PRE_ImieOjca", "");
        p.Add("@PRE_ImieMatki", "");
        p.Add("@PRE_DataUr", values.DateOfBirth);
        p.Add("@PRE_MiejsceUr", "");
        p.Add("@PRE_NazwRodowe", "");
        p.Add("@PRE_NazwRodoweM", "");
        p.Add("@PRE_Pesel", employee.Pesel);
        p.Add("@PRE_NipKraj", "");
        p.Add("@PRE_NipE", "");
        p.Add("@Status", "");
        p.Add("@PRE_MLDKraj", values.AddressCountryName);
        p.Add("@PRE_MLDWojewodztwo", "");
        p.Add("@PRE_MLDPowiat", "");
        p.Add("@PRE_MLDGmina", "");
        p.Add("@PRE_MLDUlica", employee.Street);
        p.Add("@PRE_MLDNrDomu", employee.BuildingNumber);
        p.Add("@PRE_MLDNrLokalu", employee.UnitNumber);
        p.Add("@PRE_MLDMiasto", employee.City);
        p.Add("@PRE_MLDKodPocztowy", employee.PostalCode);
        p.Add("@PRE_MLDPoczta", "");
        p.Add("@PRE_MLDAdres2", "");
        p.Add("@PRE_HDKTelefon1", employee.Phone);
        p.Add("@PRE_ZatrudnionyOd", values.StartsOn);
        p.Add("@PRE_PODMnoznikKoszty", 1);
        p.Add("@PRE_PODMnoznikUlga", 1);
        p.Add("@Urz_Akronim", null, DbType.String);
        p.Add("@Urz_ImportRowId", null, DbType.String);
        p.Add("@Fpl_Nazwa", null, DbType.String);
        p.Add("@Fpl_ImportRowId", null, DbType.String);
        p.Add("@Kat_KodSzczegol", null, DbType.String);
        p.Add("@Kat_ImportRowId", null, DbType.String);
        p.Add("@PRE_KategoriaOpis", "");
        p.Add("@BNa_Numer", null, DbType.String);
        p.Add("@BNa_ImportRowId", null, DbType.String);
        p.Add("@PRE_RachunekNr", "");
        p.Add("@PRE_IBAN", 0);
        p.Add("@PRE_HDKUwagi", employee.Note);
        p.Add("@PRE_NieRozliczac", 0);
        p.Add("@PRE_RodzajDokumentu", 0);
        p.Add("@PRE_KontoKsiegowe", "");
        p.Add("@PRE_KontoKsiegowe2", "");
        p.Add("@PRE_ETAEtatL", null, DbType.Int32);
        p.Add("@PRE_ETAEtatM", null, DbType.Int32);
        p.Add("@Zak_Symbol", null, DbType.String);
        p.Add("@Zak_ImportRowId", null, DbType.String);
        p.Add("@ZrdId", "AMBIT");
        p.Add("@PRE_HDKEmail", employee.Email);
        p.Add("@CntId", employee.CenterId);
        p.Add("@StanowiskoId", positionId, DbType.Int32);
        p.Add("@OpeId", employee.OperatorId, DbType.Int32);
        p.Add("@SlowId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        p.Add("@Warnings", dbType: DbType.String, direction: ParameterDirection.Output, size: 4000);
        return p;
    }

    private async Task RecordAsync(
        SqlConnection integrator,
        Guid operationId,
        OptimaCompany company,
        CompanyOutcome outcome,
        CancellationToken cancellationToken
    ) =>
        await integrator.ExecuteAsync(
            Command(
                """
                UPDATE integrator.employee_call
                SET status = @Status,
                    optima_employee_id = @OptimaEmployeeId,
                    optima_contract_id = @OptimaContractId,
                    warning_codes = @WarningCodes,
                    error_number = @ErrorNumber,
                    error_procedure = @ErrorProcedure,
                    error_line = @ErrorLine,
                    optima_error_codes = @OptimaErrorCodes,
                    finished_at = SYSUTCDATETIME()
                WHERE operation_id = @operationId AND database_name = @databaseName;
                """,
                new
                {
                    outcome.Status,
                    outcome.OptimaEmployeeId,
                    outcome.OptimaContractId,
                    outcome.WarningCodes,
                    outcome.ErrorNumber,
                    outcome.ErrorProcedure,
                    outcome.ErrorLine,
                    outcome.OptimaErrorCodes,
                    operationId,
                    databaseName = company.DatabaseName,
                },
                null,
                cancellationToken
            )
        );

    /// <summary>
    /// With XACT_ABORT the server may already have rolled the transaction back, and then there is nothing left to undo.
    /// </summary>
    internal static async Task RollBackAsync(SqlTransaction? transaction)
    {
        if (transaction?.Connection is null)
            return;
        try
        {
            await transaction.RollbackAsync();
        }
        catch (InvalidOperationException) { }
        catch (SqlException) { }
    }

    private CommandDefinition Command(
        string sql,
        object? parameters,
        SqlTransaction? transaction,
        CancellationToken cancellationToken
    ) =>
        new(
            sql,
            parameters,
            transaction,
            connections.CommandTimeout,
            cancellationToken: cancellationToken
        );

    private sealed record ExistingEmployee(int Id, string? ImportRowId);

    private sealed record CompanyOutcome(
        string Status,
        int? OptimaEmployeeId = null,
        int? OptimaContractId = null,
        string? WarningCodes = null,
        int? ErrorNumber = null,
        string? ErrorProcedure = null,
        int? ErrorLine = null,
        string? OptimaErrorCodes = null
    );
}

/// <summary>An employee's values in the form Optima keeps them. Prepared once and used for every company.</summary>
public sealed record OptimaEmployeeValues(
    string ImportRowId,
    string? DateOfBirth,
    string? StartsOn,
    string CitizenshipName,
    string AddressCountryCode,
    string AddressCountryName,
    string IdentityCard,
    string Passport,
    string MaskedParameters
)
{
    /// <summary>Optima keeps countries as names; the HR application sends ISO codes.</summary>
    private static readonly Dictionary<string, (string Country, string Citizenship)> Countries =
        new(StringComparer.Ordinal)
        {
            ["PL"] = ("Polska", "POLSKIE"),
            ["UA"] = ("Ukraina", "UKRAIŃSKIE"),
        };

    public static OptimaEmployeeValues From(EmployeeAddRequest employee)
    {
        var hasAddress =
            employee.Street + employee.BuildingNumber + employee.PostalCode + employee.City != "";

        // With no address, or no country given for it, the address country stays Polska / PL. An unknown code gives an
        // empty name, as it does for citizenship.
        var (addressCode, addressName) =
            hasAddress && employee.AddressCountryCode is { } code
                ? (code, Countries.TryGetValue(code, out var country) ? country.Country : "")
                : ("PL", "Polska");

        return new OptimaEmployeeValues(
            // Upper case, as SQL Server writes a uniqueidentifier converted to text.
            employee.EmployeeId!.Value.ToString().ToUpperInvariant(),
            employee.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            employee.StartsOn?.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            employee.CitizenshipCode is { } citizenship
            && Countries.TryGetValue(citizenship, out var citizenshipCountry)
                ? citizenshipCountry.Citizenship
                : "",
            addressCode,
            addressName,
            employee.DocumentKind == "IdentityCard" ? employee.DocumentNumber! : "",
            employee.DocumentKind == "Passport" ? employee.DocumentNumber! : "",
            Mask(employee, hasAddress)
        );
    }

    /// <summary>Which values were given, never what they were. The code and the dates identify the record.</summary>
    private static string Mask(EmployeeAddRequest employee, bool hasAddress) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["employee_code"] = employee.EmployeeCode,
                ["starts_on"] = employee.StartsOn,
                ["ends_on"] = employee.EndsOn,
                ["citizenship_code"] = employee.CitizenshipCode,
                ["document_kind"] = employee.DocumentKind,
                ["position_name"] = employee.PositionName,
                ["has_pesel"] = employee.Pesel != "",
                ["has_date_of_birth"] = employee.DateOfBirth is not null,
                ["has_document_number"] = employee.DocumentNumber != "",
                ["has_email"] = employee.Email != "",
                ["has_phone"] = employee.Phone != "",
                ["has_address"] = hasAddress,
                ["has_note"] = employee.Note != "",
            }
        );
}
