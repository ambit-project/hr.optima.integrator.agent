using System.Buffers.Binary;
using Ambit.Optima.Integrator.Ipc;

namespace Ambit.Optima.Integrator.UnitTests;

public sealed class IpcFramingTests
{
    [Fact]
    public async Task A_message_survives_the_round_trip()
    {
        using var stream = new MemoryStream();

        await IpcFraming.WriteAsync(
            stream,
            new IpcRequest(1, IpcCommands.Status),
            CancellationToken.None
        );
        stream.Position = 0;
        var read = await IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None);

        Assert.Equal(new IpcRequest(1, IpcCommands.Status), read);
    }

    [Fact]
    public async Task A_closed_stream_reads_as_no_message()
    {
        using var stream = new MemoryStream();

        Assert.Null(await IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IpcFraming.MaxMessageBytes + 1)]
    public async Task An_invalid_length_is_refused(int length)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None)
        );
    }

    [Fact]
    public async Task A_truncated_header_is_refused()
    {
        using var stream = new MemoryStream([1, 0]);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            IpcFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None)
        );
    }
}
