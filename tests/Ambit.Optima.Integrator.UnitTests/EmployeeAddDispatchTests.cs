using System.Text.Json;
using Ambit.Optima.Integrator.Core.Diagnostics;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.UnitTests;

public sealed class EmployeeAddDispatchTests
{
    private const string Valid = """
        {"operationId":"a0000000-0000-4000-8000-000000000001",
         "employeeId":"11111111-1111-4111-8111-111111111111",
         "employeeCode":"AK1","lastName":"L","firstName":"F",
         "dateOfBirth":"1990-05-20"}
        """;

    private sealed class NoSql : ISqlDiagnostics
    {
        public Task<SqlIdentity> GetIdentityAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class FakeEmployees(Exception? failure = null) : IEmployeeWriter
    {
        public EmployeeAddRequest? Received { get; private set; }

        public Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
            EmployeeAddRequest request,
            CancellationToken cancellationToken
        )
        {
            Received = request;
            if (failure is not null)
                throw failure;

            return Task.FromResult<IReadOnlyList<EmployeeAddResult>>([
                new EmployeeAddResult("CDN_A", "Succeeded", 1, 2, 3, null, null, null, null),
            ]);
        }
    }

    private static IpcDispatcher Dispatcher(FakeEmployees employees, bool writes = true) =>
        new(
            new NoSql(),
            employees,
            Options.Create(new WriteOptions { Enabled = writes }),
            TimeProvider.System,
            NullLogger<IpcDispatcher>.Instance
        );

    private static IpcRequest Request(string json) =>
        new(
            IpcFraming.CurrentVersion,
            IpcCommands.EmployeeAdd,
            JsonDocument.Parse(json).RootElement
        );

    [Fact]
    public async Task Writes_are_refused_unless_switched_on_locally()
    {
        var employees = new FakeEmployees();

        var response = await Dispatcher(employees, writes: false)
            .HandleAsync(Request(Valid), CancellationToken.None);

        Assert.Equal(IpcErrors.WritesDisabled, response.Error);
        Assert.Null(employees.Received);
    }

    [Fact]
    public async Task A_request_reaches_the_writer_and_each_company_gets_a_result()
    {
        var employees = new FakeEmployees();

        var response = await Dispatcher(employees)
            .HandleAsync(Request(Valid), CancellationToken.None);

        Assert.True(response.Ok, response.Error);
        Assert.Equal("AK1", employees.Received!.EmployeeCode);
        Assert.Equal(new DateOnly(1990, 5, 20), employees.Received.DateOfBirth);
        var company = response.Data!.Value.GetProperty("results")[0];
        Assert.Equal("CDN_A", company.GetProperty("databaseName").GetString());
        Assert.Equal("Succeeded", company.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""{"dateOfBirth":"not a date 12345678901"}""")]
    [InlineData("null")]
    public async Task A_payload_that_cannot_be_read_is_refused_without_repeating_it(string json)
    {
        var response = await Dispatcher(new FakeEmployees())
            .HandleAsync(Request(json), CancellationToken.None);

        Assert.Equal(IpcErrors.InvalidPayload, response.Error);
    }

    [Fact]
    public async Task No_payload_is_refused()
    {
        var response = await Dispatcher(new FakeEmployees())
            .HandleAsync(
                new IpcRequest(IpcFraming.CurrentVersion, IpcCommands.EmployeeAdd),
                CancellationToken.None
            );

        Assert.Equal(IpcErrors.InvalidPayload, response.Error);
    }

    [Fact]
    public async Task A_rejection_names_its_number_and_its_reason()
    {
        var employees = new FakeEmployees(new EmployeeRejectedException(51014, "Too long: email."));

        var response = await Dispatcher(employees)
            .HandleAsync(Request(Valid), CancellationToken.None);

        Assert.Equal($"{IpcErrors.EmployeeRejected}:51014:Too long: email.", response.Error);
    }

    [Fact]
    public async Task Another_failure_returns_only_its_type()
    {
        var employees = new FakeEmployees(new InvalidOperationException("PESEL 90052012345"));

        var response = await Dispatcher(employees)
            .HandleAsync(Request(Valid), CancellationToken.None);

        Assert.Equal(
            $"{IpcErrors.SqlUnavailable}:{nameof(InvalidOperationException)}",
            response.Error
        );
    }

    [Fact]
    public void Printing_a_request_shows_no_personal_data()
    {
        var request = new EmployeeAddRequest(
            null,
            null,
            "AK1",
            "Nowak",
            "Anna",
            Pesel: "90052012345"
        );

        Assert.Equal(nameof(EmployeeAddRequest), request.ToString());
    }
}
