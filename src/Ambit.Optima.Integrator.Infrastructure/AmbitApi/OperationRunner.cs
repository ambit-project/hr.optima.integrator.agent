using Ambit.Optima.Integrator.Core;
using Ambit.Optima.Integrator.Core.Deductions;
using Ambit.Optima.Integrator.Core.Employees;
using Ambit.Optima.Integrator.Core.Operations;
using Ambit.Optima.Integrator.Core.Payrolls;
using Microsoft.Extensions.Options;

namespace Ambit.Optima.Integrator.Infrastructure.AmbitApi;

/// <summary>Runs one attempt and always ends with a result. It never repeats anything by itself.</summary>
public sealed class OperationRunner(
    IPayrollComponentReader components,
    IEmployeeWriter employees,
    IDeductionWriter deductions,
    IPayrollReader payrolls,
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
            OperationKinds.ExportDeductions => await ExportAsync(
                operation,
                PayItemKind.Deduction,
                cancellationToken
            ),
            OperationKinds.ExportAllowances => await ExportAsync(
                operation,
                PayItemKind.Allowance,
                cancellationToken
            ),
            OperationKinds.ReadPayrolls => await ReadPayrollsAsync(operation, cancellationToken),
            _ => Failure(operation, OperationLogCodes.UnsupportedKind),
        };

    /// <summary>
    /// Each deduction or allowance is its own call of <see cref="IDeductionWriter"/>, with its own transaction, so one
    /// refused or failed does not stop the others. Its rows in Optima carry a key made from the item and the month, so a
    /// month exported again is written once. The operation succeeds only when every item is there afterwards.
    /// </summary>
    private async Task<OperationResult> ExportAsync(
        AgentOperation operation,
        PayItemKind kind,
        CancellationToken cancellationToken
    )
    {
        // Ambit never gives this kind to an agent that did not offer it; this guards a configuration changed in between.
        if (!writes.Value.Enabled)
            return Failure(operation, OperationLogCodes.WritesDisabled);

        var codes = ExportCodes.For(kind);
        DeductionsWork? work;
        try
        {
            work = await GetExportWorkAsync(operation.Id, kind, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(operation, OperationLogCodes.ExecutionFailed);
        }

        if (work is null)
            return Failure(operation, codes.Unavailable);

        var log = new List<OperationLogEntry>();
        foreach (var item in work.Deductions)
            log.Add(await AddItemAsync(operation, work, item, kind, codes, cancellationToken));

        var done = log.All(e => e.Code == codes.ItemAdded || e.Code == codes.ItemExists);
        return new OperationResult(operation.Attempt, done, done ? null : codes.Failed, log);
    }

    /// <summary>Allowances arrive under their own names and are written the same way as deductions.</summary>
    private async Task<DeductionsWork?> GetExportWorkAsync(
        Guid operationId,
        PayItemKind kind,
        CancellationToken cancellationToken
    )
    {
        if (kind == PayItemKind.Deduction)
            return await api.GetDeductionsAsync(operationId, cancellationToken);

        var allowances = await api.GetAllowancesAsync(operationId, cancellationToken);
        return allowances is null
            ? null
            : new DeductionsWork(
                allowances.DatabaseName,
                allowances.Year,
                allowances.Month,
                allowances.Allowances.Select(item => item.AsItem()).ToList()
            );
    }

    private async Task<OperationLogEntry> AddItemAsync(
        AgentOperation operation,
        DeductionsWork work,
        DeductionItem item,
        PayItemKind kind,
        ExportCodes codes,
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
                    item,
                    kind
                ),
                cancellationToken
            );
            return ItemEntry(
                result.Status switch
                {
                    Added => codes.ItemAdded,
                    AlreadyExists => codes.ItemExists,
                    NoAccess => OperationLogCodes.DatabaseNoAccess,
                    EmployeeNotFound => OperationLogCodes.EmployeeNotFound,
                    ComponentNotFound => OperationLogCodes.ComponentNotFound,
                    _ => codes.ItemFailed,
                },
                result.ErrorNumber,
                result.WarningCodes
            );
        }
        catch (DeductionRejectedException rejected)
        {
            return ItemEntry(codes.ItemRejected, rejected.Number);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ItemEntry(codes.ItemFailed);
        }

        OperationLogEntry ItemEntry(
            string code,
            int? errorNumber = null,
            string? warnings = null
        ) =>
            new(
                clock.GetUtcNow(),
                code,
                databaseName,
                errorNumber,
                EmployeeId: item.EmployeeId,
                DeductionId: kind == PayItemKind.Deduction ? item.DeductionId : null,
                WarningCodes: warnings,
                AllowanceId: kind == PayItemKind.Allowance ? item.DeductionId : null
            );
    }

    /// <summary>The log and error codes of one kind of export.</summary>
    private sealed record ExportCodes(
        string ItemAdded,
        string ItemExists,
        string ItemRejected,
        string ItemFailed,
        string Failed,
        string Unavailable
    )
    {
        private static readonly ExportCodes Deductions = new(
            OperationLogCodes.DeductionAdded,
            OperationLogCodes.DeductionExists,
            OperationLogCodes.DeductionRejected,
            OperationLogCodes.DeductionFailed,
            OperationLogCodes.DeductionsFailed,
            OperationLogCodes.DeductionsUnavailable
        );

        private static readonly ExportCodes Allowances = new(
            OperationLogCodes.AllowanceAdded,
            OperationLogCodes.AllowanceExists,
            OperationLogCodes.AllowanceRejected,
            OperationLogCodes.AllowanceFailed,
            OperationLogCodes.AllowancesFailed,
            OperationLogCodes.AllowancesUnavailable
        );

        public static ExportCodes For(PayItemKind kind) =>
            kind == PayItemKind.Allowance ? Allowances : Deductions;
    }

    /// <summary>
    /// One company and one month. The read writes nothing, so it needs no local permission. A company Optima does not
    /// list, or one the integrator may not read, fails the operation; the log says which.
    /// </summary>
    private async Task<OperationResult> ReadPayrollsAsync(
        AgentOperation operation,
        CancellationToken cancellationToken
    )
    {
        PayrollRead read;
        try
        {
            var period = await api.GetPayrollPeriodAsync(operation.Id, cancellationToken);
            if (period is null)
                return Failure(operation, OperationLogCodes.PayrollsUnavailable);

            read = await payrolls.ReadAsync(
                period.DatabaseName ?? "",
                period.Year,
                period.Month,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(operation, OperationLogCodes.ExecutionFailed);
        }

        var entry = read.Status switch
        {
            PayrollReadStatus.Read => new OperationLogEntry(
                clock.GetUtcNow(),
                OperationLogCodes.DatabaseRead,
                read.DatabaseName,
                Count: read.Payrolls!.Lists.Sum(list => list.Payouts.Count)
            ),
            PayrollReadStatus.NoAccess => new OperationLogEntry(
                clock.GetUtcNow(),
                OperationLogCodes.DatabaseNoAccess,
                read.DatabaseName
            ),
            PayrollReadStatus.NotListed => new OperationLogEntry(
                clock.GetUtcNow(),
                OperationLogCodes.DatabaseFailed,
                read.DatabaseName,
                RejectionCodes.NoCompanyMatches
            ),
            _ => new OperationLogEntry(
                clock.GetUtcNow(),
                OperationLogCodes.DatabaseFailed,
                read.DatabaseName,
                read.ErrorNumber
            ),
        };

        return read.Status == PayrollReadStatus.Read
            ? new OperationResult(operation.Attempt, true, null, [entry], Payrolls: read.Payrolls)
            : new OperationResult(
                operation.Attempt,
                false,
                OperationLogCodes.NoCompanyReadable,
                [entry]
            );
    }

    /// <summary>
    /// Each person is their own call of <see cref="IEmployeeWriter"/>, with a transaction per company, so one person
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
