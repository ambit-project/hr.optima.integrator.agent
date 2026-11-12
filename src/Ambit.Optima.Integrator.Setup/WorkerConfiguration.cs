using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ambit.Optima.Integrator.Setup;

/// <summary>What the machine's settings say about Ambit.</summary>
public sealed record AmbitOnMachine(string Url, string? Token);

/// <summary>The Worker's local settings. An upgrade keeps whatever an administrator has set in the file.</summary>
public static class WorkerConfiguration
{
    private const string AmbitSection = "AmbitApi";
    private const string UrlKey = "BaseUrl";
    private const string TokenKey = "Key";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Merge(string? existing, string server, AmbitSettings? ambit = null)
    {
        var root = existing is null ? [] : JsonNode.Parse(existing)!.AsObject();
        if (root["Sql"] is not JsonObject sql)
            root["Sql"] = sql = [];

        sql["Server"] = server;
        sql["Encrypt"] ??= true;
        // A default SQL Server install has a self-signed certificate, which the Worker could not verify.
        sql["TrustServerCertificate"] ??= true;

        if (root[AmbitSection] is not JsonObject api)
            root[AmbitSection] = api = [];

        if (ambit?.Url is { } url)
            api[UrlKey] = url;
        else
            api[UrlKey] ??= AmbitSettings.DefaultUrl;

        // A token is what switches the connection on. Without one the agent keeps working for the local UI alone.
        if (ambit?.Token is { } token)
        {
            api[TokenKey] = token;
            api["Enabled"] = true;
        }

        return root.ToJsonString(Indented);
    }

    public static AmbitOnMachine ReadAmbit(string? existing)
    {
        var api = existing is null ? null : JsonNode.Parse(existing)?[AmbitSection];
        var url = api?[UrlKey]?.GetValue<string>();
        return new AmbitOnMachine(
            string.IsNullOrEmpty(url) ? AmbitSettings.DefaultUrl : url,
            api?[TokenKey]?.GetValue<string>()
        );
    }

    public static string SettingsPath(string appDirectory) =>
        Path.Combine(appDirectory, "appsettings.Production.json");

    /// <summary>Needs the service to exist already, because the file is opened to its account.</summary>
    public static void Write(
        string appDirectory,
        string server,
        AmbitSettings ambit,
        string serviceAccount
    )
    {
        var path = SettingsPath(appDirectory);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;

        // Locked before the token goes in, so it never sits in a file that inherited Program Files' rights.
        if (OperatingSystem.IsWindows())
            Protect(path, serviceAccount);
        File.WriteAllText(path, Merge(existing, server, ambit));
    }

    /// <summary>
    /// The file holds the token, and any local user can read Program Files. Only the service, administrators and the
    /// system may open it, and no rights are inherited.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void Protect(string path, string serviceAccount)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), Full);
        Allow(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            Full
        );
        Allow(security, new NTAccount(serviceAccount), FileSystemRights.Read);

        var file = new FileInfo(path);
        if (file.Exists)
            file.SetAccessControl(security);
        else
            file.Create(FileMode.CreateNew, Full, FileShare.None, 4096, FileOptions.None, security)
                .Dispose();
    }

    [SupportedOSPlatform("windows")]
    private const FileSystemRights Full = FileSystemRights.FullControl;

    [SupportedOSPlatform("windows")]
    private static void Allow(
        FileSecurity security,
        IdentityReference who,
        FileSystemRights rights
    ) => security.AddAccessRule(new FileSystemAccessRule(who, rights, AccessControlType.Allow));
}
