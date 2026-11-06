using JetBrains.Annotations;

namespace Ambit.Agent.Core.Operations;

/// <summary>What Ambit asks the agent to do. The id stays the same when an operator tries again.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record AgentOperation(Guid Id, string Kind, int Attempt);

public static class OperationKinds
{
    public const string ReadPayrollComponents = "ReadPayrollComponents";

    public static readonly IReadOnlyList<string> Supported = [ReadPayrollComponents];
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
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for Ambit.
public sealed record OperationLogEntry(
    DateTimeOffset At,
    string Code,
    string? DatabaseName = null,
    int? ErrorNumber = null,
    int? Count = null
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

public interface IAmbitApiClient
{
    /// <summary>The next operation, or null when none waits.</summary>
    Task<AgentOperation?> ClaimAsync(CancellationToken cancellationToken);

    /// <summary>False when Ambit refused the report for good. An exception means it may be sent again.</summary>
    Task<bool> ReportAsync(
        Guid operationId,
        OperationResult result,
        CancellationToken cancellationToken
    );
}
