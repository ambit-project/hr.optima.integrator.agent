using Ambit.Optima.Integrator.SqlServer;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.UnitTests;

public sealed class SqlServerOptionsTests
{
    [Fact]
    public void The_connection_uses_the_process_identity_and_holds_no_credentials()
    {
        var built = new SqlConnectionStringBuilder(
            new SqlServerOptions
            {
                Server = @".\OPTIMA",
                Database = "CDN_Firma",
            }.ToConnectionString()
        );

        Assert.True(built.IntegratedSecurity);
        Assert.Empty(built.UserID);
        Assert.Empty(built.Password);
    }

    [Fact]
    public void By_default_the_server_certificate_is_verified()
    {
        var built = new SqlConnectionStringBuilder(new SqlServerOptions().ToConnectionString());

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, built.Encrypt);
        Assert.False(built.TrustServerCertificate);
    }
}
