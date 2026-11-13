using Ambit.Optima.Integrator.Core;
using Ambit.Optima.Integrator.Core.Payrolls;
using Dapper;
using JetBrains.Annotations;
using Microsoft.Data.SqlClient;

namespace Ambit.Optima.Integrator.SqlServer;

/// <summary>
/// Reads one company's payroll lists of one month, with each employee's payout and its elements. It writes nothing.
/// </summary>
/// <remarks>
/// <para>The month is the list's own (<c>LPL_Rok</c>, <c>LPL_Miesiac</c>), the month the list is declared in. It can
/// differ from the period the list covers (<c>LPL_DataOd</c> to <c>LPL_DataDo</c>), which is sent along.</para>
/// <para>A payout's <c>WPL_Brutto</c> is its gross and <c>WPL_Wartosc</c> what is paid out, so the net. An element's
/// <c>WPE_Wartosc</c> is its gross value.</para>
/// </remarks>
public sealed class SqlPayrollReader(SqlConnections connections, OptimaCompanies companies)
    : IPayrollReader
{
    private const string PayrollSql = """
        SELECT
            LPL_LplId AS OptimaId,
            NULLIF(TRIM(LPL_NumerPelny), N'') AS Number,
            NULLIF(TRIM(LPL_Nazwa), N'') AS Name,
            LPL_DataOd AS PeriodFrom,
            LPL_DataDo AS PeriodTo,
            LPL_DataDok AS DocumentDate,
            CONVERT(bit, CASE WHEN LPL_Zamknieta IS NULL THEN 0 ELSE 1 END) AS IsClosed
        FROM CDN.ListyPlac
        WHERE LPL_Rok = @year AND LPL_Miesiac = @month
        ORDER BY LPL_LplId;

        SELECT
            w.WPL_LplId AS ListId,
            w.WPL_WplId AS OptimaId,
            w.WPL_PraId AS OptimaEmployeeId,
            k.PRA_ImportRowId AS ImportRowId,
            NULLIF(TRIM(k.PRA_Kod), N'') AS EmployeeCode,
            w.WPL_Brutto AS Gross,
            w.WPL_Wartosc AS Net,
            CONVERT(bit, CASE WHEN w.WPL_Zamknieta <> 0 THEN 1 ELSE 0 END) AS IsClosed
        FROM CDN.Wyplaty AS w
        JOIN CDN.ListyPlac AS l ON l.LPL_LplId = w.WPL_LplId
        LEFT JOIN CDN.PracKod AS k ON k.PRA_PraId = w.WPL_PraId
        WHERE l.LPL_Rok = @year AND l.LPL_Miesiac = @month
        ORDER BY w.WPL_LplId, w.WPL_WplId;

        SELECT
            e.WPE_WplId AS PayoutId,
            e.WPE_WpeId AS OptimaId,
            e.WPE_TwpId AS ComponentOptimaId,
            CONVERT(nvarchar(128), TRIM(e.WPE_Nazwa)) AS Name,
            e.WPE_Wartosc AS Amount
        FROM CDN.WypElementy AS e
        JOIN CDN.Wyplaty AS w ON w.WPL_WplId = e.WPE_WplId
        JOIN CDN.ListyPlac AS l ON l.LPL_LplId = w.WPL_LplId
        WHERE l.LPL_Rok = @year AND l.LPL_Miesiac = @month
        ORDER BY e.WPE_WplId, e.WPE_WpeId;
        """;

    public async Task<PayrollRead> ReadAsync(
        string databaseName,
        int year,
        int month,
        CancellationToken cancellationToken
    )
    {
        var name = databaseName.Trim();
        IReadOnlyList<OptimaCompany> listed =
            name == "" ? [] : await companies.ListActiveAsync(name, cancellationToken);
        if (listed is not [var company])
            return new PayrollRead(name, PayrollReadStatus.NotListed, null, null);

        if (!await companies.HasAccessAsync(company.DatabaseName, cancellationToken))
            return new PayrollRead(company.DatabaseName, PayrollReadStatus.NoAccess, null, null);

        try
        {
            await using var connection = await connections.OpenCompanyAsync(
                company.DatabaseName,
                cancellationToken
            );
            if (!await companies.IsSameCompanyAsync(connection, company, cancellationToken))
                return new PayrollRead(
                    company.DatabaseName,
                    PayrollReadStatus.Failed,
                    FailureCodes.TaxNumberMismatch,
                    null
                );

            var taxNumber = await companies.ReadTaxNumberAsync(connection, cancellationToken);
            await using var reader = await connection.QueryMultipleAsync(
                new CommandDefinition(
                    PayrollSql,
                    new { year, month },
                    commandTimeout: connections.CommandTimeout,
                    cancellationToken: cancellationToken
                )
            );
            var lists = (await reader.ReadAsync<ListRow>()).AsList();
            var payouts = (await reader.ReadAsync<PayoutRow>()).AsList();
            var elements = (await reader.ReadAsync<ElementRow>()).ToLookup(e => e.PayoutId);
            var payoutsByList = payouts.ToLookup(p => p.ListId);

            return new PayrollRead(
                company.DatabaseName,
                PayrollReadStatus.Read,
                null,
                new CompanyPayrolls(
                    company.DatabaseName,
                    taxNumber,
                    year,
                    month,
                    lists
                        .Select(list => new PayrollList(
                            list.OptimaId,
                            list.Number,
                            list.Name,
                            DateOnly.FromDateTime(list.PeriodFrom),
                            DateOnly.FromDateTime(list.PeriodTo),
                            DateOnly.FromDateTime(list.DocumentDate),
                            list.IsClosed,
                            payoutsByList[list.OptimaId]
                                .Select(payout => new Payout(
                                    payout.OptimaId,
                                    payout.OptimaEmployeeId,
                                    Guid.TryParse(payout.ImportRowId, out var id) ? id : null,
                                    payout.EmployeeCode,
                                    payout.Gross,
                                    payout.Net,
                                    payout.IsClosed,
                                    elements[payout.OptimaId]
                                        .Select(element => new PayoutElement(
                                            element.OptimaId,
                                            element.ComponentOptimaId,
                                            element.Name,
                                            element.Amount
                                        ))
                                        .ToList()
                                ))
                                .ToList()
                        ))
                        .ToList()
                )
            );
        }
        catch (SqlException exception)
        {
            // Only the number is kept: the message can carry Optima's data.
            return new PayrollRead(
                company.DatabaseName,
                PayrollReadStatus.Failed,
                exception.Number,
                null
            );
        }
    }

    [UsedImplicitly] // Read by Dapper.
    private sealed record ListRow(
        int OptimaId,
        string? Number,
        string? Name,
        DateTime PeriodFrom,
        DateTime PeriodTo,
        DateTime DocumentDate,
        bool IsClosed
    );

    [UsedImplicitly] // Read by Dapper.
    private sealed record PayoutRow(
        int ListId,
        int OptimaId,
        int OptimaEmployeeId,
        string? ImportRowId,
        string? EmployeeCode,
        decimal Gross,
        decimal Net,
        bool IsClosed
    );

    [UsedImplicitly] // Read by Dapper.
    private sealed record ElementRow(
        int PayoutId,
        int OptimaId,
        int? ComponentOptimaId,
        string Name,
        decimal Amount
    );
}
