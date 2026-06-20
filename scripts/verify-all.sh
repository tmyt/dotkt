#!/usr/bin/env bash
# Regression across all samples via the C# backend (the retired dev/oracle path). The shipping default is
# now the IL backend (scripts/verify-il.sh); this harness explicitly opts into C# to keep exercising it as an
# oracle: KOTLIN_CLR_EMIT_CS=1 makes the compiler emit .cs, and KotlinClrBackend=cs (read by MSBuild as a
# property) keeps the .ktproj tests on the C# path. See docs/csharp-retirement-design.md (E-5).
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export KOTLIN_CLR_EMIT_CS=1 KotlinClrBackend=cs

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
check m-s1 clr-ms1 "$(printf 'fallback\npresent\nforced\nlen null = -1\nlen hello = 5')"
check m-d2 clr-md2 "$(printf 'result = 42\nsum = 15\nsafe = -1')"
check m-d2-sm clr-md2sm "$(printf 'chain = 30\nfetchDouble(7) = 14\nuseChain = 35\nsumLoop(4) = 6\nbranch(true) = 15\nbranch(false) = 10')"
check m-s2 clr-ms2 "$(printf 'Point(x=3, y=4)\nPoint(x=7, y=9)\nx=3 y=4\na==b: True\na==c: False\nhash eq: True')"
# A-1: extension functions, exhaustive when(is)+smart cast, arrays, default arguments.
check m-a1 clr-ma1 "$(printf '2\n50\n21\n<def>\n<hi>\n2')"
# A: control flow (when multi-value/range, do-while, labeled break, for) + increments (++/--/+=).
check m-a2 clr-ma2 "$(printf 'low\nmid\nhigh\ndo-while i=3\nbreak at 1,3\nsum=15')"
# B/A: zip + char range + Char.code.
check m-a3 clr-ma3 "$(printf '3\na\nTrue\n97')"
# A: top-level const/val/var, vararg -> params, subjectless when.
check m-a4 clr-ma4 "$(printf 'hi\n100\n5\n10\nB')"
# A: destructuring (Pair/data class) + numeric conversions (toInt/toLong/toDouble).
check m-a5 clr-ma5 "$(printf '7\n63\n3\n5\n2')"
# A: companion object — const (inlined) / non-const val (static field) / factory method (static).
check m-a6 clr-ma6 "$(printf '3.14\ncircle\n3.14')"
# A: local (nested) functions + closure capture.
check m-a7 clr-ma7 "$(printf '25\n15')"
# A: enum rich API — name/ordinal/valueOf/values()/entries.
check m-a8 clr-ma8 "$(printf 'GREEN\n1\nBLUE\nRED\nGREEN\nBLUE\n3')"
# B (LINQ): collection ops map/filter/forEach + value-returning lambdas.
check m-b1 clr-mb1 "$(printf '2\n4\n6\n8\nevens=2')"
# B: scope functions apply/also/let/run/with -> C# IIFEs.
check m-b2 clr-mb2 "$(printf '10\n15\n20\n7\n11')"
# B: collection ops fold/any/all/count/sum/first/take via LINQ.
check m-b3 clr-mb3 "$(printf '15\nTrue\nTrue\n2\n15\n3\n2')"
# B: kotlin.math + String ops.
check m-b4 clr-mb4 "$(printf '5\n8\n4\nHELLO, WORLD\nHello\nHello, CLR\nTrue')"
# B: precondition/error helpers (require/error) + Pair/to (value tuples).
check m-b5 clr-mb5 "$(printf '5\n3\nthree\nok')"
# B: distinct/sorted/reduce/maxOrNull/joinToString + setOf.
check m-b6 clr-mb6 "$(printf '4\n1-2-2-3-4\n12\n4\n3\na, b, c')"
# B: String.split + mapOf (Dictionary) + map indexing.
check m-b7 clr-mb7 "$(printf '3\na|b|c\n1\n2')"
# B: String->number parse, Char predicates, coerce* -> Math.*.
check m-b8 clr-mb8 "$(printf '43\nTrue\nTrue\n7\n5\n5')"
# B: firstOrNull/lastOrNull/none/sumOf/maxByOrNull via LINQ.
check m-b9 clr-mb9 "$(printf '4\n5\nTrue\n28\n1')"
# B: groupBy/associateBy/associateWith -> Dictionary.
check m-b10 clr-mb10 "$(printf '2\n9\n3')"
# B: String.repeat / reversed.
check m-b11 clr-mb11 "$(printf 'ababab\nolleh')"
# B: bitwise / shift operators.
check m-b12 clr-mb12 "$(printf '2\n7\n5\n16\n8\n-6')"
# B: string utilities (isEmpty/isBlank/indexing).
check m-b13 clr-mb13 "$(printf 'True\nTrue\nTrue\nTrue\nb')"
# C-0: REVERSE interop — a C# program consumes a Kotlin-built assembly (ProjectReference).
check revinterop clr-revinterop "$(printf 'Hi, World\n5')"
# C-1: repeat + use (AutoCloseable->IDisposable, close()->Dispose(), try/finally).
check m-c4 clr-mc4 "$(printf 'i=0\ni=1\ni=2\nwork db\nclosed db\nn=2')"
# S5: façade-free, METADATA-DRIVEN .NET type resolution. facadegen reflects over real System.Math /
# System.Console into a metadata file; the compiler's FIR injector synthesizes those types (no .kt).
s5meta="$ROOT/build/clrtypes.meta"
dotnet build "$ROOT/tools/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$s5meta" System.Math System.Console System.Text.StringBuilder >/dev/null 2>&1
CLR_TYPES_METADATA="$s5meta" check m-s5 clr-ms5 "$(printf 'abs(-9) = 9\nmax(3, 7) = 7\nsb.Length = 10\nsb = Hello, CLR')"

