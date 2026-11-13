# Ambit Optima Integrator

A Windows service that connects an HR and payroll web application with Comarch ERP Optima running on a client's own
SQL Server. It asks the web application for work, carries it out in Optima under narrow SQL Server rights, and reports
the result. It opens no port.

What it does today:

- reads every payroll component (deductions and allowances, active or not) each Optima company defines,
- reads one company's payroll lists of a month, with each employee's payout and its elements,
- adds employees to one company or to every active company,
- writes a month's deductions and allowances to employees in one company.

It asks the web application for work every 500 ms by default.

Writes are off by default and are switched on locally, on the client's machine.

## Repository

| Path | What it is |
| --- | --- |
| `src/Ambit.Optima.Integrator.Worker` | The Windows service. It keeps running when the UI is closed. |
| `src/Ambit.Optima.Integrator.Core` | Operations, requests, validation and the interfaces the other projects implement. |
| `src/Ambit.Optima.Integrator.SqlServer` | Data access with Dapper: Optima's companies, employees, deductions, payroll components, the log. |
| `src/Ambit.Optima.Integrator.Infrastructure` | The HTTP client of the HR application and the loop that claims and runs operations. |
| `src/Ambit.Optima.Integrator.Ipc` | The local channel between the service and the desktop UI (named pipe, Unix socket in development). |
| `src/Ambit.Optima.Integrator.Setup` | Run by the installer: database migrations, SQL Server rights, service registration, settings. |
| `src/ambit-optima-integrator-desktop` | The Flutter UI, for status and diagnostics only. It has no SQL logic. |
| `db/migrations` | Schema of the integrator's own database, applied once each by DbUp. |
| `db/grants` | Rights of the service account, applied on every install. |
| `installer` | The Inno Setup installer. |
| `docs` | [Architecture](docs/architecture.md), [the HR application's API](docs/hr-application-api.md), [security](docs/security.md), [what is written to Optima](docs/optima.md). |

## Requirements

- .NET SDK 10 (see `global.json`), Flutter 3.47
- Docker, for the integration tests that start SQL Server in a container
- JetBrains `jb` for the pre-push hook: `dotnet tool install -g JetBrains.ReSharper.GlobalTools`
- Inno Setup 6, to build the installer on Windows

## Getting started

```bash
dotnet restore      # also restores the local tools and installs the git hooks
dotnet build
dotnet test
```

On Linux, for development, the Worker listens on a Unix socket in `$XDG_RUNTIME_DIR`:

```bash
dotnet run --project src/Ambit.Optima.Integrator.Worker
cd src/ambit-optima-integrator-desktop && flutter run -d linux
```

## Tests

- `tests/Ambit.Optima.Integrator.UnitTests` - no external dependencies.
- `tests/Ambit.Optima.Integrator.IntegrationTests`:
  - `FakeOptimaTests` start SQL Server 2022 in Docker, apply the migrations and run the data access code against a
    stand-in for Optima's schema (`FakeOptima/*.sql`). Skipped when Docker is not available.
  - Tests against a real Optima instance run only when their variables are set: `AMBIT_TEST_SQL_SERVER` (reads),
    `AMBIT_SPIKE_EMPLOYEE_DATABASE` (writes to that test company), `AMBIT_SPIKE_EXPECTED_LOGIN` (an installed service).

## Building the installer

```powershell
./installer/build.ps1 -Version 0.4.0
```

The result is `AmbitOptimaIntegratorSetup-<version>.exe`. Silent install:

```text
/VERYSILENT /SUPPRESSMSGBOXES /SERVER=.\OPTIMA /FIRMS=CDN_A,CDN_B [/CONFIGDB=...] [/AMBITURL=...] [/TOKENFILE=...]
```

## Formatting and hooks

```bash
dotnet csharpier check .
dotnet csharpier format .
```

- pre-commit: `csharpier check`, `dotnet build`
- pre-push: `dotnet build`, `scripts/inspect-code.sh`

Switch off with `HUSKY=0`.

## License

[MIT](LICENSE). Comarch ERP Optima is a product of Comarch S.A.; this project is not affiliated with Comarch.
