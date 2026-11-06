using System.Data;
using Ambit.Agent.Core.Employees;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.SqlServer;

public sealed class SqlEmployeeWriter(IOptions<SqlServerOptions> options) : IEmployeeWriter
{
    private const int FirstRejectionNumber = 51000;
    private const int LastRejectionNumber = 51999;
    private const int CountryCodeLength = 2;
    private const int InvalidCountryCode = 51015;

    public async Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
        EmployeeAddRequest request,
        CancellationToken cancellationToken
    )
    {
        // These two are char(2) in the procedure, where a longer value would be cut without an error.
        if (
            request.CitizenshipCode is { Length: not CountryCodeLength }
            || request.AddressCountryCode is { Length: not CountryCodeLength }
        )
            throw new EmployeeRejectedException(
                InvalidCountryCode,
                "A country code must have two letters."
            );

        var settings = options.Value;
        await using var connection = new SqlConnection(settings.ToAgentConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("app.employee_add", connection);
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = settings.EmployeeAddTimeoutSeconds;
        AddParameters(command.Parameters, request);

        try
        {
            return await ReadResultsAsync(command, cancellationToken);
        }
        catch (SqlException exception)
            when (exception.Number is >= FirstRejectionNumber and <= LastRejectionNumber)
        {
            throw new EmployeeRejectedException(exception.Number, exception.Message);
        }
    }

    private static void AddParameters(SqlParameterCollection parameters, EmployeeAddRequest request)
    {
        // A value left out takes the procedure's default, which a NULL sent explicitly would not.
        Add(parameters, "@operation_id", SqlDbType.UniqueIdentifier, request.OperationId);
        Add(parameters, "@employee_id", SqlDbType.UniqueIdentifier, request.EmployeeId);
        Add(parameters, "@employee_code", SqlDbType.NVarChar, request.EmployeeCode);
        Add(parameters, "@last_name", SqlDbType.NVarChar, request.LastName);
        Add(parameters, "@first_name", SqlDbType.NVarChar, request.FirstName);
        Add(parameters, "@database_name", SqlDbType.NVarChar, request.DatabaseName);
        Add(parameters, "@center_id", SqlDbType.Int, request.CenterId);
        Add(parameters, "@operator_id", SqlDbType.Int, request.OperatorId);
        Add(parameters, "@date_of_birth", SqlDbType.Date, request.DateOfBirth);
        Add(parameters, "@pesel", SqlDbType.NVarChar, request.Pesel);
        Add(parameters, "@citizenship_code", SqlDbType.NVarChar, request.CitizenshipCode);
        Add(parameters, "@email", SqlDbType.NVarChar, request.Email);
        Add(parameters, "@phone", SqlDbType.NVarChar, request.Phone);
        Add(parameters, "@street", SqlDbType.NVarChar, request.Street);
        Add(parameters, "@building_number", SqlDbType.NVarChar, request.BuildingNumber);
        Add(parameters, "@unit_number", SqlDbType.NVarChar, request.UnitNumber);
        Add(parameters, "@postal_code", SqlDbType.NVarChar, request.PostalCode);
        Add(parameters, "@city", SqlDbType.NVarChar, request.City);
        Add(parameters, "@address_country_code", SqlDbType.NVarChar, request.AddressCountryCode);
        Add(parameters, "@note", SqlDbType.NVarChar, request.Note);
        Add(parameters, "@document_kind", SqlDbType.NVarChar, request.DocumentKind);
        Add(parameters, "@document_number", SqlDbType.NVarChar, request.DocumentNumber);
        Add(parameters, "@document_valid_until", SqlDbType.Date, request.DocumentValidUntil);
        Add(parameters, "@position_name", SqlDbType.NVarChar, request.PositionName);
        Add(parameters, "@starts_on", SqlDbType.Date, request.StartsOn);
        Add(parameters, "@ends_on", SqlDbType.Date, request.EndsOn);
    }

    private static void Add(
        SqlParameterCollection parameters,
        string name,
        SqlDbType type,
        object? value
    )
    {
        if (value is null)
            return;

        var parameter = parameters.Add(name, type);
        // Unbounded, so that a long value reaches the procedure's length check instead of being cut here.
        if (type == SqlDbType.NVarChar)
            parameter.Size = -1;
        parameter.Value = value;
    }

    private static async Task<IReadOnlyList<EmployeeAddResult>> ReadResultsAsync(
        SqlCommand command,
        CancellationToken cancellationToken
    )
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<EmployeeAddResult>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(
                new EmployeeAddResult(
                    reader.GetString(reader.GetOrdinal("database_name")),
                    reader.GetString(reader.GetOrdinal("status")),
                    reader.GetInt32(reader.GetOrdinal("attempt")),
                    NullableInt(reader, "optima_employee_id"),
                    NullableInt(reader, "optima_contract_id"),
                    NullableString(reader, "warning_codes"),
                    NullableInt(reader, "error_number"),
                    NullableString(reader, "error_procedure"),
                    NullableString(reader, "optima_error_codes")
                )
            );
        return results;
    }

    private static int? NullableInt(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static string? NullableString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
