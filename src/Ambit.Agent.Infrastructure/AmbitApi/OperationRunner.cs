using Ambit.Agent.Core.Operations;

namespace Ambit.Agent.Infrastructure.AmbitApi;

/// <summary>Runs one attempt and always ends with a result. It never repeats anything by itself.</summary>
public sealed class OperationRunner(IPayrollComponentReader components, TimeProvider clock)
{
    public async Task<OperationResult> RunAsync(
        AgentOperation operation,
        CancellationToken cancellationToken
    ) =>
        operation.Kind == OperationKinds.ReadPayrollComponents
            ? await ReadComponentsAsync(operation, cancellationToken)
            : Failure(operation, OperationLogCodes.UnsupportedKind);

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
