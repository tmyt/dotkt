#!/usr/bin/env bash
# Compile the Kotlin window app with kotlin/clr and launch the real Avalonia window.
# Pass a number as $1 to auto-close after N seconds (otherwise close the window yourself).
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STDLIB="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"

echo ">> compiling samples/win with kotlin/clr"
"$ROOT/gradlew" -q --no-daemon :compiler:run \
	--args="$ROOT/samples/win -no-stdlib -classpath $STDLIB -d $ROOT/build/clr-win" 1>&2

echo ">> launching Avalonia window (Kotlin-driven)"
if [[ -n "${1:-}" ]]; then
	timeout "$1" dotnet run --project "$ROOT/samples/win/runner.csproj" -v q --nologo || true
else
	dotnet run --project "$ROOT/samples/win/runner.csproj" -v q --nologo
fi
