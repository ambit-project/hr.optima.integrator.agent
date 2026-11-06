using Ambit.Agent.Core.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.Infrastructure.AmbitApi;

/// <summary>
/// Asks Ambit for work, so nothing opens a port on the client's machine. An operation runs once. Only sending its
/// result is repeated, because Ambit ignores a result it already has.
/// </summary>
public sealed class OperationPoller(
    IAmbitApiClient api,
    OperationRunner runner,
    IOptions<AmbitApiOptions> options,
    TimeProvider clock,
    ILogger<OperationPoller> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // After an operation the next one is asked for at once; only an empty queue waits.
                if (!await RunNextAsync(stoppingToken))
                    await Task.Delay(
                        TimeSpan.FromSeconds(options.Value.PollSeconds),
                        clock,
                        stoppingToken
                    );
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>True when an operation was run.</summary>
    public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        AgentOperation? operation;
        try
        {
            operation = await api.ClaimAsync(cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception, cancellationToken))
        {
            logger.LogWarning("Asking Ambit for work failed: {Error}.", exception.GetType().Name);
            return false;
        }

        if (operation is null)
            return false;

        logger.LogInformation(
            "Running operation {OperationId} ({Kind}), attempt {Attempt}.",
            operation.Id,
            operation.Kind,
            operation.Attempt
        );
        var result = await runner.RunAsync(operation, cancellationToken);
        await ReportAsync(operation, result, cancellationToken);
        return true;
    }

    private async Task ReportAsync(
        AgentOperation operation,
        OperationResult result,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            try
            {
                if (!await api.ReportAsync(operation.Id, result, cancellationToken))
                    logger.LogWarning(
                        "Ambit refused the result of operation {OperationId}.",
                        operation.Id
                    );
                return;
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                logger.LogWarning(
                    "Sending the result of operation {OperationId} failed: {Error}.",
                    operation.Id,
                    exception.GetType().Name
                );
            }

            await Task.Delay(
                TimeSpan.FromSeconds(options.Value.RetrySeconds),
                clock,
                cancellationToken
            );
        }
    }

    // A request timeout arrives as a cancellation too, but not of the token the service stops with.
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}
