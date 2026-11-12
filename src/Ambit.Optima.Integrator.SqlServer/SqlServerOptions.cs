using JetBrains.Annotations;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>
/// Where SQL Server is. There is deliberately no user or password here: the connection always uses
/// the Windows identity of the Worker's process.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)] // Bound from configuration.
public sealed class SqlServerOptions
{
    public const string SectionName = "Sql";

    public string Server { get; init; } = "localhost";
    public string Database { get; init; } = "master";
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public int ConnectionTimeoutSeconds { get; init; } = 15;
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>The agent's own database, with its procedures and logs.</summary>
    public string AgentDatabase { get; init; } = "agent-ambit";

    /// <summary>Long, because one call visits every company.</summary>
    public int EmployeeAddTimeoutSeconds { get; init; } = 300;

    public int LogRetentionDays { get; init; } = 30;

    public string ToConnectionString() => Build(Database);

    public string ToAgentConnectionString() => Build(AgentDatabase);

    private string Build(string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            Encrypt = Encrypt
                ? SqlConnectionEncryptOption.Mandatory
                : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectionTimeoutSeconds,
            ApplicationName = "Ambit Optima Integrator",
        }.ConnectionString;
}
