namespace Ambit.Agent.Setup;

/// <summary>What the installer's wizard passes to this program.</summary>
public sealed record SetupArguments(
    string Command,
    string Server,
    string ConfigDatabase,
    IReadOnlyList<string> Firms,
    string AppDirectory,
    string AmbitUrl,
    string AmbitTokenFile
)
{
    public const string FirmsCommand = "firms";
    public const string AmbitSettingsCommand = "ambit-settings";
    public const string CheckAmbitCommand = "check-ambit";
    private const string ApplyCommand = "apply";

    public static SetupArguments Parse(IReadOnlyList<string> args)
    {
        if (
            args.Count == 0
            || args[0]
                is not (FirmsCommand or ApplyCommand or AmbitSettingsCommand or CheckAmbitCommand)
        )
            throw new ArgumentException(
                $"Expected '{FirmsCommand}', '{ApplyCommand}', '{AmbitSettingsCommand}' or '{CheckAmbitCommand}'."
            );
        if (args.Count % 2 == 0)
            throw new ArgumentException("Every option needs a value.");

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i += 2)
            options[args[i]] = args[i + 1];

        var command = args[0];
        var server = options.GetValueOrDefault("--server", "");
        if (server.Length == 0 && command is FirmsCommand or ApplyCommand)
            throw new ArgumentException("--server is required.");

        var firms = options
            .GetValueOrDefault("--firms", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var appDirectory = options.GetValueOrDefault("--app-dir", "");
        if (command == ApplyCommand && (firms.Length == 0 || appDirectory.Length == 0))
            throw new ArgumentException("'apply' needs --firms and --app-dir.");
        if (command == AmbitSettingsCommand && appDirectory.Length == 0)
            throw new ArgumentException($"'{AmbitSettingsCommand}' needs --app-dir.");

        return new SetupArguments(
            command,
            server,
            options.GetValueOrDefault("--config-database", "CDN_KNF_Konfiguracja"),
            firms,
            appDirectory,
            options.GetValueOrDefault("--ambit-url", ""),
            options.GetValueOrDefault("--ambit-token-file", "")
        );
    }
}
