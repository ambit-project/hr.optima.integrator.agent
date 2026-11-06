using JetBrains.Annotations;

namespace Ambit.Agent.Core.Diagnostics;

/// <summary>Who the Worker is on SQL Server, and whether it holds more rights than it should.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // Serialized for the UI.
public sealed record SqlIdentity(
    string LoginName,
    string OriginalLogin,
    string DatabaseName,
    string ServerName,
    bool IsSysAdmin,
    bool IsDbOwner
)
{
    /// <summary>The account is the security boundary, so an overprivileged one is worth saying out loud.</summary>
    public bool IsOverprivileged => IsSysAdmin || IsDbOwner;
}

public interface ISqlDiagnostics
{
    Task<SqlIdentity> GetIdentityAsync(CancellationToken cancellationToken);
}
