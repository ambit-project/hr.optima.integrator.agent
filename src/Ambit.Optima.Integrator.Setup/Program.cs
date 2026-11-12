using Ambit.Optima.Integrator.Setup;
using Microsoft.Data.SqlClient;

const int connectionProblem = 2;

try
{
    var arguments = SetupArguments.Parse(args);

    switch (arguments.Command)
    {
        case SetupArguments.AmbitSettingsCommand:
        {
            var machine = ReadMachine(arguments.AppDirectory);
            // Whether a token exists, never the token: this goes to the installer's log.
            Console.WriteLine($"{machine.Url}\t{(machine.Token is null ? 0 : 1)}");
            return 0;
        }
        case SetupArguments.CheckAmbitCommand:
        {
            var ambit = AmbitSettings.From(arguments.AmbitUrl, arguments.AmbitTokenFile);
            if (ambit is not { Url: { } url, Token: { } token })
                return 0;
            var problem = await FindProblemAsync(url, token);
            if (problem is null)
                return 0;
            await Console.Error.WriteLineAsync(problem);
            return connectionProblem;
        }
    }

    var database = new AgentDatabaseInstaller(arguments.Server, arguments.ConfigDatabase);

    if (arguments.Command == SetupArguments.FirmsCommand)
    {
        foreach (var firm in await database.ReadFirmsAsync(CancellationToken.None))
            Console.WriteLine($"{firm.DatabaseName}\t{(firm.HasAccess ? 1 : 0)}");
        return 0;
    }

    var settings = AmbitSettings.From(arguments.AmbitUrl, arguments.AmbitTokenFile);
    var firms = await database.CheckSelectionAsync(arguments.Firms, CancellationToken.None);
    // The NT SERVICE login cannot be created before the service exists, and neither can the settings file's rights.
    await ServiceRegistration.RegisterAsync(arguments.AppDirectory, CancellationToken.None);
    WorkerConfiguration.Write(
        arguments.AppDirectory,
        arguments.Server,
        settings,
        ServiceRegistration.Login
    );
    await database.ApplyAsync(firms, arguments.Firms, CancellationToken.None);
    await ServiceRegistration.StartAsync(CancellationToken.None);
    await ServiceRegistration.VerifyAsync(CancellationToken.None);

    // Ambit being out of reach does not fail an install: the client's firewall may open later.
    var configured = ReadMachine(arguments.AppDirectory);
    if (configured.Token is { } configuredToken)
        Console.WriteLine(
            await FindProblemAsync(configured.Url, configuredToken) ?? "Ambit accepts the token."
        );
    return 0;
}
catch (Exception exception)
    when (exception
            is ArgumentException
                or InvalidOperationException
                or SqlException
                or IOException
                or UnauthorizedAccessException
                or OperationCanceledException
    )
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 1;
}

static AmbitOnMachine ReadMachine(string appDirectory)
{
    var path = WorkerConfiguration.SettingsPath(appDirectory);
    return WorkerConfiguration.ReadAmbit(File.Exists(path) ? File.ReadAllText(path) : null);
}

static async Task<string?> FindProblemAsync(string url, string token)
{
    using var http = new HttpClient();
    http.Timeout = TimeSpan.FromSeconds(15);
    return await AmbitConnectionCheck.FindProblemAsync(http, url, token, CancellationToken.None);
}
