using Ambit.Agent.Core.Deductions;

namespace Ambit.Agent.UnitTests;

/// <summary>For runners whose tests never reach a deduction.</summary>
internal sealed class NoDeductions : IDeductionWriter
{
    public Task<DeductionAddResult> AddAsync(
        DeductionAddRequest request,
        CancellationToken cancellationToken
    ) => throw new InvalidOperationException();
}
