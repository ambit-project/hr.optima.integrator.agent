using System.Text.RegularExpressions;
using Ambit.Optima.Integrator.Core;
using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Core.Payrolls;
using Ambit.Optima.Integrator.Setup;
using Ambit.Optima.Integrator.SqlServer;
using Dapper;
using JetBrains.Annotations;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Ambit.Optima.Integrator.IntegrationTests;

/// <summary>Runs where Docker can start a Linux SQL Server container.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public static bool Available =>
        Environment.GetEnvironmentVariable("DOCKER_HOST") is not null
        || (OperatingSystem.IsLinux() && File.Exists("/var/run/docker.sock"));

    public DockerFactAttribute()
    {
        if (!Available)
            Skip = "Needs Docker with Linux containers.";
    }
}

/// <summary>
/// SQL Server with the integrator's migrations and a stand-in for Optima (<c>FakeOptima/*.sql</c>): a configuration
/// database listing four companies. <c>CDN_Alpha</c> is a company as it should be; <c>CDN_Beta</c> holds a tax number
/// other than the list's; <c>CDN_Gone</c> is listed but has no database; <c>CDN_Off</c> is switched off.
/// </summary>
[UsedImplicitly] // Created by xUnit as a class fixture.
public sealed partial class FakeOptimaFixture : IAsyncLifetime
{
    public const string Alpha = "CDN_Alpha";
    public const string Beta = "CDN_Beta";
    public const string Gone = "CDN_Gone";

    private readonly MsSqlContainer _container = new MsSqlBuilder(
        "mcr.microsoft.com/mssql/server:2022-latest"
    ).Build();

    public SqlServerOptions Options { get; } = new();

    public TestSql Sql { get; private set; } = null!;

    private string ConnectionString(string database) =>
        new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = database,
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (!DockerFactAttribute.Available)
            return;

        await _container.StartAsync();
        await RunAsync("master", $"CREATE DATABASE [{Options.ConfigDatabase}];");
        await RunAsync(Options.ConfigDatabase, Script("config.sql"));
        await RunAsync(
            Options.ConfigDatabase,
            """
            INSERT CDN.Bazy (Baz_NazwaBazy, Baz_NIP, Baz_Nieaktywna) VALUES
                (N' CDN_Alpha ', N'111-111-11-11', 0),
                (N'CDN_Beta', N'2222222222', NULL),
                (N'CDN_Gone', NULL, 0),
                (N'CDN_Off', NULL, 1);
            """
        );
        foreach (var (company, taxNumber) in new[] { (Alpha, "1111111111"), (Beta, "9999999999") })
        {
            await RunAsync("master", $"CREATE DATABASE [{company}];");
            await RunAsync(company, Script("company.sql"));
            await RunAsync(
                company,
                $"INSERT CDN.Firma (Fir_Numer, Fir_Wartosc) VALUES (1101, N'{taxNumber}');"
            );
        }

        // Twice: the second run finds every migration applied and changes nothing.
        var installer = new DatabaseInstaller(".", Options.ConfigDatabase, ConnectionString);
        await installer.MigrateAsync(CancellationToken.None);
        await installer.MigrateAsync(CancellationToken.None);

