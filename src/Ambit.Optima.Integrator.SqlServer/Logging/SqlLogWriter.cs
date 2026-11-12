using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.SqlServer.Logging;

/// <summary>Moves log entries from the channel to the agent's database.</summary>
public sealed class SqlLogWriter(
    SqlLogChannel channel,
    IOptions<SqlServerOptions> options,
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
            await using var connection = new SqlConnection(options.Value.ToAgentConnectionString());
            await connection.OpenAsync(cancellationToken);

            while (channel.Reader.TryRead(out var entry))
                await WriteAsync(connection, entry, cancellationToken);

            return true;
        }
        catch (SqlException exception)
        {
            logger.LogWarning(
                "Writing to the agent's log failed with SQL error {Number}.",
                exception.Number
            );
            return false;
        }
    }

    private static async Task WriteAsync(
        SqlConnection connection,
        SqlLogEntry entry,
        CancellationToken cancellationToken
    )
    {
        await using var command = new SqlCommand("app.log_write", connection);
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@level", entry.Level);
        command.Parameters.AddWithValue("@category", entry.Category);
        command.Parameters.AddWithValue("@message", entry.Message);
        command.Parameters.AddWithValue(
            "@exception_type",
            (object?)entry.ExceptionType ?? DBNull.Value
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
