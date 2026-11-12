namespace Ambit.Optima.Integrator.IntegrationTests;

/// <summary>Runs only where a real Worker service is installed: the Windows job in CI, or a test machine.</summary>
public sealed class WindowsServiceFactAttribute : FactAttribute
{
    public const string ExpectedLoginVariable = "AMBIT_SPIKE_EXPECTED_LOGIN";

    public WindowsServiceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(ExpectedLoginVariable) is null)
            Skip =
                $"Set {ExpectedLoginVariable} on a machine with the Worker installed as a service.";
    }
}
