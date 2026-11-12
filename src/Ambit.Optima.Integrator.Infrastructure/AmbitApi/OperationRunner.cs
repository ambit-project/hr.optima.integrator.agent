using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.Infrastructure.AmbitApi;

/// <summary>Runs one attempt and always ends with a result. It never repeats anything by itself.</summary>
public sealed class OperationRunner(
    IPayrollComponentReader components,
    IEmployeeWriter employees,
    IDeductionWriter deductions,
    IAmbitApiClient api,
    IOptions<WriteOptions> writes,
    TimeProvider clock
)
{
    private const string Added = "Succeeded";
    private const string AlreadyExists = "AlreadyExists";
    private const string NoAccess = "NoAccess";
    private const string EmployeeNotFound = "EmployeeNotFound";
    private const string ComponentNotFound = "ComponentNotFound";

    public async Task<OperationResult> RunAsync(
        AgentOperation operation,
        CancellationToken cancellationToken
    ) =>
        operation.Kind switch
        {
            OperationKinds.ReadPayrollComponents => await ReadComponentsAsync(
                operation,
                cancellationToken
            ),
            OperationKinds.AddEmployees => await AddEmployeesAsync(operation, cancellationToken),
            OperationKinds.ExportDeductions => await ExportDeductionsAsync(
                operation,
                cancellationToken
            ),
            _ => Failure(operation, OperationLogCodes.UnsupportedKind),
        };

    /// <summary>
    /// ADR 0007. Each deduction is its own call of <c>app.deduction_add</c>, with its own transaction, so one refused
    /// or failed does not stop the others. Its rows in Optima carry a key made from the deduction and the month, so a
    /// month exported again is written once. The operation succeeds only when every deduction is there afterwards.
    /// </summary>
    private async Task<OperationResult> ExportDeductionsAsync(
        AgentOperation operation,
        CancellationToken cancellationToken
    )
    {
        // Ambit never gives this kind to an agent that did not offer it; this guards a configuration changed in between.
        if (!writes.Value.Enabled)
            return Failure(operation, OperationLogCodes.WritesDisabled);

        DeductionsWork? work;
        try
        {
            work = await api.GetDeductionsAsync(operation.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(operation, OperationLogCodes.ExecutionFailed);
        }

        if (work is null)
            return Failure(operation, OperationLogCodes.DeductionsUnavailable);

        var log = new List<OperationLogEntry>();
        foreach (var deduction in work.Deductions)
            log.Add(await AddDeductionAsync(operation, work, deduction, cancellationToken));

        var done = log.All(e =>
            e.Code is OperationLogCodes.DeductionAdded or OperationLogCodes.DeductionExists
        );
        var errorCode = done ? null : OperationLogCodes.DeductionsFailed;
        return new OperationResult(operation.Attempt, done, errorCode, log);
    }

    private async Task<OperationLogEntry> AddDeductionAsync(
        AgentOperation operation,
        DeductionsWork work,
        DeductionItem deduction,
        CancellationToken cancellationToken
    )
    {
        var databaseName = work.DatabaseName ?? "";
        try
        {
            var result = await deductions.AddAsync(
                new DeductionAddRequest(
                    operation.Id,
                    databaseName,
                    work.Year,
                    work.Month,
                    deduction
                ),
                cancellationToken
            );
            return DeductionEntry(
                result.Status switch
                {
                    Added => OperationLogCodes.DeductionAdded,
                    AlreadyExists => OperationLogCodes.DeductionExists,
                    NoAccess => OperationLogCodes.DatabaseNoAccess,
                    EmployeeNotFound => OperationLogCodes.EmployeeNotFound,
                    ComponentNotFound => OperationLogCodes.ComponentNotFound,
                    _ => OperationLogCodes.DeductionFailed,
                },
                result.ErrorNumber,
                result.WarningCodes
            );
        }
        catch (DeductionRejectedException rejected)
        {
            return DeductionEntry(OperationLogCodes.DeductionRejected, rejected.Number);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DeductionEntry(OperationLogCodes.DeductionFailed);
        }

        OperationLogEntry DeductionEntry(
            string code,
            int? errorNumber = null,
            string? warnings = null
        ) =>
            new(
                clock.GetUtcNow(),
                code,
                databaseName,
                errorNumber,
                EmployeeId: deduction.EmployeeId,
                DeductionId: deduction.DeductionId,
                WarningCodes: warnings
            );
    }

    /// <summary>
    /// ADR 0006. Each person is their own call of <c>app.employee_add</c>, with a transaction per company, so one person
    /// refused or one company failing does not stop the others. A person Optima already has is left alone, which is
    /// what makes trying the operation again safe. The log holds codes and ids, never a message, which can carry a
    /// PESEL.
    /// </summary>
    private async Task<OperationResult> AddEmployeesAsync(
        AgentOperation operation,
        CancellationToken cancellationToken
    )
    {
        // Ambit never gives this kind to an agent that did not offer it; this guards a configuration changed in between.
        if (!writes.Value.Enabled)
            return Failure(operation, OperationLogCodes.WritesDisabled);

        EmployeesWork? work;
        try
        {
            work = await api.GetEmployeesAsync(operation.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(operation, OperationLogCodes.ExecutionFailed);
        }

        if (work is null)
            return Failure(operation, OperationLogCodes.EmployeesUnavailable);

        var log = new List<OperationLogEntry>();
        foreach (var employee in work.Employees)
            log.AddRange(
                await AddOneAsync(operation, work.DatabaseName, employee, cancellationToken)
            );

        var written = log.Count(e =>
            e.Code is OperationLogCodes.EmployeeAdded or OperationLogCodes.EmployeeExists
        );
        var errorCode =
            log.Any(e =>
                e.Code is OperationLogCodes.EmployeeRejected or OperationLogCodes.EmployeeFailed
            )
                ? OperationLogCodes.EmployeesFailed
            : work.Employees.Count > 0 && written == 0 ? OperationLogCodes.NoCompanyWritable
            : null;

        return new OperationResult(operation.Attempt, errorCode is null, errorCode, log);
    }

    private async Task<IReadOnlyList<OperationLogEntry>> AddOneAsync(
        AgentOperation operation,
        string? databaseName,
        EmployeeAddRequest employee,
        CancellationToken cancellationToken
    )
    {
        var employeeId = employee.EmployeeId;
        var request = employee with
        {
            OperationId = EmployeesWork.CallId(operation.Id, employeeId ?? Guid.Empty),
            DatabaseName = databaseName,
        };

        try
        {
            var results = await employees.AddAsync(request, cancellationToken);
            return results
                .Select(result => new OperationLogEntry(
                    clock.GetUtcNow(),
                    result.Status switch
                    {
                        Added => OperationLogCodes.EmployeeAdded,
                        AlreadyExists => OperationLogCodes.EmployeeExists,
                        NoAccess => OperationLogCodes.DatabaseNoAccess,
                        _ => OperationLogCodes.EmployeeFailed,
                    },
                    result.DatabaseName,
                    result.ErrorNumber,
                    EmployeeId: employeeId
                ))
                .ToList();
        }
        catch (EmployeeRejectedException rejected)
        {
            return
            [
                new OperationLogEntry(
                    clock.GetUtcNow(),
                    OperationLogCodes.EmployeeRejected,
                    databaseName,
                    rejected.Number,
                    EmployeeId: employeeId
                ),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return
            [
                new OperationLogEntry(
                    clock.GetUtcNow(),
                    OperationLogCodes.EmployeeFailed,
                    databaseName,
                    EmployeeId: employeeId
                ),
            ];
        }
    }

    private async Task<OperationResult> ReadComponentsAsync(
        AgentOperation operation,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<CompanyRead> companies;
        try
        {
            companies = await components.ReadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(operation, OperationLogCodes.ExecutionFailed);
        }

        var log = companies.Select(Entry).ToList();
        var read = companies.Where(company => company.Status == CompanyReadStatus.Read).ToList();

        // NoAccess is not a failure: an administrator may exclude a company on purpose.
        var errorCode =
            companies.Any(company => company.Status == CompanyReadStatus.Failed)
                ? OperationLogCodes.DatabaseFailed
            : read.Count == 0 ? OperationLogCodes.NoCompanyReadable
            : null;

        // Companies that were read are kept even when another failed: a read can do no harm.
        return new OperationResult(
            operation.Attempt,
            errorCode is null,
            errorCode,
            log,
            read.Select(company => new CompanyPayrollComponents(
                    company.DatabaseName,
                    company.TaxNumber,
                    company.Components
                ))
                .ToList()
        );
    }

    private OperationLogEntry Entry(CompanyRead company) =>
        company.Status switch
        {
            CompanyReadStatus.Read => new OperationLogEntry(
                clock.GetUtcNow(),
                OperationLogCodes.DatabaseRead,
                company.DatabaseName,
                Count: company.Components.Count
            ),
            CompanyReadStatus.NoAccess => new OperationLogEntry(
                clock.GetUtcNow(),
                OperationLogCodes.DatabaseNoAccess,
                company.DatabaseName
            ),
            _ => new OperationLogEntry(
                clock.GetUtcNow(),
                OperationLogCodes.DatabaseFailed,
                company.DatabaseName,
                company.ErrorNumber
            ),
        };

    private OperationResult Failure(AgentOperation operation, string code) =>
        new(operation.Attempt, false, code, [new OperationLogEntry(clock.GetUtcNow(), code)]);
}
