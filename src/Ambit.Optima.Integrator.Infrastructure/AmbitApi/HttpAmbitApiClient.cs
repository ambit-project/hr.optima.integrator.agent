using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Core.Payrolls;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.Infrastructure.AmbitApi;

public sealed class HttpAmbitApiClient(HttpClient http, IOptions<WriteOptions> writes)
    : IAmbitApiClient
{
    public const string KeyHeader = "X-Optima-Agent-Key";
    public const string ConnectionPath = "api/optima/agent/connection";
    private const string ClaimPath = "api/optima/agent/operations/claim";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AgentOperation?> ClaimAsync(CancellationToken cancellationToken)
    {
        var claim = new
        {
            AgentVersion = typeof(HttpAmbitApiClient).Assembly.GetName().Version?.ToString(),
            SupportedKinds = OperationKinds.SupportedWith(writes.Value.Enabled),
        };
        using var response = await http.PostAsJsonAsync(ClaimPath, claim, Json, cancellationToken);
        response.EnsureSuccessStatusCode();

        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<AgentOperation>(Json, cancellationToken);
    }

    public async Task<EmployeesWork?> GetEmployeesAsync(
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        using var response = await http.GetAsync(
            $"api/optima/agent/operations/{operationId}/employees",
            cancellationToken
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EmployeesWork>(Json, cancellationToken);
    }

    public Task<DeductionsWork?> GetDeductionsAsync(
        Guid operationId,
        CancellationToken cancellationToken
    ) => GetWorkAsync<DeductionsWork>(operationId, "deductions", cancellationToken);

    public Task<AllowancesWork?> GetAllowancesAsync(
        Guid operationId,
        CancellationToken cancellationToken
    ) => GetWorkAsync<AllowancesWork>(operationId, "allowances", cancellationToken);

    public Task<PayrollPeriod?> GetPayrollPeriodAsync(
        Guid operationId,
        CancellationToken cancellationToken
    ) => GetWorkAsync<PayrollPeriod>(operationId, "payroll-period", cancellationToken);

    /// <summary>What an operation this agent holds needs. A 404 is final and reads as null.</summary>
    private async Task<T?> GetWorkAsync<T>(
        Guid operationId,
        string part,
        CancellationToken cancellationToken
    )
        where T : class
    {
        using var response = await http.GetAsync(
            $"api/optima/agent/operations/{operationId}/{part}",
            cancellationToken
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }

    public async Task<bool> ReportAsync(
        Guid operationId,
        OperationResult result,
        CancellationToken cancellationToken
    )
    {
        using var response = await http.PostAsJsonAsync(
            $"api/optima/agent/operations/{operationId}/result",
            result,
            Json,
            cancellationToken
        );

        // A refused report stays refused however often it is sent. A sign-in problem can be fixed, so it is retried.
        if (
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            || response.StatusCode == HttpStatusCode.RequestEntityTooLarge
        )
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }
}
