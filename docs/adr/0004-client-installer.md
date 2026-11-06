# ADR 0004: The client's installer

Status: proposed (2026-11-06). The owner left the choice of tool and the details to the implementer.

## Context

Installing the agent meant publishing the Worker by hand, running `sc create`, writing its settings and running
`install.ps1`. That script needed `sqlcmd` on the machine and gave the Worker every listed company unless an
administrator remembered to exclude one.

## Decision

- One `AmbitOptimaAgentSetup-<version>.exe`, built with Inno Setup by `installer/build.ps1`. An administrator runs it by
  hand, which is how we expect clients to install it, so MSI's support for mass deployment is not worth its extra work.
- The wizard only asks and copies. `src/Ambit.Agent.Setup` does the rest, is unpacked to a temporary folder and is not
  left on the machine. It carries the scripts of `db/agent-ambit` and runs them without `sqlcmd`.
- Access to companies is chosen, not excluded: on a first install nothing is ticked and at least one company is needed.
  The Worker ends up with exactly the ticked companies, so clearing one drops its database user there. This replaces
  `install.ps1`, which is gone.
- A new company, or a change of choice, means running the installer again. An upgrade keeps the local settings file.
- The end of the installation proves the result: the service answers on its pipe and signs in to SQL Server as itself.
- Silent install, for repeatable tests and scripts:
  `/VERYSILENT /SUPPRESSMSGBOXES /SERVER=.\OPTIMA /FIRMS=CDN_A,CDN_B [/CONFIGDB=...] [/AMBITURL=...] [/TOKENFILE=...]`.
- The Flutter UI's Visual C++ runtime travels next to the app, because a clean server may not have it.
- The installer closes a running UI first, because one hidden in the tray would hold its files open.

## Consequences

- The installer does not switch on writes. ADR 0005 replaces the rest of this point: a token given to the installer
  switches on the connection to Ambit.
- The installer's account must be sysadmin on the instance. The Worker's own account does not get that.
- Settings trust SQL Server's certificate by default, since a default install has a self-signed one. An administrator
  can turn that off in `appsettings.Production.json`, and an upgrade keeps the change.
- Uninstalling removes the service and the program but keeps `agent-ambit`, the SQL login and the database users, so the
  logs survive. Removing them is a manual step for now.
- The installer is not code-signed yet, so Windows SmartScreen warns about it. A code-signing certificate is needed
  before it goes to clients.
