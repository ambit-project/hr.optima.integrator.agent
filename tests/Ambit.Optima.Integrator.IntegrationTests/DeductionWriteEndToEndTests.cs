using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.IntegrationTests;

/// <summary>Runs only against a real Optima test company, where it may write and then removes what it wrote.</summary>
public sealed class DeductionWriteFactAttribute : FactAttribute
{
    public DeductionWriteFactAttribute()
    {
        if (
            Environment.GetEnvironmentVariable(OptimaSqlFactAttribute.ServerVariable) is null
            || Environment.GetEnvironmentVariable(EmployeeWriteFactAttribute.DatabaseVariable)
                is null
        )
            Skip =
                $"Set {OptimaSqlFactAttribute.ServerVariable} and {EmployeeWriteFactAttribute.DatabaseVariable}; "
                + "this test writes a deduction to that company.";
    }
}

/// <summary>
/// ADR 0007 against real Optima: <c>app.deduction_add</c> through Comarch's import procedures, as the account running
/// the tests. The deduction goes to the company's first employee with an import id, for the 2998-12 month, which no
/// payroll will reach, and is deleted afterwards.
/// </summary>
public sealed class DeductionWriteEndToEndTests
{
    private const int Year = 2998;
    private const int Month = 12;

    private static readonly string Server = Environment.GetEnvironmentVariable(
        OptimaSqlFactAttribute.ServerVariable
    )!;

    private static readonly string Company = Environment.GetEnvironmentVariable(
        EmployeeWriteFactAttribute.DatabaseVariable
    )!;

    private static SqlConnection Open() =>
        new(
            new SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = Company,
                IntegratedSecurity = true,
                TrustServerCertificate = true,
            }.ConnectionString
        );

    private static async Task<(Guid Id, string Code, int RowId, int Component)> PickAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) PRA_ImportRowId, PRA_Kod, PRA_PraId,
                (SELECT TOP (1) TWP_TwpId FROM CDN.TypWyplata
                 WHERE TWP_AlgPotracenie = 1 AND ISNULL(TWP_Nieaktywny, 0) = 0 AND ISNULL(TWP_Wycofany, 0) = 0
                   AND TWP_ImportRowId IS NOT NULL AND TWP_ImportRowId2 IS NOT NULL
                 ORDER BY TWP_TwpId)
            FROM CDN.PracKod
            WHERE TRY_CONVERT(uniqueidentifier, PRA_ImportRowId) IS NOT NULL
            ORDER BY PRA_PraId;
            """,
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{Company} has no employee with an import id.");
        return (
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3)
        );
    }

    private static async Task<(decimal Value, DateTime From, DateTime To, int Employee)?> ReadAsync(
        Guid key
    )
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT h.DOH_Wartosc1, h.DOH_WaznyOd, h.DOH_WaznyDo, d.DOD_PraId
            FROM CDN.DodatkiHist AS h JOIN CDN.Dodatki AS d ON d.DOD_DodId = h.DOH_DodId
            WHERE h.DOH_ImportRowId = @key AND d.DOD_ImportRowId = @key;
            """,
            connection
        );
        command.Parameters.AddWithValue("@key", key.ToString().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return (
            reader.GetDecimal(0),
            reader.GetDateTime(1),
            reader.GetDateTime(2),
            reader.GetInt32(3)
        );
    }

    private static async Task DeleteAsync(Guid key)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE CDN.DodatkiHist WHERE DOH_ImportRowId = @key;
            DELETE CDN.Dodatki WHERE DOD_ImportRowId = @key;
            COMMIT;
            """,
            connection
        );
        command.Parameters.AddWithValue("@key", key.ToString().ToUpperInvariant());
        await command.ExecuteNonQueryAsync();
    }

    [DeductionWriteFact]
    public async Task A_month_is_written_once_whichever_operation_repeats_it()
    {
        var (employeeId, code, rowId, component) = await PickAsync();
        var deductionId = Guid.NewGuid();
        var key = DeductionItem.OptimaKey(deductionId, Year, Month);
        var writer = new SqlDeductionWriter(
            Options.Create(new SqlServerOptions { Server = Server, TrustServerCertificate = true })
        );
        var item = new DeductionItem(deductionId, employeeId, code, component, 123.45m, "PLN");

        try
        {
            var first = await writer.AddAsync(
                new DeductionAddRequest(Guid.NewGuid(), Company, Year, Month, item),
                CancellationToken.None
            );
            Assert.Equal("Succeeded", first.Status);
            Assert.Equal(rowId, first.OptimaEmployeeId);

            var written = await ReadAsync(key);
            Assert.NotNull(written);
            Assert.Equal(123.45m, written.Value.Value);
            Assert.Equal(new DateTime(Year, Month, 1), written.Value.From);
            Assert.Equal(new DateTime(Year, Month, 31), written.Value.To);
            Assert.Equal(rowId, written.Value.Employee);

            var again = await writer.AddAsync(
                new DeductionAddRequest(Guid.NewGuid(), Company, Year, Month, item),
                CancellationToken.None
            );
            Assert.Equal("AlreadyExists", again.Status);
            Assert.Equal(first.OptimaAdditionId, again.OptimaAdditionId);
        }
        finally
        {
            await DeleteAsync(key);
        }
    }

    [DeductionWriteFact]
    public async Task An_unknown_employee_writes_nothing()
    {
        var (_, _, _, component) = await PickAsync();
        var deductionId = Guid.NewGuid();
        var writer = new SqlDeductionWriter(
            Options.Create(new SqlServerOptions { Server = Server, TrustServerCertificate = true })
        );

        var result = await writer.AddAsync(
            new DeductionAddRequest(
                Guid.NewGuid(),
                Company,
                Year,
                Month,
                new DeductionItem(deductionId, Guid.NewGuid(), "NOBODY-AMBIT", component, 1m)
            ),
            CancellationToken.None
        );

        Assert.Equal("EmployeeNotFound", result.Status);
        Assert.Null(await ReadAsync(DeductionItem.OptimaKey(deductionId, Year, Month)));
    }

    [DeductionWriteFact]
    public async Task More_than_two_decimals_is_refused_before_sql()
    {
        var writer = new SqlDeductionWriter(
            Options.Create(new SqlServerOptions { Server = Server, TrustServerCertificate = true })
        );

        var refused = await Assert.ThrowsAsync<DeductionRejectedException>(() =>
            writer.AddAsync(
                new DeductionAddRequest(
                    Guid.NewGuid(),
                    Company,
                    Year,
                    Month,
                    new DeductionItem(Guid.NewGuid(), Guid.NewGuid(), "X", 1, 1.005m)
                ),
                CancellationToken.None
            )
        );
        Assert.Equal(51024, refused.Number);
    }
}
