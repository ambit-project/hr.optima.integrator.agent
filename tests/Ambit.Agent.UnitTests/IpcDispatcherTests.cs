using Ambit.Agent.Core.Diagnostics;
using Ambit.Agent.Core.Employees;
using Ambit.Agent.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.UnitTests;

public sealed class IpcDispatcherTests
{
    private sealed class FakeSql(Exception? failure = null) : ISqlDiagnostics
    {
        public Task<SqlIdentity> GetIdentityAsync(CancellationToken cancellationToken) =>
            failure is null
                ? Task.FromResult(
                    new SqlIdentity(
                        @"NT SERVICE\Agent",
                        @"NT SERVICE\Agent",
                        "CDN",
                        "S",
                        false,
                        true
                    )
                )
                : Task.FromException<SqlIdentity>(failure);
    }

    private sealed class FakeEmployees : IEmployeeWriter
    {
        public Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
            EmployeeAddRequest request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Writes are off in these tests.");
    }

    private static IpcDispatcher Dispatcher(Exception? failure = null) =>
        new(
            new FakeSql(failure),
            new FakeEmployees(),
            Options.Create(new WriteOptions()),
            TimeProvider.System,
            NullLogger<IpcDispatcher>.Instance
        );

    [Theory]
    [InlineData("SELECT * FROM CDN.Kontrahenci")]
    [InlineData("sql.execute")]
    [InlineData("")]
    public async Task Anything_outside_the_list_of_commands_is_refused(string command)
    {
        var response = await Dispatcher()
            .HandleAsync(
                new IpcRequest(IpcFraming.CurrentVersion, command),
                CancellationToken.None
            );

        Assert.False(response.Ok);
        Assert.Equal(IpcErrors.UnknownCommand, response.Error);
    }

    [Fact]
    public async Task Another_protocol_version_is_refused()
    {
        var response = await Dispatcher()
            .HandleAsync(new IpcRequest(99, IpcCommands.Status), CancellationToken.None);

        Assert.Equal(IpcErrors.UnsupportedVersion, response.Error);
    }

    [Fact]
    public async Task The_identity_says_when_the_account_has_too_much()
    {
        var response = await Dispatcher()
            .HandleAsync(
                new IpcRequest(IpcFraming.CurrentVersion, IpcCommands.SqlIdentity),
                CancellationToken.None
            );

        Assert.True(response.Ok);
        Assert.True(response.Data!.Value.GetProperty("isOverprivileged").GetBoolean());
    }

    [Fact]
    public async Task A_failed_check_does_not_send_its_message_to_the_client()
    {
        var response = await Dispatcher(new InvalidOperationException("Server=secret-host"))
            .HandleAsync(
                new IpcRequest(IpcFraming.CurrentVersion, IpcCommands.SqlIdentity),
                CancellationToken.None
            );

        Assert.False(response.Ok);
        Assert.Equal($"{IpcErrors.SqlUnavailable}:InvalidOperationException", response.Error);
    }
}