        Sql = new TestSql(Options, ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<T> QueryAsync<T>(string database, string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString(database));
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    public async Task RunAsync(string database, string script)
    {
        await using var connection = new SqlConnection(ConnectionString(database));
        await connection.OpenAsync();
        foreach (var batch in BatchSeparator().Split(script).Where(b => b.Trim() != ""))
            await connection.ExecuteAsync(batch);
    }

    private static string Script(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FakeOptima", name));

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();
}

public sealed class FakeOptimaTests(FakeOptimaFixture optima) : IClassFixture<FakeOptimaFixture>
{
    private static EmployeeAddRequest Employee(string code, string? database = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            code,
            "Kowalski",
            "Jan",
            database,
            DateOfBirth: new DateOnly(1990, 5, 12),
            Pesel: "90051212345",
            CitizenshipCode: "UA",
            City: "Lwów",
            AddressCountryCode: "UA",
            DocumentKind: "Passport",
            DocumentNumber: "FA1234567",
            PositionName: "Magazynier",
            StartsOn: new DateOnly(2026, 11, 1),
            EndsOn: new DateOnly(2027, 10, 31)
        );

    private Task<int> CountAsync(string database, string sql, object? parameters = null) =>
        optima.QueryAsync<int>(database, sql, parameters);

    [DockerFact]
    public async Task The_migrations_are_recorded_once()
    {
        Assert.Equal(
            2,
            await CountAsync(
                DatabaseInstaller.IntegratorDatabase,
                "SELECT COUNT(*) FROM dbo.SchemaVersions;"
            )
        );
    }

    [DockerFact]
    public async Task An_employee_goes_to_every_listed_company_each_on_its_own()
    {
        var request = Employee("KOWJAN1");

        var results = await optima.Sql.EmployeeWriter().AddAsync(request, CancellationToken.None);

        Assert.Equal(
            [FakeOptimaFixture.Alpha, FakeOptimaFixture.Beta, FakeOptimaFixture.Gone],
            results.Select(r => r.DatabaseName)
        );
        var alpha = results[0];
        Assert.Equal("Succeeded", alpha.Status);
        Assert.Equal("OPT0007", alpha.WarningCodes);
        Assert.NotNull(alpha.OptimaContractId);

        Assert.Equal("Failed", results[1].Status);
        Assert.Equal(FailureCodes.TaxNumberMismatch, results[1].ErrorNumber);
        Assert.Equal("NoAccess", results[2].Status);

        var contract = await optima.QueryAsync<(string?, string?, string?, string?, DateTime?)>(
            FakeOptimaFixture.Alpha,
            """
            SELECT e.PRE_Obywatelstwo, e.PRE_MLDKodKraju, e.PRE_Paszport, e.PRE_Plec, e.PRE_ZatrudnionyDo
            FROM CDN.PracEtaty AS e JOIN CDN.PracKod AS k ON k.PRA_PraId = e.PRE_PraId
            WHERE k.PRA_ImportRowId = @id;
            """,
            new { id = request.EmployeeId.ToString()!.ToUpperInvariant() }
        );
        Assert.Equal(
            ("UKRAIŃSKIE", "UA", "FA1234567", ""),
            (contract.Item1, contract.Item2, contract.Item3, contract.Item4)
        );
        Assert.Equal(new DateTime(2027, 10, 31), contract.Item5);
    }

    [DockerFact]
    public async Task A_repeated_call_adds_nobody_twice()
    {
        var writer = optima.Sql.EmployeeWriter();
        var request = Employee("NOWADA1", FakeOptimaFixture.Alpha);

        var first = await writer.AddAsync(request, CancellationToken.None);
        var again = await writer.AddAsync(request, CancellationToken.None);
        var otherOperation = await writer.AddAsync(
            request with
            {
                OperationId = Guid.NewGuid(),
            },
            CancellationToken.None
        );

        Assert.Equal("Succeeded", first.Single().Status);
        Assert.Equal(first.Single(), again.Single());
        Assert.Equal("AlreadyExists", otherOperation.Single().Status);
        Assert.Null(otherOperation.Single().WarningCodes);
        Assert.Equal(
            1,
            await CountAsync(
                FakeOptimaFixture.Alpha,
                "SELECT COUNT(*) FROM CDN.PracKod WHERE PRA_Kod = N'NOWADA1';"
            )
        );
    }

    [DockerFact]
    public async Task The_same_acronym_from_another_source_is_reported()
    {
        await optima.RunAsync(
            FakeOptimaFixture.Alpha,
            "INSERT CDN.PracKod (PRA_Kod, PRA_ImportRowId) VALUES (N'ZIEPIO1', NULL);"
        );

        var result = await optima
            .Sql.EmployeeWriter()
            .AddAsync(Employee("ZIEPIO1", FakeOptimaFixture.Alpha), CancellationToken.None);

        Assert.Equal("AlreadyExists", result.Single().Status);
        Assert.Equal("MATCHED_BY_ACRONYM_ONLY", result.Single().WarningCodes);
    }

    [DockerFact]
    public async Task An_error_in_Optima_rolls_the_company_back_and_keeps_only_its_codes()
    {
        var request = Employee("FAILED1", FakeOptimaFixture.Alpha) with { LastName = "Fail" };

        var result = (
            await optima.Sql.EmployeeWriter().AddAsync(request, CancellationToken.None)
        ).Single();

        Assert.Equal("Failed", result.Status);
        Assert.Equal(50000, result.ErrorNumber);
        Assert.Equal("CDN.DodajPracownika", result.ErrorProcedure);
        Assert.Equal("OPT0042", result.OptimaErrorCodes);
        Assert.Equal(
            0,
            await CountAsync(
                FakeOptimaFixture.Alpha,
                "SELECT COUNT(*) FROM CDN.PracKod WHERE PRA_Kod = N'FAILED1';"
            )
        );
        Assert.Equal(
            0,
            await CountAsync(
                DatabaseInstaller.IntegratorDatabase,
                "SELECT COUNT(*) FROM integrator.employee_call WHERE masked_parameters LIKE N'%90051212345%';"
            )
        );
    }

    [DockerFact]
    public async Task A_bad_request_touches_no_company()
    {
        var refused = await Assert.ThrowsAsync<EmployeeRejectedException>(() =>
            optima
                .Sql.EmployeeWriter()
                .AddAsync(Employee("X", "CDN_Unknown"), CancellationToken.None)
        );
        Assert.Equal(RejectionCodes.NoCompanyMatches, refused.Number);
    }

    [DockerFact]
    public async Task An_interrupted_call_becomes_unknown_and_may_be_tried_again()
    {
        var request = Employee("PRZERW1", FakeOptimaFixture.Alpha);
        await optima.RunAsync(
            DatabaseInstaller.IntegratorDatabase,
            $"""
            INSERT integrator.employee_call
                (operation_id, database_name, employee_id, employee_code, status, masked_parameters)
            VALUES ('{request.OperationId}', N'CDN_Alpha', '{request.EmployeeId}', 'PRZERW1', 'Started', N'-');
            """
        );

        using var stop = new CancellationTokenSource();
        var maintenance = optima.Sql.Maintenance(optima.Options);
        await maintenance.StartAsync(stop.Token);
        await WaitForAsync(async () =>
            await CountAsync(
                DatabaseInstaller.IntegratorDatabase,
                "SELECT COUNT(*) FROM integrator.employee_call WHERE employee_code = 'PRZERW1' AND status = 'Unknown';"
            ) == 1
        );
        await maintenance.StopAsync(CancellationToken.None);

        var retried = (
            await optima.Sql.EmployeeWriter().AddAsync(request, CancellationToken.None)
        ).Single();
        Assert.Equal("Succeeded", retried.Status);
        Assert.Equal(2, retried.Attempt);
    }

    [DockerFact]
    public async Task Payroll_components_come_per_company_with_their_kind()
    {
        var companies = await optima.Sql.PayrollComponentReader().ReadAsync(CancellationToken.None);

        var alpha = companies.Single(c => c.DatabaseName == FakeOptimaFixture.Alpha);
        Assert.Equal(CompanyReadStatus.Read, alpha.Status);
        Assert.Equal("1111111111", alpha.TaxNumber);
        Assert.Equal(
            ["Dodatek stażowy", "Potrącenie za mieszkanie", "Stare potrącenie"],
            alpha.Components.Select(c => c.Name)
        );
        Assert.Contains(
            alpha.Components,
            c => c is { OptimaId: 340, IsActive: false, IsWithdrawn: false }
        );
        Assert.Contains(
            alpha.Components,
            c => c is { OptimaId: 338, IsActive: true, IsWithdrawn: false }
        );
        var deduction = alpha.Components.Single(c => c.OptimaId == 338);
        Assert.True(deduction.IsDeduction);
        Assert.Equal("pot.mieszkanie", deduction.ShortName);
        Assert.Equal("8468F49B-C16E-4529-8197-D94D93DDADD8", deduction.ImportRowId);
        Assert.False(alpha.Components.Single(c => c.OptimaId == 339).IsDeduction);
        Assert.Equal(12, alpha.Components.Single(c => c.OptimaId == 339).StandardId);

        Assert.Equal(
            CompanyReadStatus.NoAccess,
            companies.Single(c => c.DatabaseName == FakeOptimaFixture.Gone).Status
        );
        Assert.DoesNotContain(companies, c => c.DatabaseName == "CDN_Off");
    }

    [DockerFact]
    public async Task A_month_of_deductions_is_written_once()
    {
        var person = Employee("POTRAC1", FakeOptimaFixture.Alpha);
        await optima.Sql.EmployeeWriter().AddAsync(person, CancellationToken.None);
        var writer = optima.Sql.DeductionWriter();
        var item = new DeductionItem(Guid.NewGuid(), person.EmployeeId, "POTRAC1", 338, 500.00m);

        var first = await writer.AddAsync(
            new DeductionAddRequest(Guid.NewGuid(), FakeOptimaFixture.Alpha, 2026, 2, item),
            CancellationToken.None
        );
        var again = await writer.AddAsync(
            new DeductionAddRequest(Guid.NewGuid(), FakeOptimaFixture.Alpha, 2026, 2, item),
            CancellationToken.None
        );

        Assert.Equal("Succeeded", first.Status);
        Assert.Null(first.WarningCodes);
        Assert.Equal("AlreadyExists", again.Status);
        Assert.Equal(first.OptimaAdditionId, again.OptimaAdditionId);

        var key = DeductionItem
            .OptimaKey(item.DeductionId!.Value, 2026, 2)
            .ToString()
            .ToUpperInvariant();
        var written = await optima.QueryAsync<(decimal, DateTime, DateTime, string)>(
            FakeOptimaFixture.Alpha,
            """
            SELECT DOH_Wartosc1, DOH_WaznyOd, DOH_WaznyDo, DOH_TWP_ImportRowId
            FROM CDN.DodatkiHist WHERE DOH_ImportRowId = @key;
            """,
            new { key }
        );
        Assert.Equal(
            (
                500.00m,
                new DateTime(2026, 2, 1),
                new DateTime(2026, 2, 28),
                "8468F49B-C16E-4529-8197-D94D93DDADD8"
            ),
            written
        );
    }

    [DockerFact]
    public async Task A_deduction_needs_a_known_employee_and_an_active_deduction_component()
    {
        var person = Employee("POTRAC2", FakeOptimaFixture.Alpha);
        await optima.Sql.EmployeeWriter().AddAsync(person, CancellationToken.None);
        var writer = optima.Sql.DeductionWriter();

        Task<DeductionAddResult> AddAsync(Guid? employeeId, string code, int component) =>
            writer.AddAsync(
                new DeductionAddRequest(
                    Guid.NewGuid(),
                    FakeOptimaFixture.Alpha,
                    2026,
                    3,
                    new DeductionItem(Guid.NewGuid(), employeeId, code, component, 10m)
                ),
                CancellationToken.None
            );

        Assert.Equal("EmployeeNotFound", (await AddAsync(Guid.NewGuid(), "NOBODY", 338)).Status);
        Assert.Equal(
            "ComponentNotFound",
            (await AddAsync(person.EmployeeId, "POTRAC2", 339)).Status
        );
        Assert.Equal(
            "ComponentNotFound",
            (await AddAsync(person.EmployeeId, "POTRAC2", 340)).Status
        );

        var byAcronym = await AddAsync(Guid.NewGuid(), "POTRAC2", 338);
        Assert.Equal("Succeeded", byAcronym.Status);
        Assert.Equal("MATCHED_BY_ACRONYM_ONLY", byAcronym.WarningCodes);
    }

    [DockerFact]
    public async Task A_deduction_in_a_company_with_another_tax_number_fails()
    {
        var result = await optima
            .Sql.DeductionWriter()
            .AddAsync(
                new DeductionAddRequest(
                    Guid.NewGuid(),
                    FakeOptimaFixture.Beta,
                    2026,
                    4,
                    new DeductionItem(Guid.NewGuid(), Guid.NewGuid(), "X", 338, 1m)
                ),
                CancellationToken.None
            );

        Assert.Equal("Failed", result.Status);
        Assert.Equal(FailureCodes.TaxNumberMismatch, result.ErrorNumber);
    }

    [DockerFact]
    public async Task A_month_of_allowances_is_written_once_and_only_to_an_allowance()
    {
        var person = Employee("DODATEK1", FakeOptimaFixture.Alpha);
        await optima.Sql.EmployeeWriter().AddAsync(person, CancellationToken.None);
        var writer = optima.Sql.DeductionWriter();

        Task<DeductionAddResult> AddAsync(DeductionItem item) =>
            writer.AddAsync(
                new DeductionAddRequest(
                    Guid.NewGuid(),
                    FakeOptimaFixture.Alpha,
                    2026,
                    5,
                    item,
                    PayItemKind.Allowance
                ),
                CancellationToken.None
            );

        var item = new DeductionItem(Guid.NewGuid(), person.EmployeeId, "DODATEK1", 339, 150m);
        var first = await AddAsync(item);
        var again = await AddAsync(item);
        var onDeduction = await AddAsync(
            item with
            {
                DeductionId = Guid.NewGuid(),
                OptimaId = 338,
            }
        );

        Assert.Equal("Succeeded", first.Status);
        Assert.Equal("AlreadyExists", again.Status);
        Assert.Equal(first.OptimaAdditionId, again.OptimaAdditionId);
        Assert.Equal("ComponentNotFound", onDeduction.Status);
        Assert.Equal(
            "Allowance",
            await optima.QueryAsync<string>(
                DatabaseInstaller.IntegratorDatabase,
                "SELECT DISTINCT kind FROM integrator.deduction_call WHERE deduction_id = @id;",
                new { id = item.DeductionId }
            )
        );
    }

    [DockerFact]
    public async Task A_month_of_payroll_lists_comes_with_payouts_and_elements()
    {
        var read = await optima
            .Sql.PayrollReader()
            .ReadAsync(FakeOptimaFixture.Alpha, 2026, 11, CancellationToken.None);

        Assert.Equal(PayrollReadStatus.Read, read.Status);
        var payrolls = read.Payrolls!;
        Assert.Equal("1111111111", payrolls.TaxNumber);
        Assert.Equal([41, 42], payrolls.Lists.Select(l => l.OptimaId));

        var october = payrolls.Lists[0];
        Assert.Equal("EtatLP/10/2026", october.Number);
        Assert.Equal(new DateOnly(2026, 10, 1), october.PeriodFrom);
        Assert.Equal(new DateOnly(2026, 11, 10), october.DocumentDate);
        Assert.True(october.IsClosed);
        Assert.Null(payrolls.Lists[1].Name);
        Assert.False(payrolls.Lists[1].IsClosed);
        Assert.Empty(payrolls.Lists[1].Payouts);

        var known = october.Payouts.Single(p => p.OptimaId == 901);
        Assert.Equal(Guid.Parse("c0000000-0000-4000-8000-000000000001"), known.EmployeeId);
        Assert.Equal("LISTA1", known.EmployeeCode);
        Assert.Equal((6500.00m, 4712.34m, true), (known.Gross, known.Net, known.IsClosed));
        Assert.Equal(
            [(5001, (int?)339, 150.00m), (5002, null, 6350.00m)],
            known.Elements.Select(e => (e.OptimaId, e.ComponentOptimaId, e.Amount))
        );
        Assert.Null(october.Payouts.Single(p => p.OptimaId == 902).EmployeeId);
    }

    [DockerFact]
    public async Task Payroll_lists_of_a_company_out_of_reach_are_not_read()
    {
        var reader = optima.Sql.PayrollReader();

        Assert.Equal(
            PayrollReadStatus.NoAccess,
            (
                await reader.ReadAsync(FakeOptimaFixture.Gone, 2026, 11, CancellationToken.None)
            ).Status
        );
        Assert.Equal(
            PayrollReadStatus.NotListed,
            (await reader.ReadAsync("CDN_Off", 2026, 11, CancellationToken.None)).Status
        );
        var beta = await reader.ReadAsync(FakeOptimaFixture.Beta, 2026, 11, CancellationToken.None);
        Assert.Equal(PayrollReadStatus.Failed, beta.Status);
        Assert.Equal(FailureCodes.TaxNumberMismatch, beta.ErrorNumber);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }
        Assert.Fail("The condition was not met in time.");
    }
}
