using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Infrastructure.AmbitApi;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.UnitTests;

/// <summary>The people of an Ambit operation, added one call each, every outcome a log code.</summary>
public sealed class AddEmployeesRunnerTests
{
    private static readonly AgentOperation Add = new(
        Guid.Parse("a0000000-0000-4000-8000-000000000002"),
        OperationKinds.AddEmployees,
        1
    );

    private static readonly Guid Olena = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid Jan = Guid.Parse("b0000000-0000-4000-8000-000000000002");

    private static EmployeeAddRequest Person(Guid id, string lastName) =>
        new(null, id, "W" + id.ToString("N")[13..].ToUpperInvariant(), lastName, "Imię");

    private sealed class FakeApi(EmployeesWork? work, Exception? failure = null) : IAmbitApiClient
    {
        public Task<AgentOperation?> ClaimAsync(CancellationToken cancellationToken) =>
            Task.FromResult<AgentOperation?>(null);

        public Task<EmployeesWork?> GetEmployeesAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => failure is null ? Task.FromResult(work) : throw failure;

        public Task<DeductionsWork?> GetDeductionsAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<DeductionsWork?>(null);

        public Task<bool> ReportAsync(
            Guid operationId,
            OperationResult result,
            CancellationToken cancellationToken
        ) => Task.FromResult(true);
    }

