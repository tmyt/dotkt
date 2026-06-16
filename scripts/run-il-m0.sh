#!/usr/bin/env bash
# End-to-end CIL path: Kotlin -> BIR JSON -> ilemit -> CIL -> dotnet, asserted against the C# oracle.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STDLIB="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"

echo ">> build ilemit"
dotnet build "$ROOT/tools/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null

echo ">> kotlin -> BIR json"
"$ROOT/gradlew" -q --no-daemon :compiler:run \
	--args="$ROOT/samples/m0/M0.kt -no-stdlib -classpath $STDLIB -d $ROOT/build/clr-bir" >/dev/null 2>&1

echo ">> BIR -> CIL (ilemit)"
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ROOT/build/il-m0" "$ROOT/build/clr-bir/M0.bir.json" >/dev/null

echo ">> run the IL-emitted assembly"
ACTUAL="$(dotnet "$ROOT/build/il-m0/M0Kt.dll")"
EXPECTED="sum = 5
zero
n=1
n=2"
echo "$ACTUAL"
if [[ "$ACTUAL" == "$EXPECTED" ]]; then echo "PASS: CIL path matches the C# oracle"; else
	echo "FAIL"; echo "--- expected ---"; echo "$EXPECTED"; exit 1
fi
