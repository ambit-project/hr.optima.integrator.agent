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

    public const string DefaultIntegratorDatabase = "ambit-optima-integrator";
    public const string DefaultConfigDatabase = "CDN_KNF_Konfiguracja";

    public string Server { get; init; } = "localhost";
    public string Database { get; init; } = "master";
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public int ConnectionTimeoutSeconds { get; init; } = 15;
    public int CommandTimeoutSeconds { get; init; } = 60;

    /// <summary>The integrator's own database: the record of writes and the log.</summary>
    public string IntegratorDatabase { get; init; } = DefaultIntegratorDatabase;

    /// <summary>Optima's configuration database, which holds the list of companies (<c>CDN.Bazy</c>).</summary>
    public string ConfigDatabase { get; init; } = DefaultConfigDatabase;

    public int LogRetentionDays { get; init; } = 30;

    public string ToConnectionString() => ToConnectionString(Database);

    public string ToConnectionString(string database) =>
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
