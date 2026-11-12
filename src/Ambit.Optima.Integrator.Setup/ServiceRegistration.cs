using System.Diagnostics;
using System.IO.Pipes;
using Ambit.Optima.Integrator.Ipc;

namespace Ambit.Optima.Integrator.Setup;

/// <summary>Registers the Worker as a Windows Service under its own virtual account, and proves that it answers.</summary>
public static class ServiceRegistration
{
    private const string Name = "AmbitOptimaIntegrator";
    private const int ServiceDoesNotExist = 1060;
    private const int ServiceAlreadyRunning = 1056;

    public const string Login = @"NT SERVICE\" + Name;

    public static async Task RegisterAsync(string appDirectory, CancellationToken cancellationToken)
    {
        var binary = Path.Combine(appDirectory, "Ambit.Optima.Integrator.Worker.exe");
        if (!File.Exists(binary))
            throw new InvalidOperationException($"{binary} is missing.");

        var exists = await ScAsync($"query {Name}", cancellationToken) != ServiceDoesNotExist;
        var verb = exists ? "config" : "create";
        await RequireAsync(
            $"{verb} {Name} binPath= \"\\\"{binary}\\\"\" start= auto obj= \"{Login}\" DisplayName= \"Ambit Optima Integrator\"",
            cancellationToken
        );
        await RequireAsync(
            $"failure {Name} reset= 86400 actions= restart/60000/restart/60000//",
            cancellationToken
        );
    }

    public static async Task StartAsync(CancellationToken cancellationToken)
    {
        var exitCode = await ScAsync($"start {Name}", cancellationToken);
        if (exitCode is not (0 or ServiceAlreadyRunning))
            throw new InvalidOperationException($"sc start failed with code {exitCode}.");
    }

    /// <summary>The service answers on its pipe and reaches SQL Server as itself, not as whoever runs the installer.</summary>
    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        await using var pipe = new NamedPipeClientStream(
            ".",
            IpcEndpoint.DefaultPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        await pipe.ConnectAsync(timeout.Token);
        await IpcFraming.WriteAsync(
            pipe,
            new IpcRequest(IpcFraming.CurrentVersion, IpcCommands.SqlIdentity),
            timeout.Token
        );
        var response = await IpcFraming.ReadAsync<IpcResponse>(pipe, timeout.Token);

        if (response is not { Ok: true, Data: { } data })
            throw new InvalidOperationException(
                $"The service cannot reach SQL Server: {response?.Error ?? "no answer"}."
            );
        var login = data.GetProperty("loginName").GetString();
        if (login != Login)
            throw new InvalidOperationException($"The service signed in to SQL Server as {login}.");
        Console.WriteLine($"The service answers and signs in as {login}.");
    }

    private static async Task RequireAsync(string arguments, CancellationToken cancellationToken)
    {
        var exitCode = await ScAsync(arguments, cancellationToken);
        if (exitCode != 0)
            throw new InvalidOperationException($"sc {arguments} failed with code {exitCode}.");
    }

    private static async Task<int> ScAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process =
            Process.Start(
                new ProcessStartInfo("sc.exe", arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            ) ?? throw new InvalidOperationException("sc.exe did not start.");
        await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
