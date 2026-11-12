using System.Net;
using System.Text;
using System.Text.Json;
using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Infrastructure.AmbitApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.UnitTests;

public sealed class OperationPollerTests
{
    private static readonly AgentOperation Read = new(
        Guid.Parse("a0000000-0000-4000-8000-000000000001"),
        OperationKinds.ReadPayrollComponents,
        2
    );

    private static readonly PayrollComponent Housing = new(
        339,
        "Potrącenie za mieszkanie",
        null,
        true,
        4,
        0,
        null,
        false,
        false,
        null,
        null
    );

    private sealed class FakeReader(params CompanyRead[] companies) : IPayrollComponentReader
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<CompanyRead>> ReadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is null
                ? Task.FromResult<IReadOnlyList<CompanyRead>>(companies)
                : throw Failure;
        }
    }

    private sealed class NoEmployees : IEmployeeWriter
    {
        public Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
            EmployeeAddRequest request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("A read writes nobody.");
    }

    private sealed class FakeApi : IAmbitApiClient
    {
        public AgentOperation? Next { get; init; }
        public Exception? ClaimFailure { get; init; }
        public Queue<Exception> ReportFailures { get; } = new();
        public List<OperationResult> Reports { get; } = [];
        public int ReportCalls { get; private set; }
        public bool Accept { get; init; } = true;

        public Task<EmployeesWork?> GetEmployeesAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<EmployeesWork?>(null);

        public Task<DeductionsWork?> GetDeductionsAsync(
            Guid operationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<DeductionsWork?>(null);

        public Task<AgentOperation?> ClaimAsync(CancellationToken cancellationToken) =>
            ClaimFailure is null ? Task.FromResult(Next) : throw ClaimFailure;

        public Task<bool> ReportAsync(
            Guid operationId,
            OperationResult result,
            CancellationToken cancellationToken
        )
        {
            ReportCalls++;
            if (ReportFailures.TryDequeue(out var failure))
                throw failure;
            Reports.Add(result);
            return Task.FromResult(Accept);
        }
    }

    private static OperationPoller Poller(FakeApi api, FakeReader reader) =>
        new(
            api,
            new OperationRunner(
                reader,
                new NoEmployees(),
                new NoDeductions(),
                api,
                Options.Create(new WriteOptions()),
                TimeProvider.System
            ),
            Options.Create(new AmbitApiOptions { RetrySeconds = 1 }),
            TimeProvider.System,
            NullLogger<OperationPoller>.Instance
        );

    private static CompanyRead ReadCompany(string name) =>
        new(name, CompanyReadStatus.Read, "1234567890", null, [Housing]);

    [Fact]
    public async Task An_empty_queue_runs_nothing()
    {
        var api = new FakeApi();
        var reader = new FakeReader();

        Assert.False(await Poller(api, reader).RunNextAsync(CancellationToken.None));
        Assert.Equal(0, reader.Calls);
        Assert.Empty(api.Reports);
    }

    [Fact]
    public async Task Ambit_being_unreachable_runs_nothing()
    {
        var api = new FakeApi { Next = Read, ClaimFailure = new HttpRequestException() };
        var reader = new FakeReader();

        Assert.False(await Poller(api, reader).RunNextAsync(CancellationToken.None));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Definitions_are_reported_with_the_attempt_it_was_given()
    {
        var api = new FakeApi { Next = Read };

        Assert.True(
            await Poller(api, new FakeReader(ReadCompany("CDN_A")))
                .RunNextAsync(CancellationToken.None)
        );

        var result = Assert.Single(api.Reports);
        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Attempt);
        var company = Assert.Single(result.PayrollComponents!);
        Assert.Equal("CDN_A", company.DatabaseName);
        Assert.Equal(Housing, Assert.Single(company.Components));
        var entry = Assert.Single(result.Log);
        Assert.Equal(OperationLogCodes.DatabaseRead, entry.Code);
        Assert.Equal(1, entry.Count);
    }

    [Fact]
    public async Task An_allowance_travels_with_the_deductions_and_says_which_it_is()
    {
        var api = new FakeApi { Next = Read };
        var allowance = Housing with { OptimaId = 340, Name = "Dojazd", IsDeduction = false };
        var reader = new FakeReader(
            new CompanyRead("CDN_A", CompanyReadStatus.Read, null, null, [Housing, allowance])
        );

        await Poller(api, reader).RunNextAsync(CancellationToken.None);

        var components = Assert.Single(Assert.Single(api.Reports).PayrollComponents!).Components;
        Assert.Contains(components, component => component is { OptimaId: 339, IsDeduction: true });
        Assert.Contains(
            components,
            component => component is { OptimaId: 340, IsDeduction: false }
        );
    }

    [Fact]
    public async Task An_excluded_company_is_not_a_failure()
    {
        var api = new FakeApi { Next = Read };
        var reader = new FakeReader(
            ReadCompany("CDN_A"),
            new CompanyRead("CDN_B", CompanyReadStatus.NoAccess, null, null, [])
        );

        await Poller(api, reader).RunNextAsync(CancellationToken.None);

        var result = Assert.Single(api.Reports);
        Assert.True(result.Succeeded);
        Assert.Contains(
            result.Log,
            entry => entry is { Code: OperationLogCodes.DatabaseNoAccess, DatabaseName: "CDN_B" }
        );
    }

    [Fact]
    public async Task No_company_within_reach_is_a_failure()
    {
        var api = new FakeApi { Next = Read };
        var reader = new FakeReader(
            new CompanyRead("CDN_B", CompanyReadStatus.NoAccess, null, null, [])
        );

        await Poller(api, reader).RunNextAsync(CancellationToken.None);

        var result = Assert.Single(api.Reports);
        Assert.False(result.Succeeded);
        Assert.Equal(OperationLogCodes.NoCompanyReadable, result.ErrorCode);
    }

    [Fact]
    public async Task A_failed_company_fails_the_operation_and_keeps_the_others_definitions()
    {
        var api = new FakeApi { Next = Read };
        var reader = new FakeReader(
            ReadCompany("CDN_A"),
            new CompanyRead("CDN_B", CompanyReadStatus.Failed, null, 229, [])
        );

        await Poller(api, reader).RunNextAsync(CancellationToken.None);

        var result = Assert.Single(api.Reports);
        Assert.False(result.Succeeded);
        Assert.Equal(OperationLogCodes.DatabaseFailed, result.ErrorCode);
        Assert.Equal("CDN_A", Assert.Single(result.PayrollComponents!).DatabaseName);
        Assert.Contains(
            result.Log,
            entry =>
                entry
                    is {
                        Code: OperationLogCodes.DatabaseFailed,
                        DatabaseName: "CDN_B",
                        ErrorNumber: 229
                    }
        );
    }

    [Fact]
    public async Task A_read_that_breaks_is_reported_once_without_the_message()
    {
        var api = new FakeApi { Next = Read };
        var reader = new FakeReader
        {
            Failure = new InvalidOperationException("PESEL 90052012345"),
        };

        await Poller(api, reader).RunNextAsync(CancellationToken.None);

        Assert.Equal(1, reader.Calls);
        var result = Assert.Single(api.Reports);
        Assert.False(result.Succeeded);
        Assert.Equal(OperationLogCodes.ExecutionFailed, result.ErrorCode);
        Assert.DoesNotContain("90052012345", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task A_kind_it_does_not_know_fails_without_running_anything()
    {
        var api = new FakeApi { Next = Read with { Kind = "SomethingNew" } };
        var reader = new FakeReader();

        await Poller(api, reader).RunNextAsync(CancellationToken.None);

        Assert.Equal(0, reader.Calls);
        Assert.Equal(OperationLogCodes.UnsupportedKind, Assert.Single(api.Reports).ErrorCode);
    }

    [Fact]
    public async Task Only_sending_the_result_is_repeated_not_the_operation()
    {
        var api = new FakeApi { Next = Read };
        api.ReportFailures.Enqueue(new HttpRequestException());
        var reader = new FakeReader(ReadCompany("CDN_A"));

        await Poller(api, reader).RunNextAsync(CancellationToken.None);

        Assert.Equal(1, reader.Calls);
        Assert.Equal(2, api.ReportCalls);
        Assert.Single(api.Reports);
    }

    [Fact]
    public async Task A_refused_report_is_not_sent_again()
    {
        var api = new FakeApi { Next = Read, Accept = false };

        await Poller(api, new FakeReader(ReadCompany("CDN_A")))
            .RunNextAsync(CancellationToken.None);

        Assert.Equal(1, api.ReportCalls);
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

    private static HttpAmbitApiClient Client(Handler handler, bool writes = false) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            Options.Create(new WriteOptions { Enabled = writes })
        );

    [Fact]
    public async Task No_content_means_no_work()
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        Assert.Null(await Client(handler).ClaimAsync(CancellationToken.None));
        Assert.Equal(
            "https://api.example/api/optima/agent/operations/claim",
            handler.Request!.RequestUri!.ToString()
        );
        Assert.Contains(OperationKinds.ReadPayrollComponents, handler.Sent);
    }

    [Fact]
    public async Task A_claim_is_read_from_camel_case()
    {
        var handler = new Handler(
            HttpStatusCode.OK,
            """{"id":"a0000000-0000-4000-8000-000000000001","kind":"ReadPayrollComponents","attempt":2}"""
        );

        Assert.Equal(Read, await Client(handler).ClaimAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refusal_is_final(HttpStatusCode status)
    {
        var client = Client(new Handler(status));

        Assert.False(
            await client.ReportAsync(
                Read.Id,
                new OperationResult(1, true, null, []),
                CancellationToken.None
            )
        );
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Other_errors_can_be_sent_again(HttpStatusCode status)
    {
        var client = Client(new Handler(status));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ReportAsync(
                Read.Id,
                new OperationResult(1, true, null, []),
                CancellationToken.None
            )
        );
    }

    [Theory]
    [InlineData(true, "", "key", false)]
    [InlineData(true, "http://api.example", "key", false)]
    [InlineData(true, "https://api.example", "", false)]
    [InlineData(true, "http://localhost:5000", "key", true)]
    [InlineData(true, "https://api.example", "key", true)]
    [InlineData(false, "", "", true)]
    public void The_connection_is_checked_only_when_switched_on(
        bool enabled,
        string url,
        string key,
        bool valid
    )
    {
        var result = new AmbitApiOptionsValidator().Validate(
            null,
            new AmbitApiOptions
            {
                Enabled = enabled,
                BaseUrl = url,
                Key = key,
            }
        );

        Assert.Equal(valid, result.Succeeded);
    }
}

public sealed class AmbitEmployeesContractTests
{
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

    private static HttpAmbitApiClient Client(Handler handler, bool writes) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            Options.Create(new WriteOptions { Enabled = writes })
        );

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_claim_offers_adding_people_only_with_writes_on(bool writes, bool offered)
    {
        var handler = new Handler(HttpStatusCode.NoContent);

        await Client(handler, writes).ClaimAsync(CancellationToken.None);

        var kinds = JsonDocument
            .Parse(handler.Sent!)
            .RootElement.GetProperty("supportedKinds")
            .EnumerateArray()
            .Select(k => k.GetString())
            .ToList();
        Assert.Equal(offered, kinds.Contains(OperationKinds.AddEmployees));
        Assert.Contains(OperationKinds.ReadPayrollComponents, kinds);
    }

    [Fact]
    public async Task The_people_are_read_from_ambits_camel_case()
    {
        var operationId = Guid.NewGuid();
        var handler = new Handler(
            HttpStatusCode.OK,
            """
            {
              "databaseName": "CDN_Ambit",
              "employees": [
                {
                  "employeeId": "b0000000-0000-4000-8000-000000000001",
                  "employeeCode": "W0000000000000000001",
                  "lastName": "Bondarenko",
                  "firstName": "Olena",
                  "dateOfBirth": "1990-05-12",
                  "pesel": "90051212345",
                  "citizenshipCode": "UA",
                  "documentKind": "Passport",
                  "documentNumber": "FX123456",
                  "positionName": "Monter",
                  "startsOn": "2026-03-01"
                }
              ]
            }
            """
        );

        var work = await Client(handler, true)
            .GetEmployeesAsync(operationId, CancellationToken.None);

        Assert.EndsWith(
            $"/api/optima/agent/operations/{operationId}/employees",
            handler.Request!.RequestUri!.ToString()
        );
        Assert.Equal("CDN_Ambit", work!.DatabaseName);
        var person = Assert.Single(work.Employees);
        Assert.Equal(Guid.Parse("b0000000-0000-4000-8000-000000000001"), person.EmployeeId);
        Assert.Equal("Bondarenko", person.LastName);
        Assert.Equal(new DateOnly(1990, 5, 12), person.DateOfBirth);
        Assert.Equal("90051212345", person.Pesel);
        Assert.Equal("Passport", person.DocumentKind);
        Assert.Equal(new DateOnly(2026, 3, 1), person.StartsOn);
        Assert.Null(person.OperationId);
    }

    [Fact]
    public async Task People_no_longer_handed_out_are_null()
    {
        var work = await Client(new Handler(HttpStatusCode.NotFound), true)
            .GetEmployeesAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(work);
    }

    [Fact]
    public async Task Ambit_failing_while_handing_out_people_can_be_tried_again()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(new Handler(HttpStatusCode.ServiceUnavailable), true)
                .GetEmployeesAsync(Guid.NewGuid(), CancellationToken.None)
        );
    }
}
