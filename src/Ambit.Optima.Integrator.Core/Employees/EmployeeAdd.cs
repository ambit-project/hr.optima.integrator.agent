using JetBrains.Annotations;

namespace Ambit.Optima.Integrator.Core.Employees;

/// <summary>
/// One employee to add to Optima. Everything is nullable because it arrives over IPC or HTTP:
/// <see cref="EmployeeValidation"/> decides whether a request is complete.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Read from JSON.
public sealed record EmployeeAddRequest(
    Guid? OperationId,
    Guid? EmployeeId,
    string? EmployeeCode,
    string? LastName,
    string? FirstName,
    string? DatabaseName = null,
    int? CenterId = null,
    int? OperatorId = null,
    DateOnly? DateOfBirth = null,
    string? Pesel = null,
    string? CitizenshipCode = null,
    string? Email = null,
    string? Phone = null,
    string? Street = null,
    string? BuildingNumber = null,
    string? UnitNumber = null,
    string? PostalCode = null,
    string? City = null,
    string? AddressCountryCode = null,
    string? Note = null,
    string? DocumentKind = null,
    string? DocumentNumber = null,
    DateOnly? DocumentValidUntil = null,
    string? PositionName = null,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null
)
{
    /// <summary>Holds personal data, so printing it must never put that in a log.</summary>
    public override string ToString() => nameof(EmployeeAddRequest);
}

/// <summary>What happened on one company database. Holds identifiers and codes only.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for the IPC client.
public sealed record EmployeeAddResult(
    string DatabaseName,
    string Status,
    int Attempt,
    int? OptimaEmployeeId,
    int? OptimaContractId,
    string? WarningCodes,
    int? ErrorNumber,
    string? ErrorProcedure,
    string? OptimaErrorCodes
);

/// <summary>The request was refused as a whole, before any company was touched; the message is safe to show.</summary>
public sealed class EmployeeRejectedException(int number, string message) : Exception(message)
{
    public int Number { get; } = number;
}

public interface IEmployeeWriter
{
    Task<IReadOnlyList<EmployeeAddResult>> AddAsync(
        EmployeeAddRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>Writes are off by default and switched on locally, never from the HR application.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)] // Bound from configuration.
public sealed class WriteOptions
{
    public const string SectionName = "Writes";

    public bool Enabled { get; init; }
}
