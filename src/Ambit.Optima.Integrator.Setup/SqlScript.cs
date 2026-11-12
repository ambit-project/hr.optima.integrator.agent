using System.Reflection;
using System.Text.RegularExpressions;

namespace Ambit.Optima.Integrator.Setup;

/// <summary>
/// Reads the embedded scripts of <c>db/agent-ambit</c> the way sqlcmd does, because a client's machine may not have it.
/// </summary>
public static partial class SqlScript
{
    public static string Load(string name)
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The script {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> Batches(
        string script,
        IReadOnlyDictionary<string, string> variables
    )
    {
        var filled = Variable()
            .Replace(
                script,
                match =>
                    variables.TryGetValue(match.Groups[1].Value, out var value)
                        ? value
                        : throw new InvalidOperationException(
                            $"The script needs the variable {match.Groups[1].Value}."
                        )
            );
        return
        [
            .. BatchSeparator().Split(filled).Where(batch => !string.IsNullOrWhiteSpace(batch)),
        ];
    }

    [GeneratedRegex(@"^[ \t]*GO[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    [GeneratedRegex(@"\$\((\w+)\)")]
    private static partial Regex Variable();
}
