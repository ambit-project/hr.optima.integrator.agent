using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Ambit.Optima.Integrator.Setup;

namespace Ambit.Optima.Integrator.UnitTests;

public sealed class SetupTests
{
    private static readonly Dictionary<string, string> AllVariables = new()
    {
        ["AgentDatabase"] = "agent-ambit",
        ["ServiceLogin"] = @"NT SERVICE\AmbitOptimaIntegrator",
        ["ConfigDatabase"] = "CDN_KNF_Konfiguracja",
        ["FirmDatabase"] = "CDN_Firma",
    };

    [Theory]
    [InlineData("001-database.sql")]
    [InlineData("002-tables.sql")]
    [InlineData("003-procedures.sql")]
    [InlineData("004-service-account.sql")]
    [InlineData("005-firm.sql")]
    [InlineData("006-firm-revoke.sql")]
    public void Every_script_is_embedded_and_needs_only_known_variables(string name)
    {
        var batches = SqlScript.Batches(SqlScript.Load(name), AllVariables);

        Assert.NotEmpty(batches);
        Assert.DoesNotContain(batches, batch => batch.Contains("$("));
    }

    [Fact]
    public void A_script_splits_on_GO_alone_on_a_line_and_fills_in_variables()
    {
        var batches = SqlScript.Batches(
            "USE [$(FirmDatabase)];\r\ngo\r\nSELECT 'GOOD GO';\nGO  \n\nGO\n",
            AllVariables
        );

        Assert.Equal(2, batches.Count);
        Assert.Contains("[CDN_Firma]", batches[0]);
        Assert.Contains("SELECT 'GOOD GO';", batches[1]);
    }

