#!/usr/bin/env bash
# Stateful MSBuild integration tests that cannot be expressed as independent NUnit fixtures. They reuse
# one obj/ tree across two builds and deliberately mutate/delete source files between those builds.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT_NAME=msbuild-tests
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the stateful MSBuild integration tests (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

fail=0
WORK="$ROOT/build/tests-msbuild"
mkdir -p "$WORK"
# Build the compiler launcher once (a plain Java app) so the MSBuild EnsureKotlinClrCompiler bootstrap is a no-op.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1

# ktproj_run <absolute-project> <stderr-logfile> — build+run a .ktproj; echo its noise-filtered stdout;
# RETURN the run's exit status (0 iff `dotnet run` — build AND execution — succeeded). Status and stdout are
# captured INDEPENDENTLY (issue #163): the process status is NOT lost to the grep pipe / `|| true` that used to
# mask a program which printed the expected text and THEN threw / returned non-zero.
ktproj_run() { # <project> <stderr-logfile>
	local proj="$1" log="$2" rc=0 raw
	raw="$(dotnet run --project "$proj" -v q --nologo 2>"$log")" || rc=$?
	printf '%s' "$raw" | grep -vE 'kotlin/clr:|duplicate source root' || true
	return $rc
}

# ---- issue #163 self-test: a .ktproj whose main prints the EXPECTED text then throws MUST be REJECTED. Drives the
# real ktproj_run capture path and asserts a non-zero status is observed; a green (exit 0) means the hole is open. ----
ktproj_selftest() {
	local d="$WORK/selftest"; rm -rf "$d"; mkdir -p "$d"
	cat > "$d/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
</Project>
KTPROJ
	printf 'fun main() { println("SELFTEST-EXPECTED"); throw RuntimeException("boom after print") }\n' > "$d/app.kt"
	local rc=0
	ktproj_run "$d/app.ktproj" "$d/run.err" >/dev/null || rc=$?
	rm -rf "$d"
	if (( rc == 0 )); then
		echo "KTPROJ GATE RED — #163 self-test FAILED: a print-then-crash .ktproj was accepted (exit-code hole open)"; exit 1
	fi
	echo "SELFTEST ktproj (print-then-crash correctly REJECTED, run exit $rc)"
}
ktproj_selftest


# Static PackageReference and bidirectional ProjectReference samples now run as NUnit tests under tests/interop and
# tests/roundtrip. This shell gate otherwise remains for stateful MSBuild behavior, plus process-boundary assertions
# (such as the synthesized suspend-main entry point) that cannot be expressed by an in-process NUnit fixture.

# #140: a genuinely-suspending main whose resumed body faults must surface the RAW exception. Task.Wait() wrapped it
# in AggregateException; GetAwaiter().GetResult() follows normal .NET await semantics. This must be a separate process:
# invoking the original suspend declaration via blockOn would bypass the compiler-synthesized plain main drain.
main_fault="$WORK/suspend-main-fault"
rm -rf "$main_fault"; mkdir -p "$main_fault"
cat > "$main_fault/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
</Project>
KTPROJ
cat > "$main_fault/App.kt" <<'KOTLIN'
import System.Threading.Tasks.Task
import kotlin.clr.await

suspend fun main() {
    Task.Delay(1).await()
    throw IllegalStateException("async-main-boom")
}
KOTLIN
main_fault_rc=0
ktproj_run "$main_fault/app.ktproj" "$main_fault/run.err" >/dev/null || main_fault_rc=$?
if (( main_fault_rc != 0 )) \
    && grep -q 'System.InvalidOperationException: async-main-boom' "$main_fault/run.err" \
    && ! grep -q 'AggregateException' "$main_fault/run.err"; then
	echo "PASS  ktproj-suspend-main-raw-fault"
else
	echo "FAIL  ktproj-suspend-main-raw-fault (run exit $main_fault_rc; want raw InvalidOperationException, no AggregateException)"
	tail -20 "$main_fault/run.err" 2>/dev/null
	fail=1
fi
rm -rf "$main_fault"

# #50: INCREMENTAL deletion-safety + staleness through MSBuild. A single dir is built TWICE with the SAME obj/ (no
# clean) — the incremental path the shared targets guard. Between the builds a top-level `class Shape` is MOVED out of
# its own Shape.kt into App.kt and Shape.kt is DELETED. Pre-#50 the BIR was globbed from $(DotKtOut), which was never
# cleaned, so the deleted Shape.kt left a stale Shape.bir.json behind → Shape was emitted TWICE (App.cir.json's moved
# copy + the orphan Shape.cir.json) → ilemit "type already defined" → the second build FAILED. The fix wipes
# $(DotKtOut) on every recompile, so the stale artifact cannot survive. This case reproduces that exact failure and
# asserts BOTH builds run "12" (the deleted source is gone from the emitted dll). The dir is generated + removed here
# (not a committed sample) because the assertion is a stateful two-build mutation, not a single `dotnet run`.
incr="$WORK/incremental-delete"
rm -rf "$incr"; mkdir -p "$incr"
cat > "$incr/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
</Project>
KTPROJ
# STATE 1: `class Shape` lives in its own file. (Both builds capture the run status independently — issue #163 —
# so a build/run that prints "12" then fails is not silently accepted.)
printf 'fun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\n' > "$incr/Shape.kt"
incr_rc1=0; incr1="$(ktproj_run "$incr/app.ktproj" "$incr/run1.err")" || incr_rc1=$?
# STATE 2: MOVE `class Shape` into App.kt and DELETE Shape.kt — rebuild on the SAME obj/ (incremental).
rm -f "$incr/Shape.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\nfun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
incr_rc2=0; incr2="$(ktproj_run "$incr/app.ktproj" "$incr/run2.err")" || incr_rc2=$?
if [[ $incr_rc1 -eq 0 && $incr_rc2 -eq 0 && "$incr1" == "12" && "$incr2" == "12" ]]; then echo "PASS  ktproj-incr"; else
	echo "FAIL  ktproj-incr (build1 exit $incr_rc1, build2 exit $incr_rc2)"
	printf -- '--- build1 (want 12) ---\n%s\n--- build2 incremental after delete (want 12) ---\n%s\n--- stderr build2 ---\n%s\n' "$incr1" "$incr2" "$(tail -20 "$incr/run2.err" 2>/dev/null)"; fail=1
fi
rm -rf "$incr"

# Clean each sample's build output.
echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
