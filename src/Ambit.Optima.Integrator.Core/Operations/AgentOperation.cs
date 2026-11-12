using System.Security.Cryptography;
using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using JetBrains.Annotations;

namespace Ambit.Optima.Integrator.Core.Operations;

/// <summary>What Ambit asks the agent to do. The id stays the same when an operator tries again.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record AgentOperation(Guid Id, string Kind, int Attempt);

public static class OperationKinds
{
    public const string ReadPayrollComponents = "ReadPayrollComponents";

    /// <summary>ADR 0006: adds Ambit's people to Optima. Offered only when writes are switched on locally.</summary>
    public const string AddEmployees = "AddEmployees";

    /// <summary>ADR 0007: gives employees one month's deductions. Offered only when writes are switched on locally.</summary>
    public const string ExportDeductions = "ExportDeductions";

    private static readonly IReadOnlyList<string> Supported = [ReadPayrollComponents];

    /// <summary>What this agent tells Ambit it can run: a write only where an administrator allowed writes.</summary>
    public static IReadOnlyList<string> SupportedWith(bool writesEnabled) =>
        writesEnabled ? [ReadPayrollComponents, AddEmployees, ExportDeductions] : Supported;
}

/// <summary>
/// The steps Ambit shows in its log. Codes and identifiers only: Ambit writes the sentence, so no SQL Server
/// message, which can carry personal data, leaves the client's machine.
/// </summary>
public static class OperationLogCodes
{
    public const string DatabaseRead = "database_read";
    public const string DatabaseNoAccess = "database_no_access";
    public const string DatabaseFailed = "database_failed";
    public const string NoCompanyReadable = "no_company_readable";
    public const string ExecutionFailed = "execution_failed";
    public const string UnsupportedKind = "unsupported_kind";

    public const string EmployeeAdded = "employee_added";
    public const string EmployeeExists = "employee_exists";
    public const string EmployeeRejected = "employee_rejected";
    public const string EmployeeFailed = "employee_failed";
    public const string EmployeesFailed = "employees_failed";
    public const string EmployeesUnavailable = "employees_unavailable";
    public const string NoCompanyWritable = "no_company_writable";
    public const string WritesDisabled = "writes_disabled";

    public const string DeductionAdded = "deduction_added";
    public const string DeductionExists = "deduction_exists";
    public const string DeductionRejected = "deduction_rejected";
    public const string DeductionFailed = "deduction_failed";
    public const string EmployeeNotFound = "employee_not_found";
    public const string ComponentNotFound = "component_not_found";
    public const string DeductionsFailed = "deductions_failed";
    public const string DeductionsUnavailable = "deductions_unavailable";
}

/// <summary>
/// One step of an operation's log. <see cref="WarningCodes"/> carries the agent's own codes, such as
/// <c>MATCHED_BY_ACRONYM_ONLY</c>.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for Ambit.
public sealed record OperationLogEntry(
    DateTimeOffset At,
    string Code,
    string? DatabaseName = null,
    int? ErrorNumber = null,
    int? Count = null,
    Guid? EmployeeId = null,
    Guid? DeductionId = null,
    string? WarningCodes = null
);

/// <summary>The outcome of one attempt. Only an operator in Ambit can start another.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for Ambit.
public sealed record OperationResult(
    int Attempt,
    bool Succeeded,
    string? ErrorCode,
    IReadOnlyList<OperationLogEntry> Log,
    IReadOnlyList<CompanyPayrollComponents>? PayrollComponents = null
);

/// <summary>
/// ADR 0006: the people of an AddEmployees operation, as Ambit gives them while the agent holds it. Each one reads into
/// <see cref="EmployeeAddRequest"/>, whose names Ambit's JSON uses.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record EmployeesWork(
    string? DatabaseName,
    IReadOnlyList<EmployeeAddRequest> Employees
)
{
    /// <summary>Holds personal data, so printing it must never put that in a log.</summary>
    public override string ToString() => nameof(EmployeesWork);

    /// <summary>
    /// The key of one person's call in <c>app.employee_add</c>, which keeps one row per call and company: the same for
    /// every attempt of the operation, so a retry sees what an earlier attempt wrote, and different per person.
    /// </summary>
    public static Guid CallId(Guid operationId, Guid employeeId)
    {
        Span<byte> input = stackalloc byte[32];
        operationId.TryWriteBytes(input[..16]);
        employeeId.TryWriteBytes(input[16..]);
        return DerivedGuid.From(input);
    }
}

/// <summary>A version 8 UUID (RFC 9562) from a SHA-256 of the input: the same input always gives the same id.</summary>
public static class DerivedGuid
{
    public static Guid From(ReadOnlySpan<byte> input)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        var bytes = hash[..16].ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80); // version 8: custom, RFC 9562
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}

public interface IAmbitApiClient
{
    /// <summary>The next operation, or null when none waits.</summary>
    Task<AgentOperation?> ClaimAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The people of an AddEmployees operation this agent holds, or null when Ambit no longer hands them out (a 404 is
    /// final). An exception means Ambit could not be reached.
    /// </summary>
    Task<EmployeesWork?> GetEmployeesAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// The deductions of an ExportDeductions operation this agent holds, or null when Ambit no longer hands them out (a
    /// 404 is final). An exception means Ambit could not be reached.
    /// </summary>
    Task<DeductionsWork?> GetDeductionsAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>False when Ambit refused the report for good. An exception means it may be sent again.</summary>
    Task<bool> ReportAsync(
        Guid operationId,
        OperationResult result,
        CancellationToken cancellationToken
    );
}
