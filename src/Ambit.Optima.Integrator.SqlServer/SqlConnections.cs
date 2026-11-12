using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>Opens connections to the integrator's database, Optima's configuration database and a company database.</summary>
public sealed class SqlConnections
{
    private readonly IOptions<SqlServerOptions> _options;
    private readonly Func<string, string> _connectionString;

    public SqlConnections(IOptions<SqlServerOptions> options)
        : this(options, database => options.Value.ToConnectionString(database)) { }

    /// <summary>For tests against a server that needs SQL Server authentication.</summary>
    public SqlConnections(IOptions<SqlServerOptions> options, Func<string, string> connectionString)
    {
        _options = options;
        _connectionString = connectionString;
    }

    private SqlServerOptions Settings => _options.Value;

    public int CommandTimeout => Settings.CommandTimeoutSeconds;

    public Task<SqlConnection> OpenIntegratorAsync(CancellationToken cancellationToken) =>
        OpenAsync(Settings.IntegratorDatabase, cancellationToken);

    public Task<SqlConnection> OpenConfigAsync(CancellationToken cancellationToken) =>
        OpenAsync(Settings.ConfigDatabase, cancellationToken);

    public Task<SqlConnection> OpenCompanyAsync(
        string databaseName,
        CancellationToken cancellationToken
    ) => OpenAsync(databaseName, cancellationToken);

    private async Task<SqlConnection> OpenAsync(
        string database,
        CancellationToken cancellationToken
    )
    {
        var connection = new SqlConnection(_connectionString(database));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
