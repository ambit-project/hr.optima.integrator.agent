using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Infrastructure.AmbitApi;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.UnitTests;

/// <summary>One month's deductions of one company, one call each, every outcome a log code.</summary>
public sealed class ExportDeductionsRunnerTests
{
    private static readonly AgentOperation Export = new(
        Guid.Parse("a0000000-0000-4000-8000-000000000003"),
        OperationKinds.ExportDeductions,
        2
    );

    private static readonly Guid Housing = Guid.Parse("d0000000-0000-4000-8000-000000000001");
    private static readonly Guid Meals = Guid.Parse("d0000000-0000-4000-8000-000000000002");
    private static readonly Guid Olena = Guid.Parse("b0000000-0000-4000-8000-000000000001");

    private static DeductionItem Item(Guid id, decimal amount = 500m) =>
        new(id, Olena, "BONOLE1", 338, amount, "PLN");

    private static DeductionsWork Month(params DeductionItem[] items) =>
        new("CDN_Firma", 2026, 11, items);

    private sealed class FakeApi(DeductionsWork? work, Exception? failure = null) : IAmbitApiClient
    {
        public Task<AgentOperation?> ClaimAsync(CancellationToken cancellationToken) =>
            Task.FromResult<AgentOperation?>(null);

        public Task<EmployeesWork?> GetEmployeesAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<EmployeesWork?>(null);

        public Task<DeductionsWork?> GetDeductionsAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => failure is null ? Task.FromResult(work) : throw failure;

        public Task<bool> ReportAsync(
            Guid operationId,
            OperationResult result,
            CancellationToken cancellationToken
        ) => Task.FromResult(true);
    }

