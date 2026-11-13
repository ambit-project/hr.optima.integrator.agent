using Ambit.Optima.Integrator.Core.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.Infrastructure.AmbitApi;

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
                var wait = Wait(await RunNextAsync(stoppingToken));
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// After an operation the next one is asked for at once and an empty queue waits the short poll interval. When
    /// Ambit could not be reached or refused the token, the agent waits the longer retry time, so an API that is down
    /// is not asked twice a second.
    /// </summary>
    private TimeSpan Wait(PollOutcome outcome) =>
        outcome switch
        {
            PollOutcome.Ran => TimeSpan.Zero,
            PollOutcome.Idle => TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds),
            _ => TimeSpan.FromSeconds(options.Value.RetrySeconds),
        };

    public async Task<PollOutcome> RunNextAsync(CancellationToken cancellationToken)
    {
        AgentOperation? operation;
        try
        {
            operation = await api.ClaimAsync(cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception, cancellationToken))
        {
            logger.LogWarning("Asking Ambit for work failed: {Error}.", exception.GetType().Name);
            return PollOutcome.Unreachable;
        }

        if (operation is null)
            return PollOutcome.Idle;

        logger.LogInformation(
            "Running operation {OperationId} ({Kind}), attempt {Attempt}.",
            operation.Id,
            operation.Kind,
            operation.Attempt
        );
        var result = await runner.RunAsync(operation, cancellationToken);
        await ReportAsync(operation, result, cancellationToken);
        return PollOutcome.Ran;
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

public enum PollOutcome
{
    /// <summary>An operation was run and reported.</summary>
    Ran,

    /// <summary>Nothing waited.</summary>
    Idle,

    /// <summary>Ambit could not be reached, timed out or refused the token (401).</summary>
    Unreachable,
}
