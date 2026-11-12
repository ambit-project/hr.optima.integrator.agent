using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>
/// At startup marks the employee calls the previous run left unfinished as <c>Unknown</c>: they may or may not have been
/// written, and are never repeated automatically. Then applies the retention once a day. It assumes one Worker per
/// SQL Server instance.
/// </summary>
public sealed class IntegratorDatabaseMaintenance(
    SqlConnections connections,
    IOptions<SqlServerOptions> options,
    ILogger<IntegratorDatabaseMaintenance> logger
) : BackgroundService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromDays(1);

    private const string MarkInterruptedSql = """
        UPDATE integrator.employee_call
        SET status = 'Unknown', finished_at = SYSUTCDATETIME()
        WHERE status = 'Started';
        """;

    /// <summary>A call still <c>Started</c> is kept, because its outcome is not known yet.</summary>
    private const string PurgeSql = """
        DECLARE @before datetime2(3) = DATEADD(DAY, -@retentionDays, SYSUTCDATETIME());
        DELETE integrator.log_entry WHERE logged_at < @before;
        DELETE integrator.employee_call WHERE started_at < @before AND status <> 'Started';
        DELETE integrator.deduction_call WHERE started_at < @before;
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (
                !await TryRunAsync(
                    "mark interrupted calls",
                    MarkInterruptedSql,
                    null,
                    stoppingToken
                )
            )
                await Task.Delay(RetryAfter, stoppingToken);

            var retentionDays = Math.Max(1, options.Value.LogRetentionDays);
            do
            {
                await TryRunAsync("purge", PurgeSql, new { retentionDays }, stoppingToken);
                await Task.Delay(PurgeEvery, stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> TryRunAsync(
        string task,
        string sql,
        object? parameters,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var connection = await connections.OpenIntegratorAsync(cancellationToken);
            await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    parameters,
                    commandTimeout: connections.CommandTimeout,
                    cancellationToken: cancellationToken
                )
            );
            return true;
        }
        catch (SqlException exception)
        {
            logger.LogWarning(
                "Database maintenance ({Task}) failed with SQL error {Number}.",
                task,
                exception.Number
            );
            return false;
        }
    }
}
