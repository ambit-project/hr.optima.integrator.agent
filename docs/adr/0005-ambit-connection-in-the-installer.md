# ADR 0005: The installer sets up the connection to Ambit

Status: proposed (2026-11-07). The owner asked for the address and the token in the installer. The other points are the
implementer's, taken from the proposals in the task, and the owner can still change them.

## Context

ADR 0004 left the connection to Ambit off until it was decided where the key lives. An administrator would have had to
edit a settings file by hand. The platform owner now issues each organization a token in Ambit (`ambit.es`, ADR 0073).

## Decision

- The wizard has a page with **Ambit Url**, by default `https://app.ambitsys.eu`, and a masked **Token**. A repeat run shows
  the address in use and cannot show the token, so an empty field keeps it.
- A token switches the connection on (`AmbitApi:Enabled`). The wizard needs one on a first install. A silent install may
  go without one, and the agent then serves only the local UI, as before.
- The address follows the Worker's own rule: https, or http on this machine only. A token must look like one
  (`optima_` and 64 lowercase hexadecimal characters), which catches a bad paste before the service starts.
- The token stays in `appsettings.Production.json`, under `AmbitApi:Key`. The file has no inherited rights and opens only
  to `SYSTEM`, administrators and `NT SERVICE\AmbitOptimaAgent`, which can only read it. The rights are set before the
  token is written, and after the service exists, since its account does not exist before that.
- The token travels from the wizard to `Ambit.Agent.Setup` in a temporary file, deleted right after the call, because any
  local user can read a command line. A silent install names a file of its own:
  `/AMBITURL=https://... /TOKENFILE=C:\path\token.txt`.
- The wizard asks Ambit whether it accepts the token, on `GET api/optima/agent/connection`, which changes nothing there.
  It never uses `claim`, which would take an operation that runs only once. An answer it does not like is a warning the
  administrator can pass, because the client's firewall may open only later. A silent install writes the result to the
  log.

## Alternatives considered

- `AmbitApi__Key` in the service's environment, which any local user can read in the registry.
- DPAPI on top of the file's rights. Machine-scoped protection opens to any local program, so it adds nothing without the
  rights, and it would take the plain view of the settings away from an administrator diagnosing a problem.

## Consequences

- This replaces the point of ADR 0004 that the installer does not switch on the connection. Writes stay off.
- An administrator can change the address or the token by running the installer again, or by editing the file as an
  administrator and restarting the service. Other local users cannot read the file any more.
- A failed connection check does not stop the service, which keeps asking Ambit and logging the failure.
- The default address is the panel, so the agent's requests pass the front's proxy and Cloudflare on their way to the API.
  `api.ambitsys.eu` reaches the API directly, should that path ever cause trouble.
