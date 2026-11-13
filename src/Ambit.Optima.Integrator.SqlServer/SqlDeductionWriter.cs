using System.Data;
using System.Globalization;
using Ambit.Optima.Integrator.Core;
using Ambit.Optima.Integrator.Core.Deductions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>
/// Gives one employee one month's deduction or allowance in one company, and records the outcome in
/// <c>integrator.deduction_call</c>, whose <c>kind</c> says which.
/// </summary>
/// <remarks>
/// <para>In Optima a deduction given to an employee is a row of <c>CDN.Dodatki</c> (the employee's component) with a row
/// of <c>CDN.DodatkiHist</c> (its value and the dates it is valid). Comarch's <c>CDN.ImportDataDodatki</c> and
/// <c>CDN.ImportDataDodatkiHist</c> write them, and both refuse an import key they already have. Both rows carry the
/// same key, derived from the deduction and the month, so a deduction exported twice for one month is written once.</para>
/// <para>The order of work: validate; report again what this operation already finished; stop without access or with a
/// different tax number; then, in one transaction on the company database, stop if the key is there, find the
/// employee, find the component, which must be active and of the item's kind, and write both rows.</para>
/// <para>An allowance is written exactly like a deduction: Comarch's procedures take any payroll component and check
/// neither kind.</para>
/// </remarks>
public sealed class SqlDeductionWriter(SqlConnections connections, OptimaCompanies companies)
    : IDeductionWriter
{
    private const string MatchedByAcronymOnly = "MATCHED_BY_ACRONYM_ONLY";

    /// <summary>The form Comarch's <c>CDN.ImportDataConvertToDatetime</c> reads.</summary>
    private const string DateFormat = "yyyy-MM-dd";

    private const string ResultColumns = """
        database_name AS DatabaseName, status AS Status, attempt AS Attempt,
        optima_employee_id AS OptimaEmployeeId, optima_addition_id AS OptimaAdditionId,
        warning_codes AS WarningCodes, error_number AS ErrorNumber, error_procedure AS ErrorProcedure
        """;

    public async Task<DeductionAddResult> AddAsync(
        DeductionAddRequest request,
        CancellationToken cancellationToken
    )
    {
        var deduction = DeductionValidation.Validate(request);

        var listed = await companies.ListActiveAsync(deduction.DatabaseName, cancellationToken);
        if (listed is not [var company])
            throw new DeductionRejectedException(
                RejectionCodes.NoCompanyMatches,
                "No active Optima company database matches this request."
            );

        await using var integrator = await connections.OpenIntegratorAsync(cancellationToken);
        var key = new { deduction.OperationId, deduction.DeductionId };

        // Work this operation finished before is only reported again.
        var finished = await integrator.QuerySingleOrDefaultAsync<DeductionAddResult>(
            Command(
                $"""
                SELECT {ResultColumns}
                FROM integrator.deduction_call
                WHERE operation_id = @OperationId AND deduction_id = @DeductionId
                  AND status IN ('Succeeded', 'AlreadyExists');
                """,
                key,
                null,
                cancellationToken
            )
        );
        if (finished is not null)
            return finished;

        var outcome = await WriteAsync(deduction, company, cancellationToken);

        // The record of this attempt. A row from an earlier attempt that did not finish the work is replaced.
        return await integrator.QuerySingleAsync<DeductionAddResult>(
            Command(
                $"""
                UPDATE integrator.deduction_call
                SET status = @Status, attempt = attempt + 1, database_name = @DatabaseName, employee_id = @EmployeeId,
                    optima_key = @OptimaKey, period_year = @Year, period_month = @Month,
                    optima_component_id = @OptimaComponentId, amount = @Amount,
                    optima_employee_id = @OptimaEmployeeId, optima_addition_id = @OptimaAdditionId, kind = @Kind,
                    warning_codes = @WarningCodes, error_number = @ErrorNumber, error_procedure = @ErrorProcedure,
                    error_line = @ErrorLine, started_at = SYSUTCDATETIME(), finished_at = SYSUTCDATETIME()
                WHERE operation_id = @OperationId AND deduction_id = @DeductionId;

                IF @@ROWCOUNT = 0
                    INSERT integrator.deduction_call
                        (operation_id, deduction_id, kind, database_name, employee_id, optima_key, period_year,
                         period_month, optima_component_id, amount, status, optima_employee_id, optima_addition_id,
                         warning_codes, error_number, error_procedure, error_line, finished_at)
                    VALUES
                        (@OperationId, @DeductionId, @Kind, @DatabaseName, @EmployeeId, @OptimaKey, @Year,
                         @Month, @OptimaComponentId, @Amount, @Status, @OptimaEmployeeId, @OptimaAdditionId,
                         @WarningCodes, @ErrorNumber, @ErrorProcedure, @ErrorLine, SYSUTCDATETIME());

                SELECT {ResultColumns}
                FROM integrator.deduction_call
                WHERE operation_id = @OperationId AND deduction_id = @DeductionId;
                """,
                new
                {
                    deduction.OperationId,
                    deduction.DeductionId,
                    deduction.DatabaseName,
                    deduction.EmployeeId,
                    deduction.OptimaKey,
                    deduction.Year,
                    deduction.Month,
                    deduction.OptimaComponentId,
                    deduction.Amount,
                    Kind = deduction.Kind.ToString(),
                    outcome.Status,
                    outcome.OptimaEmployeeId,
                    outcome.OptimaAdditionId,
                    outcome.WarningCodes,
                    outcome.ErrorNumber,
                    outcome.ErrorProcedure,
                    outcome.ErrorLine,
                },
                null,
                cancellationToken
            )
        );
    }

    private async Task<Outcome> WriteAsync(
        ValidDeduction deduction,
        OptimaCompany company,
        CancellationToken cancellationToken
    )
    {
        if (!await companies.HasAccessAsync(company.DatabaseName, cancellationToken))
            return new Outcome("NoAccess");

        // Upper case, as SQL Server writes a uniqueidentifier converted to text.
        var keyText = deduction.OptimaKey.ToString().ToUpperInvariant();
        var importRowId = deduction.EmployeeId.ToString().ToUpperInvariant();
        int? employeeRowId = null;
        SqlTransaction? transaction = null;
        try
        {
            await using var connection = await connections.OpenCompanyAsync(
                company.DatabaseName,
                cancellationToken
            );

            if (!await companies.IsSameCompanyAsync(connection, company, cancellationToken))
                return new Outcome("Failed", ErrorNumber: FailureCodes.TaxNumberMismatch);

            await connection.ExecuteAsync(
                Command("SET XACT_ABORT ON;", null, null, cancellationToken)
            );
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            // The lock holds the key until the commit, so two calls with one key cannot both pass this check.
            var existing = await connection.QuerySingleOrDefaultAsync<WrittenDeduction>(
                Command(
                    """
                    SELECT h.DOH_DodId AS AdditionId, d.DOD_PraId AS EmployeeRowId
                    FROM CDN.DodatkiHist AS h WITH (UPDLOCK, HOLDLOCK)
                    JOIN CDN.Dodatki AS d ON d.DOD_DodId = h.DOH_DodId
                    WHERE h.DOH_ImportRowId = @keyText;
                    """,
                    new { keyText = Ansi(keyText) },
                    transaction,
                    cancellationToken
                )
            );
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new Outcome(
                    "AlreadyExists",
                    OptimaEmployeeId: existing.EmployeeRowId,
                    OptimaAdditionId: existing.AdditionId
                );
            }

            // The HR application's id decides. The acronym also finds a person imported earlier from a file, whom
            // Optima gave its own id; that match is reported, since it may be a different person.
            var employee = await connection.QuerySingleOrDefaultAsync<EmployeeMatch>(
                Command(
                    """
                    SELECT TOP (1) PRA_PraId AS Id, PRA_ImportRowId AS ImportRowId
                    FROM CDN.PracKod
                    WHERE PRA_ImportRowId = @importRowId OR PRA_Kod = @employeeCode
                    ORDER BY CASE WHEN PRA_ImportRowId = @importRowId THEN 0 ELSE 1 END;
                    """,
                    new { importRowId, employeeCode = deduction.EmployeeCode },
                    transaction,
                    cancellationToken
                )
            );
            if (employee is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new Outcome("EmployeeNotFound");
            }
            employeeRowId = employee.Id;
            var warningCodes = string.Equals(
                employee.ImportRowId,
                importRowId,
                StringComparison.OrdinalIgnoreCase
            )
                ? null
                : MatchedByAcronymOnly;

            // Comarch's procedure finds the component by its import ids, so they are read here from its id, and it
            // refuses a component without both. An allowance mapped to a deduction component, or the other way round,
            // is not found, because only an active component of the item's kind qualifies.
            var component = await connection.QuerySingleOrDefaultAsync<Component>(
                Command(
                    """
                    SELECT CONVERT(nvarchar(40), TWP_Nazwa) AS Name,
                        TWP_ImportRowId AS ImportRowId, TWP_ImportRowId2 AS ImportRowId2
                    FROM CDN.TypWyplata
                    WHERE TWP_TwpId = @componentId
                      AND CASE WHEN TWP_AlgPotracenie = 1 THEN 1 ELSE 0 END = @isDeduction
                      AND ISNULL(TWP_Nieaktywny, 0) = 0
                      AND ISNULL(TWP_Wycofany, 0) = 0
                      AND TWP_ImportRowId IS NOT NULL
                      AND TWP_ImportRowId2 IS NOT NULL;
                    """,
                    new
                    {
                        componentId = deduction.OptimaComponentId,
                        isDeduction = deduction.Kind == PayItemKind.Deduction ? 1 : 0,
                    },
                    transaction,
                    cancellationToken
                )
            );
            if (component is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new Outcome(
                    "ComponentNotFound",
                    OptimaEmployeeId: employeeRowId,
                    WarningCodes: warningCodes
                );
            }

            // The employee's component, then its value for the month: valid from the first to the last day of the
            // month, no operator, payment as the employee's. Both rows carry the same key.
            var addition = new DynamicParameters();
            addition.Add("@PRA_PraId", employeeRowId);
            addition.Add("@OnlyNew", 1);
            addition.Add("@ConfigDBName", "");
            addition.Add("@ServerName", "");
            addition.Add("@DOD_ImportRowId", keyText);
            addition.Add("@DOD_Nazwa", component.Name);
            addition.Add("@RDZ_ImportRowId", null, DbType.String);
            addition.Add("@DOD_Rodzaj", 0);
            addition.Add("@DOD_Nieaktywny", 0);
            addition.Add("@DOD_Umowa", 0);
            addition.Add("@DOD_WliczChorWOkresie", 0);
            addition.Add("@ZrdId", "AMBIT");
            addition.Add("@OpeId", null, DbType.Int32);
            addition.Add("@IdParam", dbType: DbType.Int32, direction: ParameterDirection.Output);
            addition.Add(
                "@Warnings",
                dbType: DbType.String,
                direction: ParameterDirection.Output,
                size: 4000
            );
            await connection.ExecuteAsync(
                Procedure("CDN.ImportDataDodatki", addition, transaction, cancellationToken)
            );
            var additionId = addition.Get<int?>("@IdParam");

            var history = new DynamicParameters();
            history.Add("@DOD_DodId", additionId, DbType.Int32);
            history.Add("@OnlyNew", 1);
            history.Add("@ConfigDBName", "");
            history.Add("@ServerName", "");
            history.Add("@DOH_ImportRowId", keyText);
            history.Add("@TWP_ImportRowId", component.ImportRowId);
            history.Add("@TWP_ImportRowId2", component.ImportRowId2);
            history.Add("@DOH_ListaPlacSymbol", null, DbType.String);
            history.Add("@DOH_Nazwa", component.Name);
            history.Add("@DOH_OkresOd_VCH", "1900-01-01 00:00:00");
            history.Add("@DOH_OkresDo_VCH", "2999-12-31 00:00:00");
            history.Add(
                "@DOH_WaznyOd_VCH",
                deduction.ValidFrom.ToString(DateFormat, CultureInfo.InvariantCulture) + " 00:00:00"
            );
            history.Add(
                "@DOH_WaznyDo_VCH",
                deduction.ValidTo.ToString(DateFormat, CultureInfo.InvariantCulture) + " 00:00:00"
            );
            history.Add("@DOH_Wartosc1", deduction.Amount, DbType.Decimal, precision: 15, scale: 2);
            history.Add("@DOH_Wartosc2", 0m, DbType.Decimal, precision: 15, scale: 2);
            history.Add("@DOH_PozyczkaNr", "");
            history.Add("@DOH_Opis", "");
            history.Add("@DOH_OddelegowanyWaluta", "");
            history.Add("@DOH_PlatnoscZgodnaPrac", 1);
            history.Add("@ZrdId", "AMBIT");
            history.Add("@OpeId", null, DbType.Int32);
            history.Add("@IdParam", dbType: DbType.Int32, direction: ParameterDirection.Output);
            history.Add(
                "@Warnings",
                dbType: DbType.String,
                direction: ParameterDirection.Output,
                size: 4000
            );
            await connection.ExecuteAsync(
                Procedure("CDN.ImportDataDodatkiHist", history, transaction, cancellationToken)
            );

            await transaction.CommitAsync(cancellationToken);
            return new Outcome(
                "Succeeded",
                OptimaEmployeeId: employeeRowId,
                OptimaAdditionId: additionId,
                WarningCodes: warningCodes
            );
        }
        catch (SqlException exception)
        {
            // Only the numbers are kept: the message can carry Optima's data.
            await SqlEmployeeWriter.RollBackAsync(transaction);
            return new Outcome(
                "Failed",
                OptimaEmployeeId: employeeRowId,
                ErrorNumber: exception.Number,
                ErrorProcedure: exception.Procedure is "" ? null : exception.Procedure,
                ErrorLine: exception.LineNumber
            );
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private static DbString Ansi(string value) =>
        new()
        {
            Value = value,
            IsAnsi = true,
            Length = 36,
        };

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

    private CommandDefinition Procedure(
        string name,
        DynamicParameters parameters,
        SqlTransaction transaction,
        CancellationToken cancellationToken
    ) =>
        new(
            name,
            parameters,
            transaction,
            connections.CommandTimeout,
            CommandType.StoredProcedure,
            cancellationToken: cancellationToken
        );

    private sealed record WrittenDeduction(int AdditionId, int EmployeeRowId);

    private sealed record EmployeeMatch(int Id, string? ImportRowId);

    private sealed record Component(string Name, string? ImportRowId, string? ImportRowId2);

    private sealed record Outcome(
        string Status,
        int? OptimaEmployeeId = null,
        int? OptimaAdditionId = null,
        string? WarningCodes = null,
        int? ErrorNumber = null,
        string? ErrorProcedure = null,
        int? ErrorLine = null
    );
}
