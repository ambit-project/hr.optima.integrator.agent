using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.Ipc;

public sealed class IpcServer(
    IOptions<IpcEndpoint> endpoint,
    IpcDispatcher dispatcher,
    ILogger<IpcServer> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var listener = endpoint.Value.CreateListener();
        logger.LogInformation("IPC is listening.");

        while (!stoppingToken.IsCancellationRequested)
        {
            Stream client;
            try
            {
                client = await listener.AcceptAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = ServeAsync(client, stoppingToken);
        }
    }

    private async Task ServeAsync(Stream client, CancellationToken cancellationToken)
    {
        await using (client)
        {
            try
            {
                while (
                    await IpcFraming.ReadAsync<IpcRequest>(client, cancellationToken) is { } request
                )
                {
                    var response = await dispatcher.HandleAsync(request, cancellationToken);
                    await IpcFraming.WriteAsync(client, response, cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
                when (exception
                        is IOException
                            or InvalidDataException
                            or System.Text.Json.JsonException
                )
            {
                // A bad client ends its own connection, never the Worker.
                logger.LogWarning(exception, "An IPC client was disconnected.");
            }
        }
    }
}
