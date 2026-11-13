using Ambit.Optima.Integrator.Core.Operations;
using JetBrains.Annotations;

namespace Ambit.Optima.Integrator.Core.Deductions;

/// <summary>
/// The deductions of an ExportDeductions operation, as the HR application gives them while the integrator holds it. One company,
/// because a component's id means something only there, and one month.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record DeductionsWork(
    string? DatabaseName,
    int Year,
    int Month,
    IReadOnlyList<DeductionItem> Deductions
);

/// <summary>One employee's deduction for the operation's month, already computed by Ambit.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record DeductionItem(
    Guid? DeductionId,
    Guid? EmployeeId,
    string? EmployeeCode,
    int? OptimaId,
    decimal? Amount,
    string? Currency = null
)
{
    /// <summary>
    /// The import key of the rows this deduction writes in Optima for one month. It depends only on the deduction and
    /// the month, so the same month exported again, by this operation or another, finds what was written before.
    /// </summary>
    public static Guid OptimaKey(Guid deductionId, int year, int month)
    {
        Span<byte> input = stackalloc byte[20];
        deductionId.TryWriteBytes(input[..16]);
        BitConverter.TryWriteBytes(input[16..], year * 100 + month);
        return DerivedGuid.From(input);
    }
}

/// <summary>What happened to one deduction. Holds identifiers and codes only.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public sealed record DeductionAddResult(
    string DatabaseName,
    string Status,
    int Attempt,
    int? OptimaEmployeeId,
    int? OptimaAdditionId,
    string? WarningCodes,
    int? ErrorNumber,
    string? ErrorProcedure
);

/// <summary>
/// Whether a month's item is a deduction or an allowance. Optima writes both the same way; only the payroll component
/// differs, and it must be of the item's kind.
/// </summary>
public enum PayItemKind
{
    Deduction,
    Allowance,
}

/// <summary>
/// The allowances of an ExportAllowances operation, as the HR application gives them while the integrator holds it.
/// One company and one month, like <see cref="DeductionsWork"/>.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record AllowancesWork(
    string? DatabaseName,
    int Year,
    int Month,
    IReadOnlyList<AllowanceItem> Allowances
);

/// <summary>One employee's allowance for the operation's month, already computed by the HR application.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record AllowanceItem(
    Guid? AllowanceId,
    Guid? EmployeeId,
    string? EmployeeCode,
    int? OptimaId,
    decimal? Amount,
    string? Currency = null
)
{
    /// <summary>The same item in the shape the writer takes; its import key is derived the same way.</summary>
    public DeductionItem AsItem() =>
        new(AllowanceId, EmployeeId, EmployeeCode, OptimaId, Amount, Currency);
}

/// <summary>One deduction or allowance to write to one company.</summary>
public sealed record DeductionAddRequest(
    Guid OperationId,
    string DatabaseName,
    int Year,
    int Month,
    DeductionItem Deduction,
    PayItemKind Kind = PayItemKind.Deduction
);

/// <summary>The deduction was refused as a whole, before the company was touched; the message is safe to show.</summary>
public sealed class DeductionRejectedException(int number, string message) : Exception(message)
{
    public int Number { get; } = number;
}

public interface IDeductionWriter
{
    Task<DeductionAddResult> AddAsync(
        DeductionAddRequest request,
        CancellationToken cancellationToken
    );
}
