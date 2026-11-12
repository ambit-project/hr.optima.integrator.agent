using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ambit.Optima.Integrator.Ipc;

/// <summary>
/// A request names a command and may carry that command's typed values. There is no field for SQL text,
/// so no client has a way of asking the Worker to run a statement of its choosing.
/// </summary>
public sealed record IpcRequest(int Version, string Command, JsonElement? Payload = null);

public sealed record IpcResponse(bool Ok, JsonElement? Data = null, string? Error = null);

public static class IpcCommands
{
    public const string Status = "status";
    public const string SqlIdentity = "sql.identity";
    public const string EmployeeAdd = "employee.add";
}

public static class IpcErrors
{
    public const string UnknownCommand = "unknown_command";
    public const string UnsupportedVersion = "unsupported_version";
    public const string SqlUnavailable = "sql_unavailable";
    public const string WritesDisabled = "writes_disabled";
    public const string InvalidPayload = "invalid_payload";
    public const string EmployeeRejected = "employee_rejected";
}

/// <summary>One JSON message on the wire: a 4-byte little-endian length, then UTF-8.</summary>
public static class IpcFraming
{
    public const int CurrentVersion = 1;
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken
    )
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxMessageBytes)
            throw new InvalidDataException("The IPC message is too long.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>The next message, or null when the other side closed between messages.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        where T : class
    {
        var header = new byte[sizeof(int)];
        var read = await stream.ReadAtLeastAsync(
            header,
            header.Length,
            throwOnEndOfStream: false,
            cancellationToken
        );
        if (read == 0)
            return null;
        if (read < header.Length)
            throw new EndOfStreamException("The IPC message ended inside its header.");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
            throw new InvalidDataException("The IPC message has an invalid length.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, Json);
    }
}
