using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Ambit.Agent.Ipc;

/// <summary>Accepts one client at a time; each returned stream belongs to the caller.</summary>
public interface IIpcListener : IAsyncDisposable
{
    Task<Stream> AcceptAsync(CancellationToken cancellationToken);
}

public sealed class IpcEndpoint
{
    public const string SectionName = "Ipc";
    public const string DefaultPipeName = "ambit-optima-agent";

    public string PipeName { get; init; } = DefaultPipeName;

    /// <summary>Linux only (development): where the socket lives.</summary>
    public string? SocketPath { get; init; }

    public string ResolveSocketPath() =>
        SocketPath
        ?? Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
            $"{PipeName}.sock"
        );

    public IIpcListener CreateListener() =>
        OperatingSystem.IsWindows()
            ? new NamedPipeListener(PipeName)
            : new UnixSocketListener(ResolveSocketPath());
}

[SupportedOSPlatform("windows")]
internal sealed class NamedPipeListener(string pipeName) : IIpcListener
{
    private const int MaxInstances = 4;
    private const int BufferBytes = 64 * 1024;

    private bool _firstInstance = true;

    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        // FirstPipeInstance on the first one: if another process already holds the name, the Worker
        // fails loudly at startup rather than sharing the pipe with an impostor.
        var firstFlag = _firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None;
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | firstFlag,
            BufferBytes,
            BufferBytes,
            CreateSecurity()
        );
        _firstInstance = false;

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    /// <summary>Signed-in local users may talk to the Worker; nobody may reach it over the network.</summary>
    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        using var self = WindowsIdentity.GetCurrent();

        security.AddAccessRule(
            new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow)
        );
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow
            )
        );
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Deny
            )
        );
        return security;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Development on Linux: a socket file only its owner can open.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixSocketListener : IIpcListener
{
    private readonly Socket _socket;
    private readonly string _path;

    public UnixSocketListener(string path)
    {
        _path = path;
        File.Delete(path);

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _socket.Listen();
    }

    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var client = await _socket.AcceptAsync(cancellationToken);
        return new NetworkStream(client, ownsSocket: true);
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }
}
