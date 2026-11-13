using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Diagnostics;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Core.Payrolls;
using Ambit.Optima.Integrator.Infrastructure.AmbitApi;
using Ambit.Optima.Integrator.Ipc;
using Ambit.Optima.Integrator.SqlServer;
using Ambit.Optima.Integrator.SqlServer.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "AmbitOptimaIntegrator");

builder.Services.Configure<SqlServerOptions>(
    builder.Configuration.GetSection(SqlServerOptions.SectionName)
);
builder.Services.Configure<IpcEndpoint>(builder.Configuration.GetSection(IpcEndpoint.SectionName));
builder.Services.Configure<WriteOptions>(
    builder.Configuration.GetSection(WriteOptions.SectionName)
);

builder.Services.AddSingleton<SqlLogChannel>();
builder.Logging.Services.AddSingleton<ILoggerProvider, SqlLoggerProvider>();
builder.Services.AddHostedService<SqlLogWriter>();
builder.Services.AddHostedService<IntegratorDatabaseMaintenance>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SqlConnections>();
builder.Services.AddSingleton<OptimaCompanies>();
builder.Services.AddSingleton<ISqlDiagnostics, SqlDiagnostics>();
builder.Services.AddSingleton<IEmployeeWriter, SqlEmployeeWriter>();
builder.Services.AddSingleton<IDeductionWriter, SqlDeductionWriter>();
builder.Services.AddSingleton<IpcDispatcher>();
builder.Services.AddHostedService<IpcServer>();

builder.Services.AddSingleton<IPayrollComponentReader, SqlPayrollComponentReader>();
builder.Services.AddSingleton<IPayrollReader, SqlPayrollReader>();

builder.Services.AddSingleton<IValidateOptions<AmbitApiOptions>, AmbitApiOptionsValidator>();
builder
    .Services.AddOptions<AmbitApiOptions>()
    .Bind(builder.Configuration.GetSection(AmbitApiOptions.SectionName))
    .ValidateOnStart();

// Off unless switched on locally: without it the integrator keeps serving the local UI and talks to nobody.
if (builder.Configuration.GetValue<bool>($"{AmbitApiOptions.SectionName}:Enabled"))
{
    builder.Services.AddHttpClient<IAmbitApiClient, HttpAmbitApiClient>(
        (services, http) =>
        {
            var api = services.GetRequiredService<IOptions<AmbitApiOptions>>().Value;
            http.BaseAddress = new Uri(api.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(api.RequestTimeoutSeconds);
            http.DefaultRequestHeaders.Add(HttpAmbitApiClient.KeyHeader, api.Key);
        }
    );
    builder.Services.AddSingleton<OperationRunner>();
    builder.Services.AddHostedService<OperationPoller>();
}

builder.Build().Run();