# I3: inherit a REAL .NET base type (System.Exception) façade-free; override a .NET virtual member.
i3meta="$ROOT/build/exc.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$i3meta" System.Exception System.Console >/dev/null 2>&1
CLR_TYPES_METADATA="$i3meta" check m-i5 clr-mi5 "$(printf 'AppError #7: 14\nAppError #21: 42')"

# C-1: a Kotlin class IMPLEMENTS a real .NET interface (System.IComparable), façade-free.
icmeta="$ROOT/build/icomp.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$icmeta" System.IComparable System.Console >/dev/null 2>&1
CLR_TYPES_METADATA="$icmeta" check m-c5 clr-mc5 "$(printf '42')"
# C-1: import and use a real .NET enum (System.DayOfWeek), façade-free.
enmeta="$ROOT/build/enum.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$enmeta" System.DayOfWeek System.Console >/dev/null 2>&1
CLR_TYPES_METADATA="$enmeta" check m-c6 clr-mc6 "$(printf 'Friday\nMonday')"
# C-1: read a .NET static field/const (System.Math.PI), façade-free.
mathmeta="$ROOT/build/math.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$mathmeta" System.Math >/dev/null 2>&1
CLR_TYPES_METADATA="$mathmeta" check m-c7 clr-mc7 "$(printf 'True\nTrue')"
# C-1: nullable value types (Int? -> int?) + isEmpty.
check m-c8 clr-mc8 "$(printf 'True\n7\n0')"

# M-I4: compile against an AUTO-GENERATED façade (no hand-written facade.kt).
"$ROOT/scripts/gen-facades.sh" "$ROOT/build/gen-facades" System.Text.StringBuilder >/dev/null 2>&1
check_multi m-i4 clr-mi4 "$ROOT/samples/m-i4/app.kt $ROOT/build/gen-facades" "$(printf 'Hello, CLR 42 True\nlength = 18')"

# S4: compile against an AUTO-GENERATED GENERIC façade (List<T>).
"$ROOT/scripts/gen-facades.sh" "$ROOT/build/gen-gen" System.Collections.Generic.List >/dev/null 2>&1
check_multi m-s4 clr-ms4 "$ROOT/samples/m-s4/app.kt $ROOT/build/gen-gen" "$(printf 'count = 3\nfirst = 10, last = 30\nsum = 139')"

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

# MSBuild + façade-FREE FIR injection via <KotlinClrType> (no .kt façade generated).
injexpected="no-facade via import scan; abs(-5)=5"
injactual="$(dotnet run --project "$ROOT/samples/ktproj-inject/inject.ktproj" -v q --nologo 2>/dev/null | grep -vE 'kotlin/clr:|duplicate source root')"
if [[ "$injactual" == "$injexpected" ]]; then echo "PASS  ktproj-inject (import-scan, façade-free)"; else
	echo "FAIL  ktproj-inject"; echo "--- expected ---"; echo "$injexpected"; echo "--- actual ---"; echo "$injactual"; fail=1
fi
rm -rf "$ROOT/samples/ktproj-inject/bin" "$ROOT/samples/ktproj-inject/obj"

# MSBuild + AssemblyResolver (I2) + .NET event subscription (I4) from a referenced EXTERNAL assembly.
extexpected="$(printf 'Add(2,3) = 5\nchanged: 5\nchanged: 9')"
extactual="$(dotnet run --project "$ROOT/samples/ktproj-extlib/app.ktproj" -v q --nologo 2>/dev/null | grep -vE 'kotlin/clr:|duplicate source root')"
if [[ "$extactual" == "$extexpected" ]]; then echo "PASS  ktproj-extlib (I2 AssemblyResolver)"; else
	echo "FAIL  ktproj-extlib"; echo "--- expected ---"; echo "$extexpected"; echo "--- actual ---"; echo "$extactual"; fail=1
fi
rm -rf "$ROOT/samples/ktproj-extlib/bin" "$ROOT/samples/ktproj-extlib/obj" \
       "$ROOT/samples/ktproj-extlib/extlib/bin" "$ROOT/samples/ktproj-extlib/extlib/obj"

# Framework-direct base class (W1 core): a Kotlin class inherits Avalonia.Application from a
# <PackageReference>, façade-free, overriding a virtual. (Needs Avalonia in the NuGet cache; running
# the UI is out of scope — this only proves a PackageReference type can be a Kotlin base class.)
avexpected="$(printf 'MyApp.Initialize: Kotlin override of Avalonia.Application\nsubclassed Avalonia.Application from Kotlin via PackageReference')"
avactual="$(dotnet run --project "$ROOT/samples/ktproj-avalonia/app.ktproj" -v q --nologo 2>/dev/null | grep -vE 'kotlin/clr:|duplicate source root')"
if [[ "$avactual" == "$avexpected" ]]; then echo "PASS  ktproj-avalonia (inherit PackageReference type)"; else
	echo "FAIL  ktproj-avalonia"; echo "--- expected ---"; echo "$avexpected"; echo "--- actual ---"; echo "$avactual"; fail=1
fi
rm -rf "$ROOT/samples/ktproj-avalonia/bin" "$ROOT/samples/ktproj-avalonia/obj"

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
