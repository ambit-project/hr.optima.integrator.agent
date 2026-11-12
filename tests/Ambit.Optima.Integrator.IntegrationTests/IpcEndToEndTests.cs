using System.IO.Pipes;
using System.Net.Sockets;
using Ambit.Optima.Integrator.Core.Diagnostics;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.IntegrationTests;

/// <summary>The real transport of the platform the test runs on: a named pipe or a Unix socket.</summary>
public sealed class IpcEndToEndTests
{
    private sealed class NoSql : ISqlDiagnostics
    {
        public Task<SqlIdentity> GetIdentityAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No SQL Server here.");
    }

    private sealed class NoEmployees : IEmployeeWriter
    {
        public Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
            EmployeeAddRequest request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("No SQL Server here.");
    }

    [Fact]
    public async Task A_client_gets_the_status_and_can_ask_again_on_the_same_connection()
    {
        var name = $"ambit-test-{Guid.NewGuid():N}";
        var endpoint = new IpcEndpoint
        {
            PipeName = name,
            SocketPath = OperatingSystem.IsWindows()
                ? null
                : Path.Combine(Path.GetTempPath(), $"{name}.sock"),
        };
        var server = new IpcServer(
            Options.Create(endpoint),
            new IpcDispatcher(
                new NoSql(),
                new NoEmployees(),
                Options.Create(new WriteOptions()),
                TimeProvider.System,
                NullLogger<IpcDispatcher>.Instance
            ),
            NullLogger<IpcServer>.Instance
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await server.StartAsync(timeout.Token);
        try
        {
            await using var client = await ConnectAsync(endpoint, timeout.Token);

            foreach (var _ in Enumerable.Range(0, 2))
            {
                await IpcFraming.WriteAsync(
                    client,
                    new IpcRequest(IpcFraming.CurrentVersion, IpcCommands.Status),
                    timeout.Token
                );
                var response = await IpcFraming.ReadAsync<IpcResponse>(client, timeout.Token);

                Assert.True(response!.Ok);
                Assert.Equal("Running", response.Data!.Value.GetProperty("worker").GetString());
            }
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Stream> ConnectAsync(
        IpcEndpoint endpoint,
        CancellationToken cancellationToken
    )
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".",
                endpoint.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }

        var path = endpoint.ResolveSocketPath();
        while (!File.Exists(path))
            await Task.Delay(20, cancellationToken);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
