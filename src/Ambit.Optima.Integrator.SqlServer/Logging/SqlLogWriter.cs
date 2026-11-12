using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ambit.Optima.Integrator.SqlServer.Logging;

/// <summary>Moves log entries from the channel to the integrator's database.</summary>
public sealed class SqlLogWriter(
    SqlLogChannel channel,
    SqlConnections connections,
    ILogger<SqlLogWriter> logger
) : BackgroundService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FlushAtStop = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(stoppingToken))
                if (!await TryWriteAvailableAsync(stoppingToken))
                    await Task.Delay(RetryAfter, stoppingToken);
        }
        catch (OperationCanceledException) { }

        using var flush = new CancellationTokenSource(FlushAtStop);
        try
        {
            await TryWriteAvailableAsync(flush.Token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> TryWriteAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connections.OpenIntegratorAsync(cancellationToken);

            while (channel.Reader.TryRead(out var entry))
                await WriteAsync(connection, entry, cancellationToken);

            return true;
        }
        catch (SqlException exception)
        {
            logger.LogWarning(
                "Writing to the integrator's log failed with SQL error {Number}.",
                exception.Number
            );
            return false;
        }
    }

    private static async Task WriteAsync(
        SqlConnection connection,
        SqlLogEntry entry,
        CancellationToken cancellationToken
    ) =>
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT integrator.log_entry (level, category, message, exception_type)
                VALUES (@Level, @Category, @Message, @ExceptionType);
                """,
                entry,
                cancellationToken: cancellationToken
            )
        );
}
