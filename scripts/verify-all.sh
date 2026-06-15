#!/usr/bin/env bash
# Regression across all console (non-GUI) samples: compile each with kotlin/clr, run on dotnet,
# assert stdout. GUI samples (win*, win-kotlin) are launched via run-window.sh instead.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STDLIB="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"
fail=0

check() { # <sample-dir> <out-dir> <expected>
	local src="$ROOT/samples/$1" out="$ROOT/build/$2" expected="$3"
	"$ROOT/gradlew" -q --no-daemon :compiler:run \
		--args="$src -no-stdlib -classpath $STDLIB -d $out" >/dev/null 2>&1
	local actual
	actual="$(dotnet run --project "$src/runner.csproj" -v q --nologo 2>/dev/null)"
	if [[ "$actual" == "$expected" ]]; then
		echo "PASS  $1"
	else
		echo "FAIL  $1"; echo "--- expected ---"; echo "$expected"; echo "--- actual ---"; echo "$actual"
		fail=1
	fi
	rm -rf "$src/bin" "$src/obj"
}

check_multi() { # <sample-name> <out-dir> <source-roots> <expected>
	local name="$1" out="$ROOT/build/$2" roots="$3" expected="$4"
	local src="$ROOT/samples/$name"
	"$ROOT/gradlew" -q --no-daemon :compiler:run \
		--args="$roots -no-stdlib -classpath $STDLIB -d $out" >/dev/null 2>&1
	local actual
	actual="$(dotnet run --project "$src/runner.csproj" -v q --nologo 2>/dev/null)"
	if [[ "$actual" == "$expected" ]]; then echo "PASS  $name"; else
		echo "FAIL  $name"; echo "--- expected ---"; echo "$expected"; echo "--- actual ---"; echo "$actual"; fail=1
	fi
	rm -rf "$src/bin" "$src/obj"
}

check m0   clr-out "$(printf 'sum = 5\nzero\nn=1\nn=2')"
check m2   clr-m2  "$(printf 'max(3, 7) = 7\nmin(3, 7) = 3\nabs(-9) = 9')"
check m-i1 clr-mi1 "$(printf 'Hello, CLR 42\nlength = 13')"
check m-i3 clr-mi3 "$(printf 'count = 3\nfirst = 10, last = 30\nsum after set = 139')"

# M-I4: compile against an AUTO-GENERATED façade (no hand-written facade.kt).
"$ROOT/scripts/gen-facades.sh" "$ROOT/build/gen-facades" System.Text.StringBuilder >/dev/null 2>&1
check_multi m-i4 clr-mi4 "$ROOT/samples/m-i4/app.kt $ROOT/build/gen-facades" "$(printf 'Hello, CLR 42 True\nlength = 18')"

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
