using System.Net;
using Ambit.Optima.Integrator.Infrastructure.AmbitApi;

namespace Ambit.Optima.Integrator.Setup;

/// <summary>
/// Asks Ambit whether it accepts a token. This route changes nothing there, unlike asking for work, which would take
/// an operation that runs only once.
/// </summary>
public static class AmbitConnectionCheck
{
    /// <summary>Null when Ambit accepts the token, otherwise what is wrong. Never contains the token.</summary>
    public static async Task<string?> FindProblemAsync(
        HttpClient http,
        string url,
        string token,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{url.TrimEnd('/')}/{HttpAmbitApiClient.ConnectionPath}"
        );
        request.Headers.Add(HttpAmbitApiClient.KeyHeader, token);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => null,
                HttpStatusCode.Unauthorized => "Ambit refused the token: unknown or revoked.",
                // Anything else, a 200 included, means this address is not an Ambit that knows the agent.
                _ =>
                    $"The address answered {(int)response.StatusCode} instead of accepting the token.",
            };
        }
        catch (HttpRequestException exception)
        {
            return $"Ambit cannot be reached: {exception.Message}";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Ambit did not answer in time.";
        }
    }
}
