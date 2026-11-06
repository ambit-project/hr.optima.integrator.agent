using Ambit.Agent.Core.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Ambit.Agent.SqlServer;

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
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(IdentityQuery, connection);
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new SqlIdentity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5)
        );
    }
}
