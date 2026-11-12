using Ambit.Optima.Integrator.Core.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.SqlServer;

public sealed class SqlDiagnostics(IOptions<SqlServerOptions> options) : ISqlDiagnostics
{
    private const string IdentityQuery = """
        SELECT
            SUSER_SNAME() AS LoginName,
            ORIGINAL_LOGIN() AS OriginalLogin,
            DB_NAME() AS DatabaseName,
            @@SERVERNAME AS ServerName,
            CAST(ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS bit) AS IsSysAdmin,
            CAST(ISNULL(IS_MEMBER('db_owner'), 0) AS bit) AS IsDbOwner;
        """;

    public async Task<SqlIdentity> GetIdentityAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var connection = new SqlConnection(settings.ToConnectionString());
        return await connection.QuerySingleAsync<SqlIdentity>(
            new CommandDefinition(
                IdentityQuery,
                commandTimeout: settings.CommandTimeoutSeconds,
                cancellationToken: cancellationToken
            )
        );
    }
}
