using JetBrains.Annotations;

namespace Ambit.Optima.Integrator.Core.Operations;

/// <summary>
/// A deduction or an allowance as Optima defines it for one company. Its id means something only in that company.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for Ambit.
public sealed record PayrollComponent(
    int OptimaId,
    string Name,
    string? ShortName,
    bool IsDeduction,
    int SourceKind,
    int? StandardId,
    decimal? DefaultAmount,
    bool IsPkzp,
    bool IsSeizure,
    string? ImportRowId,
    string? ImportRowId2
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for Ambit.
public sealed record CompanyPayrollComponents(
    string DatabaseName,
    string? TaxNumber,
    IReadOnlyList<PayrollComponent> Components
);

public enum CompanyReadStatus
{
    Read,
    NoAccess,
    Failed,
}

public sealed record CompanyRead(
    string DatabaseName,
    CompanyReadStatus Status,
    string? TaxNumber,
    int? ErrorNumber,
    IReadOnlyList<PayrollComponent> Components
);

public interface IPayrollComponentReader
{
    Task<IReadOnlyList<CompanyRead>> ReadAsync(CancellationToken cancellationToken);
}
