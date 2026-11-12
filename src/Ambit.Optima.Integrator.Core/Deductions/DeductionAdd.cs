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

/// <summary>One deduction to write to one company.</summary>
public sealed record DeductionAddRequest(
    Guid OperationId,
    string DatabaseName,
    int Year,
    int Month,
    DeductionItem Deduction
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
