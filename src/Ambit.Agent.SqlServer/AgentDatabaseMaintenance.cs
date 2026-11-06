using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.SqlServer;

/// <summary>
/// At startup marks the calls the previous run left unfinished, so ADR 0001's "a write becomes Unknown"
/// holds, and then applies the retention once a day. It assumes one Worker per instance.
/// </summary>
public sealed class AgentDatabaseMaintenance(
    IOptions<SqlServerOptions> options,
    ILogger<AgentDatabaseMaintenance> logger
) : BackgroundService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!await TryRunAsync("app.employee_call_mark_interrupted", null, stoppingToken))
                await Task.Delay(RetryAfter, stoppingToken);

            do
            {
                await TryRunAsync("app.log_purge", options.Value.LogRetentionDays, stoppingToken);
                await Task.Delay(PurgeEvery, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> TryRunAsync(
        string procedure,
        int? retentionDays,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var connection = new SqlConnection(options.Value.ToAgentConnectionString());
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(procedure, connection);
            command.CommandType = CommandType.StoredProcedure;
            if (retentionDays is { } days)
                command.Parameters.AddWithValue("@retention_days", days);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqlException exception)
        {
            logger.LogWarning(
                "{Procedure} failed with SQL error {Number}.",
                procedure,
                exception.Number
            );
            return false;
        }
    }
}
