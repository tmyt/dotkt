#!/usr/bin/env bash
# MSBuild / .ktproj end-to-end integration gate on the SHIPPING IL backend (the default; no C# backend
# involved). Builds & runs real .ktproj (and reverse-interop .csproj) samples via `dotnet run`, asserting
# stdout. This is the only MSBuild-level gate now that the C# backend is retired — its old harness
# (verify-all.sh) was removed because there's no point regression-testing a backend we no longer ship.
# See docs/csharp-retirement-design.md. Inputs: cases/ktproj*/ + the toolchain. Exits nonzero on any FAIL.
source "$(dirname "$0")/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs every .ktproj integration sample (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

fail=0
# Build the compiler launcher once (a plain Java app) so the MSBuild EnsureKotlinClrCompiler bootstrap is a no-op.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1

# ktproj_run <project> <stderr-logfile>  — build+run a .ktproj on the IL backend; echo its noise-filtered stdout;
# RETURN the run's exit status (0 iff `dotnet run` — build AND execution — succeeded). Status and stdout are
# captured INDEPENDENTLY (issue #163): the process status is NOT lost to the grep pipe / `|| true` that used to
# mask a program which printed the expected text and THEN threw / returned non-zero.
ktproj_run() { # <project> <stderr-logfile>
	local proj="$1" log="$2" rc=0 raw
	raw="$(dotnet run --project "$ROOT/$proj" -v q --nologo 2>"$log")" || rc=$?
	printf '%s' "$raw" | grep -vE 'kotlin/clr:|duplicate source root' || true
	return $rc
}

# <name> <project> <expected>  — build+run a project on the IL backend and diff stdout. A non-zero run status is a
# FAIL (recording stderr + the failing stage) BEFORE any output compare — never masked, so one broken sample is
# reported as its own FAIL line and the gate still runs every remaining sample and summarizes at the end.
kt() {
	local name="$1" proj="$2" expected="$3"
	local actual rc=0 log="$ROOT/build/ktproj-run-$name.err"
	mkdir -p "$ROOT/build"
	actual="$(ktproj_run "$proj" "$log")" || rc=$?
	if (( rc != 0 )); then
		echo "FAIL  $name (run exit $rc)"
		printf -- '--- expected ---\n%s\n--- actual (stdout before failure) ---\n%s\n--- stderr ---\n%s\n' "$expected" "$actual" "$(tail -30 "$log" 2>/dev/null)"; fail=1; return
	fi
	if [[ "$actual" == "$expected" ]]; then echo "PASS  $name"; else
		echo "FAIL  $name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; fail=1
	fi
}

# ---- issue #163 self-test: a .ktproj whose main prints the EXPECTED text then throws MUST be REJECTED. Drives the
# real ktproj_run capture path and asserts a non-zero status is observed; a green (exit 0) means the hole is open. ----
ktproj_selftest() {
	local d="$ROOT/cases/ktproj-selftest"; rm -rf "$d"; mkdir -p "$d"
	cat > "$d/app.ktproj" <<'KTPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
  <Import Project="../KotlinClr.targets" />
</Project>
KTPROJ
	printf 'fun main() { println("SELFTEST-EXPECTED"); throw RuntimeException("boom after print") }\n' > "$d/app.kt"
	local rc=0
	ktproj_run "cases/ktproj-selftest/app.ktproj" "$d/run.err" >/dev/null || rc=$?
	rm -rf "$d"
	if (( rc == 0 )); then
		echo "KTPROJ GATE RED — #163 self-test FAILED: a print-then-crash .ktproj was accepted (exit-code hole open)"; exit 1
	fi
	echo "SELFTEST ktproj (print-then-crash correctly REJECTED, run exit $rc)"
}
ktproj_selftest

# A real .ktproj end-to-end.
kt ktproj "cases/ktproj/hello.ktproj" \
	"$(printf 'Hello, Visual Studio, from a .ktproj!\nsum 1..5 = 15')"

# Import-driven .NET resolution: plain `import System.Text.StringBuilder` / `import System.Math`, no <KotlinClrFacade>,
# no facade — the facadegen import scan injects the types. Fluent StringBuilder.Append chaining + Math.Max.
# Wired here (COV6, 2026-07-06): was UNWIRED (previously no gate covered the bare-import ktproj path).
kt ktproj-import "cases/ktproj-import/import.ktproj" \
	"dotkt imports just work: 40"

# BIDIRECTIONAL ProjectReference (R-1): cslib.csproj <- klib.ktproj <- app.csproj in one graph.
# forward = Kotlin imports the C# Theme.Palette; reverse = C# consumes the Kotlin Greeter + its List<String>
# at compile time (needs the emitted dll reference-clean via the retarget tool). Running the C# host drives all.
kt ktproj-bidir "cases/ktproj-bidir/app/app.csproj" \
	"$(printf 'Hi, Visual Studio (accent=cyan)\nVisual Studio A, Visual Studio B, Visual Studio C')"

# Framework-direct base class: a Kotlin class inherits Avalonia.Application from a <PackageReference>,
# façade-free, overriding a virtual. (Needs Avalonia in the NuGet cache.)
kt ktproj-avalonia "cases/ktproj-avalonia/app.ktproj" \
	"$(printf 'MyApp.Initialize: Kotlin override of Avalonia.Application\nsubclassed Avalonia.Application from Kotlin via PackageReference')"

