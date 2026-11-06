using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ambit.Agent.Core.Operations;

namespace Ambit.Agent.Infrastructure.AmbitApi;

public sealed class HttpAmbitApiClient(HttpClient http) : IAmbitApiClient
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
            SupportedKinds = OperationKinds.Supported,
        };
        using var response = await http.PostAsJsonAsync(ClaimPath, claim, Json, cancellationToken);
        response.EnsureSuccessStatusCode();

        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<AgentOperation>(Json, cancellationToken);
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
