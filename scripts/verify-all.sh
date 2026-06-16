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
	actual="$(dotnet run --project "$src/runner.csproj" -v q --nologo 2>/dev/null | grep -vE "warning |error |\\.cs\\(")"
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
	actual="$(dotnet run --project "$src/runner.csproj" -v q --nologo 2>/dev/null | grep -vE "warning |error |\\.cs\\(")"
	if [[ "$actual" == "$expected" ]]; then echo "PASS  $name"; else
		echo "FAIL  $name"; echo "--- expected ---"; echo "$expected"; echo "--- actual ---"; echo "$actual"; fail=1
	fi
	rm -rf "$src/bin" "$src/obj"
}

check m0   clr-out "$(printf 'sum = 5\nzero\nn=1\nn=2')"
check m2   clr-m2  "$(printf 'max(3, 7) = 7\nmin(3, 7) = 3\nabs(-9) = 9')"
check m-i1 clr-mi1 "$(printf 'Hello, CLR 42\nlength = 13')"
check m-i3 clr-mi3 "$(printf 'count = 3\nfirst = 10, last = 30\nsum after set = 139')"
check m-c1 clr-mc1 "$(printf 'c = (4, 6)\na.d2 = 25\nrect area=30')"
check m-c2 clr-mc2 "$(printf 'grade(0)=zero grade(30)=fail grade(90)=pass\nsum 1..5 = 15\ncountdown 5 = 54321\nsafeDiv(10,2)=5 safeDiv(1,0)=-1')"
check m-c3 clr-mc3 "$(printf 'greet: Hello\ngreet: Konnichiwa\nGREEN is green\nRED is red')"
check m-s3 clr-ms3 "$(printf 'size = 3\nsum = 60')"
check m-s1 clr-ms1 "$(printf 'fallback\npresent\nforced')"
check m-s2 clr-ms2 "$(printf 'Point(x=3, y=4)\nPoint(x=7, y=9)\nx=3 y=4')"

# M-I4: compile against an AUTO-GENERATED façade (no hand-written facade.kt).
"$ROOT/scripts/gen-facades.sh" "$ROOT/build/gen-facades" System.Text.StringBuilder >/dev/null 2>&1
check_multi m-i4 clr-mi4 "$ROOT/samples/m-i4/app.kt $ROOT/build/gen-facades" "$(printf 'Hello, CLR 42 True\nlength = 18')"

# MSBuild: build & run a real .ktproj end-to-end via dotnet.
ktexpected="$(printf 'Hello, Visual Studio, from a .ktproj!\nsum 1..5 = 15')"
ktactual="$(dotnet run --project "$ROOT/samples/ktproj/hello.ktproj" -v q --nologo 2>/dev/null | grep -v 'kotlin/clr:')"
if [[ "$ktactual" == "$ktexpected" ]]; then echo "PASS  ktproj (dotnet build)"; else
	echo "FAIL  ktproj"; echo "--- expected ---"; echo "$ktexpected"; echo "--- actual ---"; echo "$ktactual"; fail=1
fi
rm -rf "$ROOT/samples/ktproj/bin" "$ROOT/samples/ktproj/obj"

# MSBuild + auto-generated reference façade.
refexpected="built via dotnet build + facade for 42"
refactual="$(dotnet run --project "$ROOT/samples/ktproj-ref/ref.ktproj" -v q --nologo 2>/dev/null | grep -vE 'kotlin/clr:|duplicate source root')"
if [[ "$refactual" == "$refexpected" ]]; then echo "PASS  ktproj-ref (auto-façade)"; else
	echo "FAIL  ktproj-ref"; echo "--- expected ---"; echo "$refexpected"; echo "--- actual ---"; echo "$refactual"; fail=1
fi
rm -rf "$ROOT/samples/ktproj-ref/bin" "$ROOT/samples/ktproj-ref/obj"

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
