using System.Text.Json;
using Ambit.Agent.Core.Diagnostics;
using Ambit.Agent.Core.Employees;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.Ipc;

public sealed class IpcDispatcher(
    ISqlDiagnostics sql,
    IEmployeeWriter employees,
    IOptions<WriteOptions> writes,
    TimeProvider clock,
    ILogger<IpcDispatcher> logger
)
{
    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();

    public async Task<IpcResponse> HandleAsync(
        IpcRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Version != IpcFraming.CurrentVersion)
            return new IpcResponse(false, Error: IpcErrors.UnsupportedVersion);

        return request.Command switch
        {
            IpcCommands.Status => Success(
                new
                {
                    worker = "Running",
                    version = typeof(IpcDispatcher).Assembly.GetName().Version?.ToString(),
                    os = Environment.OSVersion.VersionString,
                    startedAt = _startedAt,
                }
            ),
            IpcCommands.SqlIdentity => await SqlIdentityAsync(cancellationToken),
            IpcCommands.EmployeeAdd => await EmployeeAddAsync(request, cancellationToken),
            _ => new IpcResponse(false, Error: IpcErrors.UnknownCommand),
        };
    }

    private async Task<IpcResponse> SqlIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Success(await sql.GetIdentityAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the type goes back: the UI gets a state, and the details stay in the local log.
            logger.LogWarning(exception, "The SQL Server identity check failed.");
            return new IpcResponse(
                false,
                Error: $"{IpcErrors.SqlUnavailable}:{exception.GetType().Name}"
            );
        }
    }

    private async Task<IpcResponse> EmployeeAddAsync(
        IpcRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!writes.Value.Enabled)
            return new IpcResponse(false, Error: IpcErrors.WritesDisabled);

        EmployeeAddRequest? employee;
        try
        {
            employee = request.Payload?.Deserialize<EmployeeAddRequest>(IpcFraming.Json);
        }
        catch (JsonException)
        {
            // Its message can quote the value that did not fit.
            return new IpcResponse(false, Error: IpcErrors.InvalidPayload);
        }
        if (employee is null)
            return new IpcResponse(false, Error: IpcErrors.InvalidPayload);

        try
        {
            var results = await employees.AddAsync(employee, cancellationToken);
            logger.LogInformation(
                "Employee operation {OperationId} visited {Companies} companies.",
                employee.OperationId,
                results.Count
            );
            return Success(new { results });
        }
        catch (EmployeeRejectedException exception)
        {
            logger.LogWarning(
                "Employee operation {OperationId} was rejected with {Number}.",
                employee.OperationId,
                exception.Number
            );
            return new IpcResponse(
                false,
                Error: $"{IpcErrors.EmployeeRejected}:{exception.Number}:{exception.Message}"
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Employee operation {OperationId} failed with {Type}.",
                employee.OperationId,
                exception.GetType().Name
            );
            return new IpcResponse(
                false,
                Error: $"{IpcErrors.SqlUnavailable}:{exception.GetType().Name}"
            );
        }
    }

    private static IpcResponse Success<T>(T data) =>
        new(true, JsonSerializer.SerializeToElement(data, IpcFraming.Json));
}
