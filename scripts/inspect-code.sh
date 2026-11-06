#!/usr/bin/env bash
# JetBrains inspection of the solution; any finding fails. --no-build when the caller has just built.
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v jb >/dev/null; then
  echo "jb is missing: dotnet tool install -g JetBrains.ReSharper.GlobalTools" >&2
  exit 1
fi
[[ "${1:-}" == "--no-build" ]] || dotnet build --nologo -v q
jb inspectcode Ambit.Agent.slnx --no-build --format=Text --severity=SUGGESTION --output=inspect.txt >/dev/null
if grep -qE '^\s+\S+:[0-9]+' inspect.txt; then
  cat inspect.txt
  exit 1
fi
