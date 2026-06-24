#!/usr/bin/env bash
# End-to-end M0: Kotlin -> (kotlin/clr compiler) -> C# -> dotnet -> stdout, asserted.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/clr-out"
STDLIB="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"
SRC="${1:-$ROOT/cases/m0/M0.kt}"

echo ">> compiling $SRC with kotlin/clr"
"$ROOT/gradlew" -q --no-daemon :kotc:run \
	--args="$SRC -no-stdlib -classpath $STDLIB -d $OUT" 1>&2

echo ">> running generated C# on dotnet"
ACTUAL="$(dotnet run --project "$ROOT/cases/m0/runner.csproj" -v q --nologo 2>/dev/null)"

EXPECTED="sum = 5
zero
n=1
n=2"

echo "---- actual ----"
echo "$ACTUAL"
if [[ "$ACTUAL" == "$EXPECTED" ]]; then
	echo "PASS: M0 output matches expected"
else
	echo "FAIL: output mismatch"
	echo "---- expected ----"; echo "$EXPECTED"
	exit 1
fi
