using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.Infrastructure.AmbitApi;

/// <summary>
/// The connection to Ambit. Off until the installer is given a token, which it keeps in the settings file that only the
/// service and administrators can open.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)] // Bound from configuration.
public sealed class AmbitApiOptions
{
    public const string SectionName = "AmbitApi";

    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "";
    public string Key { get; init; } = "";
    public int PollSeconds { get; init; } = 30;
    public int RetrySeconds { get; init; } = 60;
    public int RequestTimeoutSeconds { get; init; } = 30;
}

public sealed class AmbitApiOptionsValidator : IValidateOptions<AmbitApiOptions>
{
    public ValidateOptionsResult Validate(string? name, AmbitApiOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (!IsAllowedUrl(options.BaseUrl))
            failures.Add("AmbitApi:BaseUrl must be an https address.");
        if (string.IsNullOrWhiteSpace(options.Key))
            failures.Add("AmbitApi:Key is required when AmbitApi:Enabled is set.");
        if (options.PollSeconds < 1)
            failures.Add("AmbitApi:PollSeconds must be at least 1.");
        if (options.RetrySeconds < 1)
            failures.Add("AmbitApi:RetrySeconds must be at least 1.");
        if (options.RequestTimeoutSeconds < 1)
            failures.Add("AmbitApi:RequestTimeoutSeconds must be at least 1.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The key travels in a header, so plain http is only for a server on this machine.</summary>
    public static bool IsAllowedUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url)
        && (
            url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)
        );
}
