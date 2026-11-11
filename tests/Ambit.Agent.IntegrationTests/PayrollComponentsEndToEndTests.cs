using System.Net;
using System.Text;
using System.Text.Json;
using Ambit.Agent.Core.Employees;
using Ambit.Agent.Core.Operations;
using Ambit.Agent.Infrastructure.AmbitApi;
using Ambit.Agent.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.IntegrationTests;

/// <summary>Runs only where a real Optima instance with the agent's database is within reach.</summary>
public sealed class OptimaSqlFactAttribute : FactAttribute
{
    public const string ServerVariable = "AMBIT_TEST_SQL_SERVER";

    public OptimaSqlFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(ServerVariable) is null)
            Skip = $"Set {ServerVariable} to a SQL Server instance with Optima.";
    }
}

/// <summary>
/// The whole agent against real Optima data, with only Ambit's HTTP answers faked: this is the JSON Ambit receives.
/// Set AMBIT_TEST_DEDUCTION and AMBIT_TEST_ALLOWANCE to names defined in Optima to check they arrive.
/// </summary>
public sealed class PayrollComponentsEndToEndTests
{
    private const string OperationId = "a0000000-0000-4000-8000-000000000001";

    private sealed class FakeAmbit : HttpMessageHandler
    {
        public string? Result { get; private set; }
        public Uri? ResultUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"id":"{{OperationId}}","kind":"ReadPayrollComponents","attempt":1}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };

            ResultUri = request.RequestUri;
            Result = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [OptimaSqlFact]
    public async Task A_claimed_read_reports_the_components_marked_in_Optima()
    {
        using var ambit = new FakeAmbit();
        using var http = new HttpClient(ambit);
        http.BaseAddress = new Uri("https://ambit.example/");
        var sql = Options.Create(
            new SqlServerOptions
            {
                Server = Environment.GetEnvironmentVariable(OptimaSqlFactAttribute.ServerVariable)!,
                TrustServerCertificate = true,
            }
        );
        var client = new HttpAmbitApiClient(http, Options.Create(new WriteOptions()));
        var poller = new OperationPoller(
            client,
            new OperationRunner(
                new SqlPayrollComponentReader(sql),
                new SqlEmployeeWriter(sql),
                new SqlDeductionWriter(sql),
                client,
                Options.Create(new WriteOptions()),
                TimeProvider.System
            ),
            Options.Create(new AmbitApiOptions()),
            TimeProvider.System,
            NullLogger<OperationPoller>.Instance
        );

        Assert.True(await poller.RunNextAsync(CancellationToken.None));

        Assert.EndsWith(
            $"/api/optima/agent/operations/{OperationId}/result",
            ambit.ResultUri!.ToString()
        );
        using var json = JsonDocument.Parse(ambit.Result!);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("attempt").GetInt32());
        Assert.True(root.GetProperty("succeeded").GetBoolean(), ambit.Result);

        var companies = root.GetProperty("payrollComponents").EnumerateArray().ToList();
        Assert.NotEmpty(companies);
        Assert.All(
            companies,
            company => Assert.NotEmpty(company.GetProperty("databaseName").GetString()!)
        );

        var definitions = companies
            .SelectMany(company => company.GetProperty("components").EnumerateArray())
            .ToList();
        Assert.NotEmpty(definitions);
        Assert.All(
            definitions,
            definition =>
            {
                Assert.True(definition.GetProperty("optimaId").GetInt32() > 0);
                Assert.NotEmpty(definition.GetProperty("name").GetString()!);
            }
        );

        var names = definitions
            .Select(definition => definition.GetProperty("name").GetString())
            .ToList();
        if (Environment.GetEnvironmentVariable("AMBIT_TEST_DEDUCTION") is { } deduction)
            Assert.Contains(deduction, names);
        if (Environment.GetEnvironmentVariable("AMBIT_TEST_ALLOWANCE") is { } allowance)
            Assert.Contains(allowance, names);

        Assert.Contains(
            root.GetProperty("log").EnumerateArray(),
            entry => entry.GetProperty("code").GetString() == OperationLogCodes.DatabaseRead
        );
    }
}