    private sealed class FakeWriter(Func<DeductionAddRequest, DeductionAddResult> answer)
        : IDeductionWriter
    {
        public List<DeductionAddRequest> Calls { get; } = [];

        public Task<DeductionAddResult> AddAsync(
            DeductionAddRequest request,
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

    private sealed class NoEmployees : IEmployeeWriter
    {
        public Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
            EmployeeAddRequest request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException();
    }

    private static DeductionAddResult Row(
        string status,
        int? error = null,
        string? warnings = null
    ) => new("CDN_Firma", status, 1, 3, 2, warnings, error, null);

    private static OperationRunner Runner(
        IAmbitApiClient api,
        IDeductionWriter writer,
        bool writes = true
    ) =>
        new(
            new NoReader(),
            new NoEmployees(),
            writer,
            api,
            Options.Create(new WriteOptions { Enabled = writes }),
            TimeProvider.System
        );

    [Fact]
    public async Task Each_deduction_is_one_call_for_the_company_and_month()
    {
        var writer = new FakeWriter(_ => Row("Succeeded"));
        var api = new FakeApi(Month(Item(Housing), Item(Meals, 12.5m)));

        var result = await Runner(api, writer).RunAsync(Export, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Attempt);
        Assert.Equal([Housing, Meals], writer.Calls.Select(c => c.Deduction.DeductionId));
        Assert.Equal(
            [(Export.Id, "CDN_Firma", 2026, 11), (Export.Id, "CDN_Firma", 2026, 11)],
            writer.Calls.Select(c => (c.OperationId, c.DatabaseName, c.Year, c.Month))
        );
        Assert.Equal(
            [
                (OperationLogCodes.DeductionAdded, Olena, Housing),
                (OperationLogCodes.DeductionAdded, Olena, Meals),
            ],
            result.Log.Select(e => (e.Code, e.EmployeeId!.Value, e.DeductionId!.Value))
        );
    }

    [Fact]
    public void The_optima_key_depends_on_the_deduction_and_the_month_only()
    {
        var november = DeductionItem.OptimaKey(Housing, 2026, 11);

        Assert.Equal(november, DeductionItem.OptimaKey(Housing, 2026, 11));
        Assert.NotEqual(november, DeductionItem.OptimaKey(Housing, 2026, 12));
        Assert.NotEqual(november, DeductionItem.OptimaKey(Housing, 2027, 11));
        Assert.NotEqual(november, DeductionItem.OptimaKey(Meals, 2026, 11));
        Assert.Equal(8, november.Version);
    }

    [Fact]
    public async Task A_deduction_written_before_counts_as_done()
    {
        var writer = new FakeWriter(request =>
            request.Deduction.DeductionId == Housing ? Row("AlreadyExists") : Row("Succeeded")
        );
        var api = new FakeApi(Month(Item(Housing), Item(Meals)));

        var result = await Runner(api, writer).RunAsync(Export, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [OperationLogCodes.DeductionExists, OperationLogCodes.DeductionAdded],
            result.Log.Select(e => e.Code)
        );
    }

    [Theory]
    [InlineData("EmployeeNotFound", "employee_not_found")]
    [InlineData("ComponentNotFound", "component_not_found")]
    [InlineData("NoAccess", "database_no_access")]
    [InlineData("Failed", "deduction_failed")]
    public async Task Anything_not_written_fails_the_operation(string status, string code)
    {
        var writer = new FakeWriter(request =>
            request.Deduction.DeductionId == Housing ? Row(status, 229) : Row("Succeeded")
        );
        var api = new FakeApi(Month(Item(Housing), Item(Meals)));

        var result = await Runner(api, writer).RunAsync(Export, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(OperationLogCodes.DeductionsFailed, result.ErrorCode);
        Assert.Equal(code, result.Log[0].Code);
        Assert.Equal(229, result.Log[0].ErrorNumber);
        Assert.Equal(OperationLogCodes.DeductionAdded, result.Log[1].Code);
    }

    [Fact]
    public async Task A_refused_deduction_is_logged_with_its_number_and_the_others_go_on()
    {
        var writer = new FakeWriter(request =>
            request.Deduction.DeductionId == Housing
                ? throw new DeductionRejectedException(51024, "amount must be positive.")
                : Row("Succeeded")
        );
        var api = new FakeApi(Month(Item(Housing, -1m), Item(Meals)));

        var result = await Runner(api, writer).RunAsync(Export, CancellationToken.None);

        Assert.Equal(OperationLogCodes.DeductionsFailed, result.ErrorCode);
        var refused = result.Log[0];
        Assert.Equal(OperationLogCodes.DeductionRejected, refused.Code);
        Assert.Equal(51024, refused.ErrorNumber);
        Assert.Equal(Housing, refused.DeductionId);
        Assert.Equal(OperationLogCodes.DeductionAdded, result.Log[1].Code);
    }

    [Fact]
    public async Task A_match_on_the_acronym_alone_is_reported()
    {
        var writer = new FakeWriter(_ => Row("Succeeded", warnings: "MATCHED_BY_ACRONYM_ONLY"));
        var api = new FakeApi(Month(Item(Housing)));

        var result = await Runner(api, writer).RunAsync(Export, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("MATCHED_BY_ACRONYM_ONLY", Assert.Single(result.Log).WarningCodes);
    }

    [Fact]
    public async Task An_unexpected_error_is_logged_without_its_message()
    {
        var writer = new FakeWriter(_ => throw new InvalidOperationException("Kowalski 500 PLN"));
        var api = new FakeApi(Month(Item(Housing)));

        var result = await Runner(api, writer).RunAsync(Export, CancellationToken.None);

        var entry = Assert.Single(result.Log);
        Assert.Equal(OperationLogCodes.DeductionFailed, entry.Code);
        Assert.Null(entry.ErrorNumber);
        Assert.Equal(OperationLogCodes.DeductionsFailed, result.ErrorCode);
    }

    [Fact]
    public async Task An_empty_month_succeeds_without_writing()
    {
        var writer = new FakeWriter(_ => Row("Succeeded"));

        var result = await Runner(new FakeApi(Month()), writer)
            .RunAsync(Export, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task Deductions_ambit_no_longer_hands_out_end_the_operation()
    {
        var writer = new FakeWriter(_ => Row("Succeeded"));

        var result = await Runner(new FakeApi(null), writer)
            .RunAsync(Export, CancellationToken.None);

        Assert.Equal(OperationLogCodes.DeductionsUnavailable, result.ErrorCode);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task Ambit_out_of_reach_while_reading_the_deductions_is_an_execution_failure()
    {
        var writer = new FakeWriter(_ => Row("Succeeded"));
        var api = new FakeApi(null, new HttpRequestException());

        var result = await Runner(api, writer).RunAsync(Export, CancellationToken.None);

        Assert.Equal(OperationLogCodes.ExecutionFailed, result.ErrorCode);
    }

    [Fact]
    public async Task With_writes_switched_off_nothing_is_written()
    {
        var writer = new FakeWriter(_ => Row("Succeeded"));
        var api = new FakeApi(Month(Item(Housing)));

        var result = await Runner(api, writer, writes: false)
            .RunAsync(Export, CancellationToken.None);

        Assert.Equal(OperationLogCodes.WritesDisabled, result.ErrorCode);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public void Writes_off_means_ambit_is_never_offered_the_kind()
    {
        Assert.DoesNotContain(OperationKinds.ExportDeductions, OperationKinds.SupportedWith(false));
        Assert.Contains(OperationKinds.ExportDeductions, OperationKinds.SupportedWith(true));
    }
}