    [Fact]
    public void An_unknown_variable_stops_the_script()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlScript.Batches("SELECT $(Missing);", AllVariables)
        );
        Assert.Contains("Missing", exception.Message);
    }

    [Fact]
    public void Apply_needs_an_explicit_choice_of_companies()
    {
        Assert.Throws<ArgumentException>(() =>
            SetupArguments.Parse(["apply", "--server", ".", "--app-dir", @"C:\a"])
        );
    }

    [Fact]
    public void Arguments_keep_the_chosen_companies_and_default_the_configuration_database()
    {
        var parsed = SetupArguments.Parse([
            "apply",
            "--server",
            @".\OPTIMA",
            "--app-dir",
            @"C:\a",
            "--firms",
            "A, B",
        ]);

        Assert.Equal(@".\OPTIMA", parsed.Server);
        Assert.Equal("CDN_KNF_Konfiguracja", parsed.ConfigDatabase);
        Assert.Equal(["A", "B"], parsed.Firms);
    }

    [Theory]
    [InlineData]
    [InlineData("uninstall", "--server", ".")]
    [InlineData("firms", "--server")]
    [InlineData("firms")]
    public void Bad_arguments_are_refused(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => SetupArguments.Parse(args));
    }

    [Fact]
    public void A_new_configuration_points_at_the_server()
    {
        var sql = JsonNode.Parse(WorkerConfiguration.Merge(null, @".\OPTIMA"))!["Sql"]!;

        Assert.Equal(@".\OPTIMA", sql["Server"]!.GetValue<string>());
        Assert.True(sql["Encrypt"]!.GetValue<bool>());
        Assert.True(sql["TrustServerCertificate"]!.GetValue<bool>());
    }

    private const string Token =
        "optima_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string TokenFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_token_switches_the_connection_on_with_the_given_address()
    {
        var api = JsonNode.Parse(
            WorkerConfiguration.Merge(null, ".", new AmbitSettings("https://ambit.example", Token))
        )!["AmbitApi"]!;

        Assert.True(api["Enabled"]!.GetValue<bool>());
        Assert.Equal("https://ambit.example", api["BaseUrl"]!.GetValue<string>());
        Assert.Equal(Token, api["Key"]!.GetValue<string>());
    }

    [Fact]
    public void Without_a_token_the_connection_stays_off_and_the_default_address_is_set()
    {
        var api = JsonNode.Parse(
            WorkerConfiguration.Merge(null, ".", new AmbitSettings(null, null))
        )!["AmbitApi"]!;

        Assert.Null(api["Enabled"]);
        Assert.Null(api["Key"]);
        Assert.Equal("https://app.ambitsys.eu", api["BaseUrl"]!.GetValue<string>());
    }

    [Fact]
    public void An_upgrade_with_nothing_given_keeps_the_connection()
    {
        const string existing = """
            {"AmbitApi":{"Enabled":true,"BaseUrl":"https://own.example","Key":"kept","PollSeconds":5}}
            """;

        var api = JsonNode.Parse(
            WorkerConfiguration.Merge(existing, ".", new AmbitSettings(null, null))
        )!["AmbitApi"]!;

        Assert.True(api["Enabled"]!.GetValue<bool>());
        Assert.Equal("https://own.example", api["BaseUrl"]!.GetValue<string>());
        Assert.Equal("kept", api["Key"]!.GetValue<string>());
        Assert.Equal(5, api["PollSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void A_new_token_replaces_the_old_one_and_keeps_the_rest()
    {
        const string existing = """
            {"AmbitApi":{"Enabled":false,"BaseUrl":"https://own.example","Key":"old","PollSeconds":5}}
            """;

        var api = JsonNode.Parse(
            WorkerConfiguration.Merge(existing, ".", new AmbitSettings(null, Token))
        )!["AmbitApi"]!;

        Assert.True(api["Enabled"]!.GetValue<bool>());
        Assert.Equal(Token, api["Key"]!.GetValue<string>());
        Assert.Equal("https://own.example", api["BaseUrl"]!.GetValue<string>());
        Assert.Equal(5, api["PollSeconds"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, "https://app.ambitsys.eu", false)]
    [InlineData("""{"AmbitApi":{"BaseUrl":""}}""", "https://app.ambitsys.eu", false)]
    [InlineData(
        """{"AmbitApi":{"BaseUrl":"https://own.example","Key":"x"}}""",
        "https://own.example",
        true
    )]
    public void The_machine_says_its_address_and_whether_it_has_a_token(
        string? existing,
        string url,
        bool hasToken
    )
    {
        var machine = WorkerConfiguration.ReadAmbit(existing);

        Assert.Equal(url, machine.Url);
        Assert.Equal(hasToken, machine.Token is not null);
    }

    [Theory]
    [InlineData("http://ambit.example")]
    [InlineData("ftp://ambit.example")]
    [InlineData("ambit.example")]
    public void An_address_that_would_expose_the_token_is_refused(string url)
    {
        Assert.Throws<ArgumentException>(() => AmbitSettings.From(url, ""));
    }

    [Theory]
    [InlineData("https://ambit.example/", "https://ambit.example")]
    [InlineData(" https://ambit.example ", "https://ambit.example")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("", null)]
    public void An_address_is_tidied(string url, string? expected)
    {
        Assert.Equal(expected, AmbitSettings.From(url, "").Url);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("optima_short")]
    [InlineData("optima_0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("optima_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void A_mistyped_token_is_refused_without_repeating_it(string token)
    {
        var path = TokenFile(token);
        try
        {
            var exception = Assert.Throws<ArgumentException>(() => AmbitSettings.From("", path));
            Assert.DoesNotContain(token, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_token_file_may_end_with_a_new_line()
    {
        var path = TokenFile(Token + "\r\n");
        try
        {
            Assert.Equal(Token, AmbitSettings.From("", path).Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("No such host.");
    }

    [Fact]
    public async Task Checking_a_token_reads_a_route_that_takes_no_work()
    {
        var handler = new Answering(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);

        var problem = await AmbitConnectionCheck.FindProblemAsync(
            http,
            "https://ambit.example/",
            Token,
            CancellationToken.None
        );

        Assert.Null(problem);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal(
            "https://ambit.example/api/optima/agent/connection",
            handler.Request.RequestUri!.ToString()
        );
        Assert.Equal(Token, handler.Request.Headers.GetValues("X-Optima-Agent-Key").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Anything_but_no_content_is_a_problem_that_does_not_repeat_the_token(
        HttpStatusCode status
    )
    {
        using var http = new HttpClient(new Answering(status));

        var problem = await AmbitConnectionCheck.FindProblemAsync(
            http,
            "https://ambit.example",
            Token,
            CancellationToken.None
        );

        Assert.NotNull(problem);
        Assert.DoesNotContain(Token, problem);
    }

    [Fact]
    public async Task An_unreachable_ambit_is_a_problem_and_not_a_crash()
    {
        using var http = new HttpClient(new Unreachable());

        var problem = await AmbitConnectionCheck.FindProblemAsync(
            http,
            "https://ambit.example",
            Token,
            CancellationToken.None
        );

        Assert.Contains("No such host.", problem);
    }

    [Fact]
    public void Apply_takes_the_connection_to_ambit()
    {
        var parsed = SetupArguments.Parse([
            "apply",
            "--server",
            ".",
            "--app-dir",
            @"C:\a",
            "--firms",
            "A",
            "--ambit-url",
            "https://ambit.example",
            "--ambit-token-file",
            @"C:\t.txt",
        ]);

        Assert.Equal("https://ambit.example", parsed.AmbitUrl);
        Assert.Equal(@"C:\t.txt", parsed.AmbitTokenFile);
    }

    [Fact]
    public void Asking_about_ambit_needs_no_sql_server()
    {
        Assert.Equal(
            SetupArguments.CheckAmbitCommand,
            SetupArguments.Parse(["check-ambit", "--ambit-url", "https://a.example"]).Command
        );
        Assert.Equal(
            SetupArguments.AmbitSettingsCommand,
            SetupArguments.Parse(["ambit-settings", "--app-dir", @"C:\a"]).Command
        );
        Assert.Throws<ArgumentException>(() => SetupArguments.Parse(["ambit-settings"]));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_settings_file_opens_only_to_the_service_administrators_and_the_system()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory();
        try
        {
            // A real account, as the agent's own exists only once the service does.
            WorkerConfiguration.Write(
                directory.FullName,
                ".",
                new AmbitSettings(null, Token),
                @"NT AUTHORITY\LOCAL SERVICE"
            );

            var path = WorkerConfiguration.SettingsPath(directory.FullName);
            var security = new FileInfo(path).GetAccessControl();
            var rights = security
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToDictionary(
                    rule => (SecurityIdentifier)rule.IdentityReference,
                    rule => rule.FileSystemRights & ~FileSystemRights.Synchronize
                );

            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(3, rights.Count);
            Assert.Equal(
                FileSystemRights.Read,
                rights[new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null)]
            );
            Assert.Equal(
                FileSystemRights.FullControl & ~FileSystemRights.Synchronize,
                rights[new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]
            );
            Assert.Equal(
                FileSystemRights.FullControl & ~FileSystemRights.Synchronize,
                rights[new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)]
            );
            Assert.Contains(Token, File.ReadAllText(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_upgrade_keeps_what_an_administrator_set()
    {
        const string existing = """
            {"Writes":{"Enabled":true},"Sql":{"Server":"old","TrustServerCertificate":false}}
            """;

        var root = JsonNode.Parse(WorkerConfiguration.Merge(existing, "new"))!;

        Assert.True(root["Writes"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("new", root["Sql"]!["Server"]!.GetValue<string>());
        Assert.False(root["Sql"]!["TrustServerCertificate"]!.GetValue<bool>());
    }
}
