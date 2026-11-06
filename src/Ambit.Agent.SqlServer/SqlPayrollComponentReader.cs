using System.Data;
using Ambit.Agent.Core.Operations;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.SqlServer;

public sealed class SqlPayrollComponentReader(IOptions<SqlServerOptions> options)
    : IPayrollComponentReader
{
    public async Task<IReadOnlyList<CompanyRead>> ReadAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var connection = new SqlConnection(settings.ToAgentConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("app.payroll_components_read", connection);
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = settings.EmployeeAddTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var companies =
            new List<(string Name, CompanyReadStatus Status, string? Tax, int? Error)>();
        while (await reader.ReadAsync(cancellationToken))
            companies.Add(
                (
                    reader.GetString(0),
                    Enum.Parse<CompanyReadStatus>(reader.GetString(1)),
                    await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                    await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt32(3)
                )
            );

        await reader.NextResultAsync(cancellationToken);

        var components = new List<(string Database, PayrollComponent Component)>();
        while (await reader.ReadAsync(cancellationToken))
            components.Add((reader.GetString(0), ReadComponent(reader)));

        return companies
            .Select(company => new CompanyRead(
                company.Name,
                company.Status,
                company.Tax,
                company.Error,
                components
                    .Where(row => row.Database == company.Name)
                    .Select(row => row.Component)
                    .ToList()
            ))
            .ToList();
    }

    private static PayrollComponent ReadComponent(SqlDataReader reader) =>
        new(
            reader.GetInt32(1),
            reader.GetString(2).Trim(),
            reader.IsDBNull(3) ? null : reader.GetString(3).Trim(),
            reader.GetBoolean(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt16(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11)
        );
}
