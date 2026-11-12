using System.Reflection;
using Ambit.Optima.Integrator.SqlServer;
using Dapper;
using DbUp;
using DbUp.Engine;
using DbUp.Helpers;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.Setup;

public sealed record OptimaFirm(string DatabaseName, bool HasAccess);

/// <summary>
/// Creates or migrates the integrator's database and sets which companies the Worker may reach. Runs as the installer's
/// account, which must be sysadmin; the Worker's own account never gets that.
/// </summary>
/// <remarks>
/// Schema changes are numbered scripts in <c>db/migrations</c>, applied once each by DbUp and recorded in
/// <c>dbo.SchemaVersions</c>. Rights are idempotent scripts in <c>db/grants</c>, applied on every run, so running the
/// installer again repairs them.
/// </remarks>
public sealed class DatabaseInstaller(
    string server,
    string configDatabase,
    Func<string, string>? connectionString = null
)
{
    public const string IntegratorDatabase = SqlServerOptions.DefaultIntegratorDatabase;

    private const string MigrationsPrefix = "migrations.";
    private const string GrantsPrefix = "grants.";

    public static IReadOnlyList<string> EmbeddedScripts =>
        Assembly
            .GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(MigrationsPrefix) || name.StartsWith(GrantsPrefix))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The active companies on Optima's list whose database exists on this instance.</summary>
    public async Task<IReadOnlyList<OptimaFirm>> ReadFirmsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync("master", cancellationToken);

        if (await connection.ExecuteScalarAsync<int?>("SELECT IS_SRVROLEMEMBER(N'sysadmin');") != 1)
            throw new InvalidOperationException(
                "The installer's account must be sysadmin on this SQL Server instance."
            );

        var names = (
            await connection.QueryAsync<string>(
                $"""
                SELECT DISTINCT TRIM(b.Baz_NazwaBazy)
                FROM {Quote(configDatabase)}.CDN.Bazy AS b
                JOIN sys.databases AS d ON d.name = TRIM(b.Baz_NazwaBazy)
                WHERE ISNULL(b.Baz_Nieaktywna, 0) = 0 AND d.state_desc = N'ONLINE'
                ORDER BY 1;
                """
            )
        ).AsList();

        var firms = new List<OptimaFirm>(names.Count);
        foreach (var name in names)
        {
            var users = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {Quote(name)}.sys.database_principals WHERE name = @login;",
                new { login = ServiceRegistration.Login }
            );
            firms.Add(new OptimaFirm(name, users > 0));
        }
        return firms;
    }

    /// <summary>Refuses a bad server, account or choice of companies before anything on the machine changes.</summary>
    public async Task<IReadOnlyList<OptimaFirm>> CheckSelectionAsync(
        IReadOnlyCollection<string> selectedFirms,
        CancellationToken cancellationToken
    )
    {
        var firms = await ReadFirmsAsync(cancellationToken);
        var unknown = selectedFirms.Except(firms.Select(firm => firm.DatabaseName)).ToList();
        return unknown.Count == 0
            ? firms
            : throw new InvalidOperationException(
                $"Not on Optima's list of companies: {string.Join(", ", unknown)}."
            );
    }

    /// <summary>
    /// After this the Worker reaches exactly the selected companies, so clearing a company in the wizard takes its
    /// access away. Every step is safe to run again, which is what makes a failed run repeatable.
    /// </summary>
    public async Task ApplyAsync(
        IReadOnlyList<OptimaFirm> firms,
        IReadOnlyCollection<string> selectedFirms,
        CancellationToken cancellationToken
    )
    {
        await MigrateAsync(cancellationToken);
        Run("master", GrantsPrefix + "01-login.sql");
        Run(IntegratorDatabase, GrantsPrefix + "02-integrator.sql");
        Run(configDatabase, GrantsPrefix + "03-config.sql");

        foreach (var firm in firms)
        {
            var selected = selectedFirms.Contains(firm.DatabaseName);
            Run(
                firm.DatabaseName,
                GrantsPrefix + (selected ? "company.sql" : "company-revoke.sql")
            );
            Console.WriteLine($"{firm.DatabaseName}: {(selected ? "access" : "no access")}");
        }
    }

    /// <summary>Creates the integrator's database when it is missing and applies the migrations it has not had yet.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        EnsureDatabase.For.SqlDatabase(ConnectionString(IntegratorDatabase));
        await using (var master = await OpenAsync("master", cancellationToken))
        {
            // SQL Server Express turns this on by default, and a connection racing the closing database then fails.
            await master.ExecuteAsync(
                $"ALTER DATABASE {Quote(IntegratorDatabase)} SET AUTO_CLOSE OFF;"
            );
        }

        Run(IntegratorDatabase, MigrationsPrefix, journal: true);
    }

    /// <summary>
    /// Runs the embedded scripts whose names start with <paramref name="scripts"/>, in name order, on
    /// <paramref name="database"/>. Only migrations are journaled; grants run every time.
    /// </summary>
    private void Run(string database, string scripts, bool journal = false)
    {
        var builder = DeployChanges
            .To.SqlDatabase(ConnectionString(database))
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.StartsWith(scripts, StringComparison.Ordinal)
            )
            .WithVariable("ServiceLogin", ServiceRegistration.Login)
            .WithExecutionTimeout(TimeSpan.FromMinutes(10))
            .LogToConsole();
        if (!journal)
            builder = builder.JournalTo(new NullJournal());

        DatabaseUpgradeResult result = builder.Build().PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException(
                $"{result.ErrorScript?.Name ?? scripts} failed on {database}: {result.Error.Message}",
                result.Error
            );
    }

    private string ConnectionString(string database) =>
        connectionString?.Invoke(database)
        ?? new SqlServerOptions
        {
            Server = server,
            TrustServerCertificate = true,
        }.ToConnectionString(database);

    private async Task<SqlConnection> OpenAsync(
        string database,
        CancellationToken cancellationToken
    )
    {
        var connection = new SqlConnection(ConnectionString(database));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string Quote(string name) => $"[{name.Replace("]", "]]")}]";
}
