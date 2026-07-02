#!/usr/bin/env bash
# Generate FIR-injection metadata for .NET types (façade-FREE interop — facadegen's only mode; the old
# @Clr .kt-facade generation is retired, apps take .NET types via `import System.X` + this metadata).
#   scripts/gen-facades.sh <out.meta> <Type.Full.Name> [<Type.Full.Name> ...]
# Pass the result to kotc via CLR_TYPES_METADATA=<out.meta> (the MSBuild targets do this automatically).
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?usage: gen-facades.sh <out.meta> <Type.Full.Name>...}"; shift
[[ -f "$ROOT/build/facadegen-bin/facadegen.dll" ]] || \
	dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo >/dev/null
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$OUT" "$@"
echo "gen-facades: wrote $OUT"
