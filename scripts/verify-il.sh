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
il_check m2    M2    "$ROOT/samples/m2"         "$(printf 'max(3, 7) = 7\nmin(3, 7) = 3\nabs(-9) = 9')"
il_check mi1   MI1   "$ROOT/samples/m-i1"       "$(printf 'Hello, CLR 42\nlength = 13')"

# Formal IL verification (ilverify), if the tool is available.
ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
REFDIR="$(dirname "$(find /usr/share/dotnet/shared/Microsoft.NETCore.App -name System.Private.CoreLib.dll 2>/dev/null | sort | tail -1)")"
if [[ -n "$ILV" && -d "$REFDIR" ]]; then
	echo "--- ilverify ---"
	declare -A ASMS=( [m0]=M0Kt [mc1]=MC1 [iface]=Iface [enum]=Enum [m2]=M2 [mi1]=MI1 )
	for n in "${!ASMS[@]}"; do
		dll="$ROOT/build/il-$n/${ASMS[$n]}.dll"
		[[ -f "$dll" ]] || continue
		if dotnet "$ILV" "$dll" -r "$REFDIR/*.dll" 2>&1 | grep -qi 'Verified\.'; then echo "VERIFY  $n"; else echo "VERIFY FAIL  $n"; fail=1; fi
	done
else
	echo "(ilverify not installed; skipping formal verification — 'dotnet tool install -g dotnet-ilverify')"
fi

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "IL ALL PASS" || { echo "IL SOME FAILED"; exit 1; }
