using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Payrolls;

namespace Ambit.Optima.Integrator.UnitTests;

/// <summary>For runners whose tests never reach a deduction.</summary>
internal sealed class NoDeductions : IDeductionWriter
{
    public Task<DeductionAddResult> AddAsync(
        DeductionAddRequest request,
        CancellationToken cancellationToken
    ) => throw new InvalidOperationException();
}

/// <summary>For runners whose tests never read a payroll list.</summary>
internal sealed class NoPayrolls : IPayrollReader
{
    public Task<PayrollRead> ReadAsync(
        string databaseName,
        int year,
        int month,
        CancellationToken cancellationToken
    ) => throw new InvalidOperationException();
}
