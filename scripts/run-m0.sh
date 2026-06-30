#!/usr/bin/env bash
# End-to-end M0: Kotlin -> (kotlin/clr compiler) -> C# -> dotnet -> stdout, asserted.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/clr-out"
# kotc resolves the stdlib (kotlin.*) from the CLR FRONTEND JAR (scripts/build-clr-stdlib-frontend.sh), NOT the JVM
# kotlin-stdlib.jar (which leaked java.util.* typealiases). Build it once if missing (needs the kotc lib jars).
FE_JAR="$ROOT/build/clr-stdlib-frontend-jvm/kotlin-stdlib-clr-frontend.jar"
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
[[ -f "$FE_JAR" ]] || bash "$ROOT/scripts/build-clr-stdlib-frontend.sh" >/dev/null 2>&1
SRC="${1:-$ROOT/cases/m0/M0.kt}"

echo ">> compiling $SRC with kotlin/clr"
"$ROOT/gradlew" -q --no-daemon :kotc:run \
	--args="$SRC -no-stdlib -classpath $FE_JAR -d $OUT" 1>&2

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