    private sealed class FakeWriter(
        Func<EmployeeAddRequest, IReadOnlyList<EmployeeAddResult>> answer
    ) : IEmployeeWriter
    {
        public List<EmployeeAddRequest> Calls { get; } = [];

        public Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
            EmployeeAddRequest request,
            CancellationToken cancellationToken
        )
        {
            Calls.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class NoReader : IPayrollComponentReader
    {
        public Task<IReadOnlyList<CompanyRead>> ReadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private static EmployeeAddResult Row(string database, string status, int? error = null) =>
        new(database, status, 1, null, null, null, error, null, null);

    private static OperationRunner Runner(
        IAmbitApiClient api,
        IEmployeeWriter writer,
        bool writes = true
    ) =>
        new(
            new NoReader(),
            writer,
            new NoDeductions(),
            api,
            Options.Create(new WriteOptions { Enabled = writes }),
            TimeProvider.System
        );

    [Fact]
    public async Task Each_person_is_one_call_keyed_by_the_operation_and_the_person()
    {
        var writer = new FakeWriter(_ => [Row("CDN_Firma", "Succeeded")]);
        var api = new FakeApi(
            new EmployeesWork("CDN_Firma", [Person(Olena, "Bondarenko"), Person(Jan, "Nowak")])
        );

        var result = await Runner(api, writer).RunAsync(Add, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, writer.Calls.Count);
        Assert.All(writer.Calls, call => Assert.Equal("CDN_Firma", call.DatabaseName));
        Assert.Equal(EmployeesWork.CallId(Add.Id, Olena), writer.Calls[0].OperationId);
        Assert.NotEqual(writer.Calls[0].OperationId, writer.Calls[1].OperationId);
        Assert.Equal(
            [Olena, Jan],
            result
                .Log.Where(e => e.Code == OperationLogCodes.EmployeeAdded)
                .Select(e => e.EmployeeId)
        );
    }

    [Fact]
    public void A_call_id_is_the_same_on_every_attempt_and_a_valid_uuid()
    {
        var first = EmployeesWork.CallId(Add.Id, Olena);

        Assert.Equal(first, EmployeesWork.CallId(Add.Id, Olena));
        Assert.NotEqual(first, EmployeesWork.CallId(Add.Id, Jan));
        Assert.Equal(8, first.Version);
    }

    [Fact]
    public async Task Every_company_answer_becomes_its_own_code()
    {
        var writer = new FakeWriter(_ =>
            [Row("CDN_A", "Succeeded"), Row("CDN_B", "AlreadyExists"), Row("CDN_C", "NoAccess")]
        );
        var api = new FakeApi(new EmployeesWork(null, [Person(Olena, "Bondarenko")]));

        var result = await Runner(api, writer).RunAsync(Add, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                OperationLogCodes.EmployeeAdded,
                OperationLogCodes.EmployeeExists,
                OperationLogCodes.DatabaseNoAccess,
            ],
            result.Log.Select(e => e.Code)
        );
        Assert.Null(writer.Calls.Single().DatabaseName);
    }

    [Fact]
    public async Task A_refused_person_fails_the_operation_but_not_the_others()
    {
        var writer = new FakeWriter(request =>
            request.EmployeeId == Olena
                ? throw new EmployeeRejectedException(51012, "pesel must have 11 digits.")
                : [Row("CDN_Firma", "Succeeded")]
        );
        var api = new FakeApi(
            new EmployeesWork("CDN_Firma", [Person(Olena, "Bondarenko"), Person(Jan, "Nowak")])
        );

        var result = await Runner(api, writer).RunAsync(Add, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(OperationLogCodes.EmployeesFailed, result.ErrorCode);
        var refused = Assert.Single(result.Log, e => e.Code == OperationLogCodes.EmployeeRejected);
        Assert.Equal(51012, refused.ErrorNumber);
        Assert.Equal(Olena, refused.EmployeeId);
        Assert.Contains(
            result.Log,
            e => e.Code == OperationLogCodes.EmployeeAdded && e.EmployeeId == Jan
        );
    }

    [Fact]
    public async Task A_failed_company_is_a_failed_operation()
    {
        var writer = new FakeWriter(_ => [Row("CDN_Firma", "Failed", 2627)]);
        var api = new FakeApi(new EmployeesWork("CDN_Firma", [Person(Olena, "Bondarenko")]));

        var result = await Runner(api, writer).RunAsync(Add, CancellationToken.None);

        Assert.Equal(OperationLogCodes.EmployeesFailed, result.ErrorCode);
        Assert.Equal(2627, Assert.Single(result.Log).ErrorNumber);
    }

    [Fact]
    public async Task No_company_within_reach_fails_the_operation()
    {
        var writer = new FakeWriter(_ => [Row("CDN_Firma", "NoAccess")]);
        var api = new FakeApi(new EmployeesWork("CDN_Firma", [Person(Olena, "Bondarenko")]));

        var result = await Runner(api, writer).RunAsync(Add, CancellationToken.None);

        Assert.Equal(OperationLogCodes.NoCompanyWritable, result.ErrorCode);
    }

    [Fact]
    public async Task An_unexpected_error_on_one_person_is_logged_without_its_message()
    {
        var writer = new FakeWriter(_ => throw new InvalidOperationException("PESEL 90052012345"));
        var api = new FakeApi(new EmployeesWork("CDN_Firma", [Person(Olena, "Bondarenko")]));

        var result = await Runner(api, writer).RunAsync(Add, CancellationToken.None);

        var entry = Assert.Single(result.Log);
        Assert.Equal(OperationLogCodes.EmployeeFailed, entry.Code);
        Assert.Equal(Olena, entry.EmployeeId);
        Assert.Equal(OperationLogCodes.EmployeesFailed, result.ErrorCode);
    }

    [Fact]
    public async Task People_ambit_no_longer_hands_out_end_the_operation()
    {
        var writer = new FakeWriter(_ => []);

        var result = await Runner(new FakeApi(null), writer).RunAsync(Add, CancellationToken.None);

        Assert.Equal(OperationLogCodes.EmployeesUnavailable, result.ErrorCode);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task Ambit_out_of_reach_while_reading_the_people_is_an_execution_failure()
    {
        var writer = new FakeWriter(_ => []);
        var api = new FakeApi(null, new HttpRequestException());

        var result = await Runner(api, writer).RunAsync(Add, CancellationToken.None);

        Assert.Equal(OperationLogCodes.ExecutionFailed, result.ErrorCode);
    }

    [Fact]
    public async Task With_writes_switched_off_nobody_is_written()
    {
        var writer = new FakeWriter(_ => [Row("CDN_Firma", "Succeeded")]);
        var api = new FakeApi(new EmployeesWork("CDN_Firma", [Person(Olena, "Bondarenko")]));

        var result = await Runner(api, writer, writes: false).RunAsync(Add, CancellationToken.None);

        Assert.Equal(OperationLogCodes.WritesDisabled, result.ErrorCode);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public void Writes_off_means_ambit_is_never_offered_the_kind()
    {
        Assert.DoesNotContain(OperationKinds.AddEmployees, OperationKinds.SupportedWith(false));
        Assert.Contains(OperationKinds.AddEmployees, OperationKinds.SupportedWith(true));
    }
}
