using Ambit.Optima.Integrator.SqlServer;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.IntegrationTests;

/// <summary>The SQL Server classes wired as the Worker wires them, against one instance.</summary>
public sealed class TestSql
{
    public TestSql(SqlServerOptions options, Func<string, string>? connectionString = null)
    {
        Connections = connectionString is null
            ? new SqlConnections(Options.Create(options))
            : new SqlConnections(Options.Create(options), connectionString);
        Companies = new OptimaCompanies(Connections);
    }

    public TestSql(string server)
        : this(new SqlServerOptions { Server = server, TrustServerCertificate = true }) { }

    public SqlConnections Connections { get; }
    public OptimaCompanies Companies { get; }

    public SqlEmployeeWriter EmployeeWriter() => new(Connections, Companies);

    public SqlDeductionWriter DeductionWriter() => new(Connections, Companies);

    public SqlPayrollComponentReader PayrollComponentReader() => new(Connections, Companies);

    public IntegratorDatabaseMaintenance Maintenance(SqlServerOptions options) =>
        new(
            Connections,
            Options.Create(options),
            Microsoft
                .Extensions
                .Logging
                .Abstractions
                .NullLogger<IntegratorDatabaseMaintenance>
                .Instance
        );
}
