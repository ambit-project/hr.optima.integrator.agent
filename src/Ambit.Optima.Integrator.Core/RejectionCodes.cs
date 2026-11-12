namespace Ambit.Optima.Integrator.Core;

/// <summary>
/// Stable numbers for a request refused as a whole, before any company is touched. They travel to the HR application
/// as <c>errorNumber</c> together with a <c>*_rejected</c> log code, and to the local UI over IPC.
/// </summary>
public static class RejectionCodes
{
    public const int First = 51000;
    public const int Last = 51999;

    public const int EmployeeIdsRequired = 51010;
    public const int EmployeeNamesRequired = 51011;
    public const int PeselInvalid = 51012;
    public const int DocumentKindUnknown = 51013;
    public const int TooLongForOptima = 51014;
    public const int CountryCodeInvalid = 51015;

    public const int DeductionIdsRequired = 51020;
    public const int DeductionTargetRequired = 51021;
    public const int DeductionEmployeeCodeInvalid = 51022;
    public const int DeductionMonthInvalid = 51023;
    public const int DeductionAmountInvalid = 51024;
    public const int DeductionCurrencyNotSupported = 51025;

    public const int NoCompanyMatches = 51401;

    public static bool IsRejection(int number) => number is >= First and <= Last;
}

/// <summary>Numbers the integrator records for a company that failed for a reason of its own, not SQL Server's.</summary>
public static class FailureCodes
{
    /// <summary>The company database holds a different tax number than Optima's list of companies.</summary>
    public const int TaxNumberMismatch = 51501;
}