# PRACTICAL COLLECTIONS app consuming the real CLR stdlib (DotKt.Stdlib.dll): a List held as an app local (resolves as
# the referenced IReadOnlyList), member access (size/indexing), TOP-LEVEL stdlib funs (first/getOrElse/contains/indexOf/
# count/isEmpty/take) which kotc emits as `callStatic owner=null` and bir2cir attributes to their file-class owner
# (kotlin.collections._CollectionsKt), AND `for (x in list)` (the iterator protocol re-pointed at the real referenced
# kotlin.collections.Iterator<E> via the rt bridge). The whole app-consume gap, end-to-end through MSBuild.
kt ktproj-coll "cases/ktproj-coll/app.ktproj" \
	"$(printf '5\n30\n10\n20\n-1\nTrue\n3\n5\nFalse\n2\n150\nAPPLE\npear\n5\n4\n3')"

# #37 finding 1 (RID-aware identity selection): a PackageReference (System.IO.Ports) whose copy-local set carries
# BOTH lib/<tfm>/Foo.dll and runtimes/<rid>/lib/<tfm>/Foo.dll for ONE identity. ilemit's runtime catalog used to
# hard-fail at emit on the duplicate simple name; it now dedups by identity and selects the host-RID asset. On Linux
# the runtimes/unix/lib build is the REAL impl (the plain lib asset is a PlatformNotSupported placeholder), so
# GetPortNames() returning a count (0 here) — not throwing — proves the RID-correct asset was selected (keep-first
# would have picked the placeholder). Regression guard for #37 finding 1.
kt ktproj-runtimetargets "cases/ktproj-runtimetargets/app.ktproj" \
	"ports 0"

# #37 finding 3 (catalog-first, TPA-fallback): framework/inbox types NOT copy-local (absent from the runtime
# catalog) — System.Text.Json.JsonSerializerOptions + System.Net.Http.HttpClient — must resolve via the fallback
# onto ilemit's own host framework (TPA). Before the fix these hard-failed "cannot resolve .NET type". Regression
# guard for #37 finding 3.
kt ktproj-inbox "cases/ktproj-inbox/app.ktproj" \
	"$(printf 'indented False\ntimeout 100')"

# #50: INCREMENTAL deletion-safety + staleness through MSBuild. A single dir is built TWICE with the SAME obj/ (no
# clean) — the incremental path the shared targets guard. Between the builds a top-level `class Shape` is MOVED out of
# its own Shape.kt into App.kt and Shape.kt is DELETED. Pre-#50 the BIR was globbed from $(DotKtOut), which was never
# cleaned, so the deleted Shape.kt left a stale Shape.bir.json behind → Shape was emitted TWICE (App.cir.json's moved
# copy + the orphan Shape.cir.json) → ilemit "type already defined" → the second build FAILED. The fix wipes
# $(DotKtOut) on every recompile, so the stale artifact cannot survive. This case reproduces that exact failure and
# asserts BOTH builds run "12" (the deleted source is gone from the emitted dll). The dir is generated + removed here
# (not a committed sample) because the assertion is a stateful two-build mutation, not a single `dotnet run`.
incr="$ROOT/cases/ktproj-incr"
rm -rf "$incr"; mkdir -p "$incr"
cat > "$incr/app.ktproj" <<'KTPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <Import Project="../KotlinClr.targets" />
</Project>
KTPROJ
# STATE 1: `class Shape` lives in its own file. (Both builds capture the run status independently — issue #163 —
# so a build/run that prints "12" then fails is not silently accepted.)
printf 'fun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\n' > "$incr/Shape.kt"
incr_rc1=0; incr1="$(ktproj_run "cases/ktproj-incr/app.ktproj" "$incr/run1.err")" || incr_rc1=$?
# STATE 2: MOVE `class Shape` into App.kt and DELETE Shape.kt — rebuild on the SAME obj/ (incremental).
rm -f "$incr/Shape.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\nfun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
incr_rc2=0; incr2="$(ktproj_run "cases/ktproj-incr/app.ktproj" "$incr/run2.err")" || incr_rc2=$?
if [[ $incr_rc1 -eq 0 && $incr_rc2 -eq 0 && "$incr1" == "12" && "$incr2" == "12" ]]; then echo "PASS  ktproj-incr"; else
	echo "FAIL  ktproj-incr (build1 exit $incr_rc1, build2 exit $incr_rc2)"
	printf -- '--- build1 (want 12) ---\n%s\n--- build2 incremental after delete (want 12) ---\n%s\n--- stderr build2 ---\n%s\n' "$incr1" "$incr2" "$(tail -20 "$incr/run2.err" 2>/dev/null)"; fail=1
fi
rm -rf "$incr"

# Clean each sample's build output.
rm -rf "$ROOT"/cases/ktproj/bin "$ROOT"/cases/ktproj/obj \
       "$ROOT"/cases/ktproj-import/bin "$ROOT"/cases/ktproj-import/obj \
       "$ROOT"/cases/ktproj-bidir/*/bin "$ROOT"/cases/ktproj-bidir/*/obj \
       "$ROOT"/cases/ktproj-coll/bin "$ROOT"/cases/ktproj-coll/obj \
       "$ROOT"/cases/ktproj-runtimetargets/bin "$ROOT"/cases/ktproj-runtimetargets/obj \
       "$ROOT"/cases/ktproj-inbox/bin "$ROOT"/cases/ktproj-inbox/obj \
       "$ROOT"/cases/ktproj-avalonia/bin "$ROOT"/cases/ktproj-avalonia/obj

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
