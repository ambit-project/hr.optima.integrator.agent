using System.Net;
using System.Text;
using System.Text.Json;
using Ambit.Optima.Integrator.Core;
using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Core.Payrolls;
using Ambit.Optima.Integrator.Infrastructure.AmbitApi;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.UnitTests;

/// <summary>One company's payroll lists of one month: a read, so it runs with writes switched off.</summary>
public sealed class ReadPayrollsRunnerTests
{
    private static readonly AgentOperation Read = new(
        Guid.Parse("a0000000-0000-4000-8000-000000000004"),
        OperationKinds.ReadPayrolls,
        1
    );

    private static readonly Guid Olena = Guid.Parse("b0000000-0000-4000-8000-000000000001");

    private static readonly CompanyPayrolls October = new(
        "CDN_Firma",
        "1234567890",
        2026,
        10,
        [
            new PayrollList(
                41,
                "LP/10/2026",
                "Etat",
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 31),
                new DateOnly(2026, 10, 31),
                true,
                [
                    new Payout(
                        901,
                        77,
                        Olena,
                        "202611ABCDEFGH",
                        6500m,
                        4712.34m,
                        true,
                        [new PayoutElement(5001, 1, "Wynagrodzenie zasadnicze", 6500m)]
                    ),
                    new Payout(902, 78, null, "202611ZZZZZZZZ", 100m, 80m, false, []),
                ]
            ),
        ]
    );

    private sealed class FakeApi(PayrollPeriod? period, Exception? failure = null) : IAmbitApiClient
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
        ) => Task.FromResult<DeductionsWork?>(null);

        public Task<AllowancesWork?> GetAllowancesAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<AllowancesWork?>(null);

        public Task<PayrollPeriod?> GetPayrollPeriodAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => failure is null ? Task.FromResult(period) : throw failure;

        public Task<bool> ReportAsync(
            Guid operationId,
            OperationResult result,
            CancellationToken cancellationToken
        ) => Task.FromResult(true);
    }

    private sealed class FakeReader(PayrollRead read) : IPayrollReader
    {
        public List<(string, int, int)> Calls { get; } = [];

        public Task<PayrollRead> ReadAsync(
            string databaseName,
            int year,
            int month,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((databaseName, year, month));
            return Task.FromResult(read);
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

    private static OperationRunner Runner(IAmbitApiClient api, IPayrollReader reader) =>
        new(
            new NoReader(),
            new NoEmployees(),
            new NoDeductions(),
            reader,
            api,
            Options.Create(new WriteOptions()),
            TimeProvider.System
        );

    private static readonly PayrollPeriod Period = new("CDN_Firma", 2026, 10);

    [Fact]
    public async Task The_month_is_read_and_reported_with_the_number_of_payouts()
    {
        var reader = new FakeReader(
            new PayrollRead("CDN_Firma", PayrollReadStatus.Read, null, October)
        );

        var result = await Runner(new FakeApi(Period), reader)
            .RunAsync(Read, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([("CDN_Firma", 2026, 10)], reader.Calls);
        Assert.Same(October, result.Payrolls);
        var entry = Assert.Single(result.Log);
        Assert.Equal(OperationLogCodes.DatabaseRead, entry.Code);
        Assert.Equal(2, entry.Count);
    }

    [Theory]
    [InlineData(PayrollReadStatus.NoAccess, "database_no_access", null)]
    [InlineData(PayrollReadStatus.NotListed, "database_failed", RejectionCodes.NoCompanyMatches)]
    [InlineData(PayrollReadStatus.Failed, "database_failed", 229)]
    public async Task A_company_that_cannot_be_read_fails_the_operation(
        PayrollReadStatus status,
        string code,
        int? errorNumber
    )
    {
        var reader = new FakeReader(
            new PayrollRead(
                "CDN_Firma",
                status,
                status == PayrollReadStatus.Failed ? 229 : null,
                null
            )
        );

        var result = await Runner(new FakeApi(Period), reader)
            .RunAsync(Read, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(OperationLogCodes.NoCompanyReadable, result.ErrorCode);
        Assert.Null(result.Payrolls);
        var entry = Assert.Single(result.Log);
        Assert.Equal(code, entry.Code);
        Assert.Equal(errorNumber, entry.ErrorNumber);
    }

    [Fact]
    public async Task A_period_ambit_no_longer_hands_out_ends_the_operation()
    {
        var reader = new FakeReader(new PayrollRead("", PayrollReadStatus.NotListed, null, null));

        var result = await Runner(new FakeApi(null), reader).RunAsync(Read, CancellationToken.None);

        Assert.Equal(OperationLogCodes.PayrollsUnavailable, result.ErrorCode);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task Ambit_out_of_reach_while_reading_the_period_is_an_execution_failure()
    {
        var reader = new FakeReader(new PayrollRead("", PayrollReadStatus.NotListed, null, null));

        var result = await Runner(new FakeApi(null, new HttpRequestException()), reader)
            .RunAsync(Read, CancellationToken.None);

        Assert.Equal(OperationLogCodes.ExecutionFailed, result.ErrorCode);
    }

    [Fact]
    public void A_read_is_offered_without_writes()
    {
        Assert.Contains(OperationKinds.ReadPayrolls, OperationKinds.SupportedWith(false));
    }

    private sealed class Handler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            Sent = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static HttpAmbitApiClient Client(Handler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            Options.Create(new WriteOptions { Enabled = true })
        );

    [Fact]
    public async Task The_period_is_read_from_camel_case()
    {
        var handler = new Handler(
            HttpStatusCode.OK,
            """{"databaseName":"CDN_Firma","year":2026,"month":10}"""
        );

        var period = await Client(handler).GetPayrollPeriodAsync(Read.Id, CancellationToken.None);

        Assert.Equal(Period, period);
        Assert.EndsWith(
            $"/api/optima/agent/operations/{Read.Id}/payroll-period",
            handler.Request!.RequestUri!.ToString()
        );
    }

    [Fact]
    public async Task The_allowances_are_read_from_camel_case()
    {
        var handler = new Handler(
            HttpStatusCode.OK,
            """
            {"databaseName":"CDN_Firma","year":2026,"month":11,"allowances":[
              {"allowanceId":"d0000000-0000-4000-8000-000000000001","employeeId":"b0000000-0000-4000-8000-000000000001",
               "employeeCode":"202611ABCDEFGH","optimaId":12,"amount":300.00,"currency":"PLN"}]}
            """
        );

        var work = await Client(handler).GetAllowancesAsync(Read.Id, CancellationToken.None);

        Assert.EndsWith(
            $"/api/optima/agent/operations/{Read.Id}/allowances",
            handler.Request!.RequestUri!.ToString()
        );
        var allowance = Assert.Single(work!.Allowances);
        Assert.Equal(Guid.Parse("d0000000-0000-4000-8000-000000000001"), allowance.AllowanceId);
        Assert.Equal(12, allowance.OptimaId);
        Assert.Equal(300.00m, allowance.Amount);
    }

    [Fact]
    public async Task Work_no_longer_handed_out_is_null()
    {
        var client = Client(new Handler(HttpStatusCode.NotFound));

        Assert.Null(await client.GetAllowancesAsync(Read.Id, CancellationToken.None));
        Assert.Null(await client.GetPayrollPeriodAsync(Read.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_payrolls_travel_in_camel_case_with_dates_as_dates()
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        await Client(handler)
            .ReportAsync(
                Read.Id,
                new OperationResult(1, true, null, [], Payrolls: October),
                CancellationToken.None
            );

        var payrolls = JsonDocument.Parse(handler.Sent!).RootElement.GetProperty("payrolls");
        Assert.Equal("CDN_Firma", payrolls.GetProperty("databaseName").GetString());
        var list = payrolls.GetProperty("lists")[0];
        Assert.Equal("2026-10-01", list.GetProperty("periodFrom").GetString());
        Assert.True(list.GetProperty("isClosed").GetBoolean());
        var payout = list.GetProperty("payouts")[0];
        Assert.Equal(4712.34m, payout.GetProperty("net").GetDecimal());
        Assert.Equal(Olena, payout.GetProperty("employeeId").GetGuid());
        Assert.Equal(
            1,
            payout.GetProperty("elements")[0].GetProperty("componentOptimaId").GetInt32()
        );
    }
}
