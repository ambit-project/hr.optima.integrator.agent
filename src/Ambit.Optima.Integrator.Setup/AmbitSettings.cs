using Ambit.Optima.Integrator.Infrastructure.AmbitApi;

namespace Ambit.Optima.Integrator.Setup;

/// <summary>
/// What the installer was given for the connection to Ambit. Nothing given means "keep what the machine has".
/// Messages never carry the token, because they end up in the installer's log.
/// </summary>
public sealed record AmbitSettings(string? Url, string? Token)
{
    public const string DefaultUrl = "https://app.ambitsys.eu";

    private const string TokenPrefix = "optima_";
    private const int TokenHexLength = 64;

    /// <param name="url">Empty keeps the address on the machine.</param>
    /// <param name="tokenFile">
    /// Empty keeps the token on the machine. A file, because a command line can be read by any local user.
    /// </param>
    public static AmbitSettings From(string url, string tokenFile)
    {
        var address = url.Trim();
        if (address.Length > 0 && !AmbitApiOptionsValidator.IsAllowedUrl(address))
            throw new ArgumentException("The Ambit address must be an https address.");

        var token = tokenFile.Length == 0 ? "" : File.ReadAllText(tokenFile).Trim();
        if (token.Length > 0 && !LooksLikeToken(token))
            throw new ArgumentException(
                $"An Ambit token is '{TokenPrefix}' followed by {TokenHexLength} hexadecimal characters."
            );

        return new AmbitSettings(
            address.Length == 0 ? null : address.TrimEnd('/'),
            token.Length == 0 ? null : token
        );
    }

    private static bool LooksLikeToken(string value) =>
        value.StartsWith(TokenPrefix, StringComparison.Ordinal)
        && value.Length == TokenPrefix.Length + TokenHexLength
        && value.AsSpan(TokenPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}
