using Ambit.Agent.SqlServer;
using Microsoft.Data.SqlClient;

namespace Ambit.Agent.Setup;

public sealed record OptimaFirm(string DatabaseName, bool HasAccess);

/// <summary>Creates or updates the agent's database and sets which companies the Worker may reach.</summary>
public sealed class AgentDatabaseInstaller(string server, string configDatabase)
{
    private const string AgentDatabase = "agent-ambit";

    private static readonly string[] DatabaseScripts =
    [
        "001-database.sql",
        "002-tables.sql",
        "003-procedures.sql",
        "004-service-account.sql",
    ];

    /// <summary>The active companies on Optima's list whose database exists on this instance.</summary>
    public async Task<IReadOnlyList<OptimaFirm>> ReadFirmsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var role = new SqlCommand("SELECT IS_SRVROLEMEMBER(N'sysadmin');", connection);
        if (await role.ExecuteScalarAsync(cancellationToken) is not 1)
            throw new InvalidOperationException(
                "The installer's account must be sysadmin on this SQL Server instance."
            );

        var names = new List<string>();
        await using (
            var list = new SqlCommand(
                $"""
                SELECT DISTINCT TRIM(b.Baz_NazwaBazy)
                FROM {Quote(configDatabase)}.CDN.Bazy AS b
                JOIN sys.databases AS d ON d.name = TRIM(b.Baz_NazwaBazy)
                WHERE ISNULL(b.Baz_Nieaktywna, 0) = 0 AND d.state_desc = N'ONLINE'
                ORDER BY 1;
                """,
                connection
            )
        )
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                names.Add(reader.GetString(0));
        }

        var firms = new List<OptimaFirm>(names.Count);
        foreach (var name in names)
        {
            await using var access = new SqlCommand(
                $"SELECT COUNT(*) FROM {Quote(name)}.sys.database_principals WHERE name = @login;",
                connection
            );
            access.Parameters.AddWithValue("@login", ServiceRegistration.Login);
            var count = (int)(await access.ExecuteScalarAsync(cancellationToken))!;
            firms.Add(new OptimaFirm(name, count > 0));
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
    /// access away. Each script is safe to run again, which is what makes a failed run repeatable.
    /// </summary>
    public async Task ApplyAsync(
        IReadOnlyList<OptimaFirm> firms,
        IReadOnlyCollection<string> selectedFirms,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenAsync(cancellationToken);
        var variables = new Dictionary<string, string>
        {
            ["AgentDatabase"] = AgentDatabase,
            ["ServiceLogin"] = ServiceRegistration.Login,
            ["ConfigDatabase"] = configDatabase,
        };
        foreach (var script in DatabaseScripts)
            await RunAsync(connection, script, variables, cancellationToken);

        foreach (var firm in firms)
        {
            variables["FirmDatabase"] = firm.DatabaseName;
            var selected = selectedFirms.Contains(firm.DatabaseName);
            await RunAsync(
                connection,
                selected ? "005-firm.sql" : "006-firm-revoke.sql",
                variables,
                cancellationToken
            );
            Console.WriteLine($"{firm.DatabaseName}: {(selected ? "access" : "no access")}");
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(
            new SqlServerOptions
            {
                Server = server,
                TrustServerCertificate = true,
            }.ToConnectionString()
        );
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task RunAsync(
        SqlConnection connection,
        string script,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken
    )
    {
        foreach (var batch in SqlScript.Batches(SqlScript.Load(script), variables))
        {
            await using var command = new SqlCommand(batch, connection);
            command.CommandTimeout = 0; // Creating a database has no sensible upper limit here.
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string Quote(string name) => $"[{name.Replace("]", "]]")}]";
}
