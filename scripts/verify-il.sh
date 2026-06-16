#!/usr/bin/env bash
# Direct-IL backend differential: Kotlin -> BIR -> ilemit -> CIL -> dotnet, asserted vs the C# oracle.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STDLIB="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"
fail=0

dotnet build "$ROOT/tools/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null

il_check() { # <name> <asm> <srcArg> <expected>
	local name="$1" asm="$2" src="$3" expected="$4"
	local birdir="$ROOT/build/bir-$name" ildir="$ROOT/build/il-$name"
	rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
	"$ROOT/gradlew" -q --no-daemon :compiler:run \
		--args="$src -no-stdlib -classpath $STDLIB -d $birdir" >/dev/null 2>&1
	dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" "$birdir"/*.bir.json >/dev/null
	local actual; actual="$(dotnet "$ildir/$asm.dll")"
	if [[ "$actual" == "$expected" ]]; then echo "PASS  il:$name"; else
		echo "FAIL  il:$name"; echo "--- expected ---"; echo "$expected"; echo "--- actual ---"; echo "$actual"; fail=1
	fi
}

il_check m0    M0Kt  "$ROOT/samples/m0/M0.kt"  "$(printf 'sum = 5\nzero\nn=1\nn=2')"
il_check mc1   MC1   "$ROOT/samples/m-c1"      "$(printf 'c = (4, 6)\na.d2 = 25\nrect area=30')"
il_check iface Iface "$ROOT/samples/il-iface"  "$(printf 'Hello\nKonnichiwa')"
il_check enum  Enum  "$ROOT/samples/il-enum"   "$(printf 'red\ngreen\nblue')"

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "IL ALL PASS" || { echo "IL SOME FAILED"; exit 1; }
