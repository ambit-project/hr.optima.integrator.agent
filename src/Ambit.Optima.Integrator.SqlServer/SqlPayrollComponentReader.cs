using Ambit.Optima.Integrator.Core.Operations;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>
/// Reads every payroll component, deductions and allowances, active, inactive and withdrawn alike, of every active
/// company. A company's failure
/// stays that company's, and its components come back only when the whole read succeeded.
/// </summary>
public sealed class SqlPayrollComponentReader(SqlConnections connections, OptimaCompanies companies)
    : IPayrollComponentReader
{
    /// <summary>
    /// <c>TWP_AlgPotracenie = 1</c> marks a deduction. <c>TWP_TwpId</c> differs between companies, so an id means
    /// something only together with its company.
    /// </summary>
    private const string ComponentsSql = """
        SELECT
            TWP_TwpId AS OptimaId,
            CONVERT(nvarchar(40), TRIM(TWP_Nazwa)) AS Name,
            NULLIF(TRIM(TWP_Skrot), N'') AS ShortName,
            CONVERT(bit, CASE WHEN TWP_AlgPotracenie = 1 THEN 1 ELSE 0 END) AS IsDeduction,
            CONVERT(int, TWP_RodzajZrodla) AS SourceKind,
            CONVERT(int, TWP_Standardowy) AS StandardId,
            CONVERT(decimal(15, 2), TWP_AlgKwota) AS DefaultAmount,
            CONVERT(bit, CASE WHEN ISNULL(TWP_PKZP, 0) <> 0 THEN 1 ELSE 0 END) AS IsPkzp,
            CONVERT(bit, CASE WHEN ISNULL(TWP_ZajecieWynagr, 0) <> 0 THEN 1 ELSE 0 END) AS IsSeizure,
            CONVERT(bit, CASE WHEN ISNULL(TWP_Nieaktywny, 0) = 0 THEN 1 ELSE 0 END) AS IsActive,
            CONVERT(bit, CASE WHEN ISNULL(TWP_Wycofany, 0) <> 0 THEN 1 ELSE 0 END) AS IsWithdrawn,
            CONVERT(varchar(36), TWP_ImportRowId) AS ImportRowId,
            CONVERT(varchar(36), TWP_ImportRowId2) AS ImportRowId2
        FROM CDN.TypWyplata
        ORDER BY Name, OptimaId;
        """;

    public async Task<IReadOnlyList<CompanyRead>> ReadAsync(CancellationToken cancellationToken)
    {
        var reads = new List<CompanyRead>();
        foreach (var company in await companies.ListActiveAsync(null, cancellationToken))
            reads.Add(await ReadCompanyAsync(company, cancellationToken));
        return reads;
    }

    private async Task<CompanyRead> ReadCompanyAsync(
        OptimaCompany company,
        CancellationToken cancellationToken
    )
    {
        if (!await companies.HasAccessAsync(company.DatabaseName, cancellationToken))
            return new CompanyRead(
                company.DatabaseName,
                CompanyReadStatus.NoAccess,
                null,
                null,
                []
            );

        try
        {
            await using var connection = await connections.OpenCompanyAsync(
                company.DatabaseName,
                cancellationToken
            );
            var taxNumber = await companies.ReadTaxNumberAsync(connection, cancellationToken);
            var components = await connection.QueryAsync<PayrollComponent>(
                new CommandDefinition(
                    ComponentsSql,
                    commandTimeout: connections.CommandTimeout,
                    cancellationToken: cancellationToken
                )
            );
            return new CompanyRead(
                company.DatabaseName,
                CompanyReadStatus.Read,
                taxNumber,
                null,
                components.AsList()
            );
        }
        catch (SqlException exception)
        {
            return new CompanyRead(
                company.DatabaseName,
                CompanyReadStatus.Failed,
                null,
                exception.Number,
                []
            );
        }
    }
}
