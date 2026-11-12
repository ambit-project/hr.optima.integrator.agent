using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>An active company on Optima's list, with its tax number without dashes when the list has one.</summary>
public sealed record OptimaCompany(string DatabaseName, string? TaxNumber);

/// <summary>Optima's list of companies and the checks made before the integrator touches one of them.</summary>
public sealed class OptimaCompanies(SqlConnections connections)
{
    /// <summary>The <c>CDN.Firma</c> row that holds the company's tax number.</summary>
    private const int TaxNumberSetting = 1101;

    /// <summary>
    /// The active companies, in name order: all of them, or only <paramref name="databaseName"/>. Read on every call, so
    /// a company switched off in Optima is not written to.
    /// </summary>
    public async Task<IReadOnlyList<OptimaCompany>> ListActiveAsync(
        string? databaseName,
        CancellationToken cancellationToken
    )
    {
        await using var config = await connections.OpenConfigAsync(cancellationToken);
        var companies = await config.QueryAsync<OptimaCompany>(
            new CommandDefinition(
                """
                SELECT
                    CONVERT(sysname, TRIM(Baz_NazwaBazy)) AS DatabaseName,
                    NULLIF(MAX(ISNULL(REPLACE(TRIM(Baz_NIP), N'-', N''), N'')), N'') AS TaxNumber
                FROM CDN.Bazy
                WHERE ISNULL(Baz_Nieaktywna, 0) = 0
                  AND TRIM(Baz_NazwaBazy) <> N''
                  AND (@databaseName IS NULL OR TRIM(Baz_NazwaBazy) = @databaseName)
                GROUP BY CONVERT(sysname, TRIM(Baz_NazwaBazy))
                ORDER BY DatabaseName;
                """,
                new { databaseName },
                commandTimeout: connections.CommandTimeout,
                cancellationToken: cancellationToken
            )
        );
        return companies.AsList();
    }

    /// <summary>
    /// Whether the Worker's account has a user in the company database. The installer creates one only for the companies
    /// an administrator selected, so this is the real boundary.
    /// </summary>
    public async Task<bool> HasAccessAsync(string databaseName, CancellationToken cancellationToken)
    {
        await using var config = await connections.OpenConfigAsync(cancellationToken);
        return await config.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT ISNULL(HAS_DBACCESS(@databaseName), 0);",
                    new { databaseName },
                    commandTimeout: connections.CommandTimeout,
                    cancellationToken: cancellationToken
                )
            ) == 1;
    }

    /// <summary>The tax number the company database holds, without dashes.</summary>
    public async Task<string?> ReadTaxNumberAsync(
        SqlConnection company,
        CancellationToken cancellationToken
    ) =>
        await company.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                """
                SELECT REPLACE(CONVERT(nvarchar(400), Fir_Wartosc), N'-', N'')
                FROM CDN.Firma
                WHERE Fir_Numer = @setting;
                """,
                new { setting = TaxNumberSetting },
                commandTimeout: connections.CommandTimeout,
                cancellationToken: cancellationToken
            )
        );

    /// <summary>
    /// Guards against a database that is not the company the list says it is. When the list has no tax number there is
    /// nothing to compare.
    /// </summary>
    public async Task<bool> IsSameCompanyAsync(
        SqlConnection company,
        OptimaCompany listed,
        CancellationToken cancellationToken
    ) =>
        listed.TaxNumber is null
        || (await ReadTaxNumberAsync(company, cancellationToken) ?? "") == listed.TaxNumber;
}

/// <summary>
/// Optima's messages can carry personal data, such as a PESEL. Only their <c>OPT</c> codes are kept: the parts of the
/// message, split on <c>#</c>, that are <c>OPT</c> followed by a digit and then only capitals and digits.
/// </summary>
public static partial class OptimaMessages
{
    public static string? Codes(IEnumerable<string?> messages)
    {
        var codes = messages
            .Where(message => message is not null)
            .SelectMany(message => message!.Split('#'))
            .Where(part => OptimaCode().IsMatch(part))
            .ToList();
        return codes.Count == 0 ? null : string.Join(',', codes);
    }

    public static string? Codes(string? message) => Codes([message]);

    [GeneratedRegex("^OPT[0-9][A-Z0-9]*$")]
    private static partial Regex OptimaCode();
}
