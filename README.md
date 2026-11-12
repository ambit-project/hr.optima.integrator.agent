# Ambit Optima Integrator

A local service that connects Ambit with a client's SQL Server and Comarch ERP Optima. It is not an AI agent: it
fetches assigned tasks from Ambit (by default `https://app.ambitsys.eu`), runs them under controlled rules and reports
the results.

- `src/Ambit.Optima.Integrator.Worker` - the Windows Service; it keeps running when the UI is closed.
- `src/ambit-optima-integrator-desktop` - the Flutter UI, for administration and diagnostics only; no SQL logic in Dart.
- `db/agent-ambit` - the agent's database scripts, embedded in `src/Ambit.Optima.Integrator.Setup`.
- `installer` - the client's installer (`docs/adr/0004-client-installer.md`).
- `docs/adr` - decisions.
- `docs/features.md` - what the agent does today and what is not done yet.

Status: stage 0. So far the Worker answers `status`, `sql.identity` and, when `Writes:Enabled` is set, `employee.add` (also as Ambit's `AddEmployees` operation, ADR 0006)
over IPC. With a token from Ambit, given to the installer (`docs/adr/0005-ambit-connection-in-the-installer.md`), it also
takes operations from Ambit and reads Optima's payroll components (`docs/adr/0003-operations-from-ambit.md`, with the
HTTP contract). With writes on, it also gives employees a month's deductions as Ambit's `ExportDeductions` operation
(`docs/adr/0007-exporting-deductions-from-ambit.md`). Ambit's side of that contract is not finished yet.

## Requirements

- .NET SDK 10 (see `global.json`), Flutter 3.47
- JetBrains `jb` for the pre-push hook: `dotnet tool install -g JetBrains.ReSharper.GlobalTools`

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

Windows Authentication needs Windows. The `windows-service-spike` CI job installs the real service with SQL Server
Express and checks it from C# and Dart. To run those tests on a test machine, set `AMBIT_SPIKE_EXPECTED_LOGIN`.
To run the agent against real Optima data with Ambit's answers faked, set `AMBIT_TEST_SQL_SERVER`.

## Formatting and hooks

```bash
dotnet csharpier check .
dotnet csharpier format .
```

- pre-commit: `csharpier check`, `dotnet build`
- pre-push: `dotnet build`, `scripts/inspect-code.sh`; run `dotnet test` by hand before a PR

Switch off with `HUSKY=0`.
