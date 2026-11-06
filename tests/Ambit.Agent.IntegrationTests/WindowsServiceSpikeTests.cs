using System.IO.Pipes;
using System.Text.Json;
using Ambit.Agent.Ipc;

namespace Ambit.Agent.IntegrationTests;

/// <summary>
/// The stage 0 proof, with no mocks: the installed service answers on its pipe and reaches SQL Server
/// through Windows Authentication under its own account, not under the account of whoever is asking.
/// </summary>
public sealed class WindowsServiceSpikeTests
{
    [WindowsServiceFact]
    public async Task The_service_signs_in_to_SQL_Server_as_itself()
    {
        var expected = Environment.GetEnvironmentVariable(
            WindowsServiceFactAttribute.ExpectedLoginVariable
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var pipe = new NamedPipeClientStream(
            ".",
            IpcEndpoint.DefaultPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        await pipe.ConnectAsync(timeout.Token);

        await IpcFraming.WriteAsync(
            pipe,
            new IpcRequest(IpcFraming.CurrentVersion, IpcCommands.SqlIdentity),
            timeout.Token
        );
        var response = await IpcFraming.ReadAsync<IpcResponse>(pipe, timeout.Token);

        Assert.True(response!.Ok, response.Error);
        Assert.Equal(expected, response.Data!.Value.GetProperty("loginName").GetString());
    }

    [EmployeeWriteFact]
    public async Task The_service_adds_an_employee_once_over_its_pipe()
    {
        var database = Environment.GetEnvironmentVariable(
            EmployeeWriteFactAttribute.DatabaseVariable
        );
        var employeeId = Guid.NewGuid();
        var code = $"T{employeeId:N}"[..20];

        var first = await AddAsync(employeeId, code, database!);
        var second = await AddAsync(employeeId, code, database!);

        Assert.Equal("Succeeded", first.GetProperty("status").GetString());
        Assert.Equal("AlreadyExists", second.GetProperty("status").GetString());
        Assert.Equal(
            first.GetProperty("optimaEmployeeId").GetInt32(),
            second.GetProperty("optimaEmployeeId").GetInt32()
        );
    }

    private static async Task<JsonElement> AddAsync(Guid employeeId, string code, string database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var pipe = new NamedPipeClientStream(
            ".",
            IpcEndpoint.DefaultPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        await pipe.ConnectAsync(timeout.Token);

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                operationId = Guid.NewGuid(),
                employeeId,
                employeeCode = code,
                lastName = "Testowy",
                firstName = "Ambit",
                databaseName = database,
                dateOfBirth = "1990-05-20",
                citizenshipCode = "UA",
                documentKind = "Passport",
                documentNumber = "AB1234567",
                startsOn = "2024-01-15",
                operatorId = 1,
            }
        );
        await IpcFraming.WriteAsync(
            pipe,
            new IpcRequest(IpcFraming.CurrentVersion, IpcCommands.EmployeeAdd, payload),
            timeout.Token
        );
        var response = await IpcFraming.ReadAsync<IpcResponse>(pipe, timeout.Token);

        Assert.True(response!.Ok, response.Error);
        return response.Data!.Value.GetProperty("results")[0];
    }
}
