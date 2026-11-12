using System.Data;
using Ambit.Optima.Integrator.Core.Deductions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.SqlServer;

public sealed class SqlDeductionWriter(IOptions<SqlServerOptions> options) : IDeductionWriter
{
    private const int FirstRejectionNumber = 51000;
    private const int LastRejectionNumber = 51999;
    private const int InvalidRequest = 51020;
    private const int InvalidAmount = 51024;

    public async Task<DeductionAddResult> AddAsync(
        DeductionAddRequest request,
        CancellationToken cancellationToken
    )
    {
        var deduction = request.Deduction;
        if (deduction.DeductionId is not { } deductionId)
            throw new DeductionRejectedException(InvalidRequest, "deductionId is required.");

        // The parameter keeps six decimals, so a value with more would be rounded on the way instead of refused.
        if (deduction.Amount is { } amount && decimal.Round(amount, 2) != amount)
            throw new DeductionRejectedException(
                InvalidAmount,
                "amount must be positive, with at most two decimals."
            );

        var settings = options.Value;
        await using var connection = new SqlConnection(settings.ToAgentConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("app.deduction_add", connection);
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        var parameters = command.Parameters;
        parameters.Add("@operation_id", SqlDbType.UniqueIdentifier).Value = request.OperationId;
        parameters.Add("@deduction_id", SqlDbType.UniqueIdentifier).Value = deductionId;
        parameters.Add("@optima_key", SqlDbType.UniqueIdentifier).Value = DeductionItem.OptimaKey(
            deductionId,
            request.Year,
            request.Month
        );
        parameters.Add("@database_name", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
        parameters.Add("@employee_id", SqlDbType.UniqueIdentifier).Value =
            (object?)deduction.EmployeeId ?? DBNull.Value;
        // Unbounded, so that a long value reaches the procedure's length check instead of being cut here.
        parameters.Add("@employee_code", SqlDbType.NVarChar, -1).Value =
            (object?)deduction.EmployeeCode ?? DBNull.Value;
        parameters.Add("@optima_component_id", SqlDbType.Int).Value =
            (object?)deduction.OptimaId ?? DBNull.Value;
        parameters.Add("@year", SqlDbType.Int).Value = request.Year;
        parameters.Add("@month", SqlDbType.Int).Value = request.Month;
        var amountParameter = parameters.Add("@amount", SqlDbType.Decimal);
        amountParameter.Precision = 38;
        amountParameter.Scale = 6;
        amountParameter.Value = (object?)deduction.Amount ?? DBNull.Value;
        parameters.Add("@currency", SqlDbType.NVarChar, -1).Value =
            (object?)deduction.Currency ?? DBNull.Value;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("app.deduction_add returned no row.");

            return new DeductionAddResult(
                reader.GetString(reader.GetOrdinal("database_name")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetInt32(reader.GetOrdinal("attempt")),
                NullableInt(reader, "optima_employee_id"),
                NullableInt(reader, "optima_addition_id"),
                NullableString(reader, "warning_codes"),
                NullableInt(reader, "error_number"),
                NullableString(reader, "error_procedure")
            );
        }
        catch (SqlException exception)
            when (exception.Number is >= FirstRejectionNumber and <= LastRejectionNumber)
        {
            throw new DeductionRejectedException(exception.Number, exception.Message);
        }
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
