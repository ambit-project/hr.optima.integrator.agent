using JetBrains.Annotations;

namespace Ambit.Optima.Integrator.Core.Payrolls;

/// <summary>The company and month of a ReadPayrolls operation, as the HR application gives them.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record PayrollPeriod(string? DatabaseName, int Year, int Month);

/// <summary>
/// One company's payroll lists of one month (Optima's <c>LPL_Rok</c> / <c>LPL_Miesiac</c>). Ids mean something only in
/// that company.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for the HR application.
public sealed record CompanyPayrolls(
    string DatabaseName,
    string? TaxNumber,
    int Year,
    int Month,
    IReadOnlyList<PayrollList> Lists
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for the HR application.
public sealed record PayrollList(
    int OptimaId,
    string? Number,
    string? Name,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly DocumentDate,
    bool IsClosed,
    IReadOnlyList<Payout> Payouts
);

/// <summary>
/// One employee's payout on a list. <see cref="EmployeeId"/> is the HR application's id when Optima holds it as the
/// employee's import id.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for the HR application.
public sealed record Payout(
    int OptimaId,
    int OptimaEmployeeId,
    Guid? EmployeeId,
    string? EmployeeCode,
    decimal Gross,
    decimal Net,
    bool IsClosed,
    IReadOnlyList<PayoutElement> Elements
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for the HR application.
public sealed record PayoutElement(
    int OptimaId,
    int? ComponentOptimaId,
    string Name,
    decimal Amount
);

public enum PayrollReadStatus
{
    Read,
    NotListed,
    NoAccess,
    Failed,
}

/// <summary>The outcome of reading one company. <see cref="Payrolls"/> is set only when the read succeeded.</summary>
public sealed record PayrollRead(
    string DatabaseName,
    PayrollReadStatus Status,
    int? ErrorNumber,
    CompanyPayrolls? Payrolls
);

public interface IPayrollReader
{
    Task<PayrollRead> ReadAsync(
        string databaseName,
        int year,
        int month,
        CancellationToken cancellationToken
    );
}
