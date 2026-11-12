namespace Ambit.Optima.Integrator.IntegrationTests;

/// <summary>Runs only on a test machine with the service installed and writes switched on.</summary>
public sealed class EmployeeWriteFactAttribute : FactAttribute
{
    public const string DatabaseVariable = "AMBIT_SPIKE_EMPLOYEE_DATABASE";

    public EmployeeWriteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(DatabaseVariable) is null)
            Skip = $"Set {DatabaseVariable} to a test company; this test writes to it.";
    }
}
