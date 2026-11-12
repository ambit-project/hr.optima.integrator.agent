namespace Ambit.Optima.Integrator.Core.Deductions;

/// <summary>A deduction checked before the company is touched, with every required value present.</summary>
public sealed record ValidDeduction(
    Guid OperationId,
    Guid DeductionId,
    Guid OptimaKey,
    string DatabaseName,
    Guid EmployeeId,
    string EmployeeCode,
    int OptimaComponentId,
    int Year,
    int Month,
    decimal Amount
)
{
    public DateOnly ValidFrom => new(Year, Month, 1);

    public DateOnly ValidTo => ValidFrom.AddMonths(1).AddDays(-1);
}

public static class DeductionValidation
{
    public const string Currency = "PLN";

    /// <summary>The largest value Optima's amount column (decimal(15, 2)) holds.</summary>
    public const decimal MaxAmount = 9_999_999_999_999.99m;

    public static ValidDeduction Validate(DeductionAddRequest request)
    {
        var item = request.Deduction;
        var databaseName = request.DatabaseName?.Trim() ?? "";
        var employeeCode = item.EmployeeCode?.Trim() ?? "";
        var currency = string.IsNullOrWhiteSpace(item.Currency)
            ? Currency
            : item.Currency.Trim().ToUpperInvariant();

        if (item is not { DeductionId: { } deductionId, EmployeeId: { } employeeId })
            throw Rejected(
                RejectionCodes.DeductionIdsRequired,
                "deductionId and employeeId are required."
            );
        if (databaseName == "" || item.OptimaId is not { } componentId)
            throw Rejected(
                RejectionCodes.DeductionTargetRequired,
                "databaseName and optimaId are required."
            );
        if (employeeCode == "" || employeeCode.Length > 20)
            throw Rejected(
                RejectionCodes.DeductionEmployeeCodeInvalid,
                "employeeCode is required and has at most 20 characters."
            );
        if (request.Year is < 2000 or > 2998 || request.Month is < 1 or > 12)
            throw Rejected(RejectionCodes.DeductionMonthInvalid, "The month is not valid.");
        // The component makes it a deduction, so the amount itself is positive.
        if (
            item.Amount is not { } amount
            || amount <= 0
            || amount > MaxAmount
            || decimal.Round(amount, 2) != amount
        )
            throw Rejected(
                RejectionCodes.DeductionAmountInvalid,
                "amount must be positive, with at most two decimals."
            );
        if (currency != Currency)
            throw Rejected(
                RejectionCodes.DeductionCurrencyNotSupported,
                "Only PLN deductions can be exported."
            );

        return new ValidDeduction(
            request.OperationId,
            deductionId,
            DeductionItem.OptimaKey(deductionId, request.Year, request.Month),
            databaseName,
            employeeId,
            employeeCode,
            componentId,
            request.Year,
            request.Month,
            amount
        );
    }

    private static DeductionRejectedException Rejected(int number, string message) =>
        new(number, message);
}
