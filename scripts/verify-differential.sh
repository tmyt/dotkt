#!/usr/bin/env bash
# F1 differential gate: for pure-Kotlin samples (language + stdlib, no .NET interop), run the SAME
# source on (a) kotlin/jvm — the ground-truth oracle — and (b) kotlin/clr through the SHIPPING IL
# backend (kotc -> bir2cir -> ilemit -> CIL), and assert stdout matches. This validates our codegen +
# stdlib mappings against real Kotlin semantics (not hand-written expecteds). Inputs: the PURE sample
# list below (cases/) + a JVM Kotlin compiler from the Gradle cache. Verdict: exit 0 iff every DIFFing
# sample is in the XFAIL_DIFF baseline below (lib.sh xfail_diff — NEW-FAIL reddens, FIXED means prune).
source "$(dirname "$0")/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the jvm-vs-clr differential over the pure-Kotlin sample list (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

# Prefer the local JDK path, then $JAVA_HOME (CI), then `java` on PATH.
JAVA=/usr/lib/jvm/java-21-openjdk-amd64/bin/java
[[ -x "$JAVA" ]] || JAVA="${JAVA_HOME:+$JAVA_HOME/bin/java}"
[[ -x "$JAVA" ]] || JAVA="$(command -v java)"
STDLIBJ="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.4.0.jar' | head -1)"
EMB="$(find "$HOME/.gradle" -name 'kotlin-compiler-embeddable-2.4.0.jar' | head -1)"
REFLECT="$(find "$HOME/.gradle/caches" -name 'kotlin-reflect-*.jar' | head -1)"
SCRIPT="$(find "$HOME/.gradle/caches" -name 'kotlin-script-runtime-2.4.0.jar' | head -1)"
ANNOT="$(find "$HOME/.gradle/caches" -path '*org.jetbrains/annotations*' -name 'annotations-*.jar' | head -1)"
# kotlin-compiler-embeddable 2.2.0 has an EXTERNAL runtime dep on kotlinx-coroutines-core (its IntelliJ
# CoreApplicationEnvironment refs kotlinx.coroutines.CoroutineScope, which is NOT shaded under
# org.jetbrains.kotlin.*). Without it the oracle K2JVMCompiler dies at startup with
# NoClassDefFoundError: kotlinx/coroutines/CoroutineScope BEFORE compiling anything -> the whole gate goes
# red with empty JVM output. The jar is in the Gradle cache (pulled transitively); put it on the CCP.
COROUTINES="$(find "$HOME/.gradle/caches" -name 'kotlinx-coroutines-core-jvm-*.jar' | sort -V | tail -1)"
CCP="$EMB:$STDLIBJ:$REFLECT:$SCRIPT:$ANNOT:$COROUTINES"   # classpath to RUN the kotlin/jvm compiler

# E-2: the clr side runs through the SHIPPING IL backend, so this harness validates the actual shipping
# path against real Kotlin semantics. Build the toolchain once (UNCONDITIONALLY — the gate tests current sources).
build_tool ilemit; build_tool bir2cir
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
# The CLR stdlib (kotlin.*) is supplied to kotc via the FRONTEND KLIB on the clr side's -classpath
# (the JVM oracle above keeps the separate JVM kotlin-stdlib.jar — it IS the oracle, a different thing).
# bir2cir then reads the REFERENCE assembly for the @Clr labels, and ilemit references the RUNTIME
# assembly so a stdlib op resolves to its real Kotlin body — exactly the canonical ref/rt stdlib that
# dotkt.sh / verify-il use. kotlin.* comes from the klib, never a facadegen reconstruction.
need_fe_klib; need_stdlib_ref; need_stdlib_rt; need_dotnet_reference_sets

# The XFAIL baseline — MACHINE-READABLE (DIFFing sample -> reason), same mechanism as verify-il's
# XFAIL_RUN. The coroutine names mirror verify-il's run-XFAILs. (The m-b6/m-b9/m-b10 entries from the
# 2026-07-02 stdlib subtree bump cde8afd are FIXED — maxOrNull overload-select, sumOf func-return-type
# overload disambiguation, and the groupBy/associate* Map dual-rep variance realignment — and pruned.)
declare -A XFAIL_DIFF=(
)

# Pure-Kotlin samples only (no @Clr / injected .NET types — those can't run on the JVM). Each name is a
# JVM-RUNNABLE plain-Kotlin sample: no `import System.*` / `import kotlin.clr.*`, no injected C# runtime.cs
# (il_check_inject), no coroutine/suspend/Task/sequence-builder cold-core, no other CLR-only construct.
# EXCLUDED and WHY: il_check_imports/il_check_inject samples (interop, can't run on the JVM); the coroutine
# cold-core family (il-cold*/il-co*/il-suspendco/il-seqforin/il-genseq/il-seqyieldall — CLR-specific SM
# lowering, and several have no `fun main` so the JVM oracle can't drive them); the .NET-base/metadata
# il_check samples (il-event/il-netbase*/il-netgen* — inject System.* via facadegen meta). Also EXCLUDED as
# CLR-SPECIFIC-BY-DESIGN (would DIFF for a documented reason, not a bug): String.format .NET composite
# format strings (`{0:F2}`/`{0:D5}`, literal text on the JVM) or `Int::class.simpleName` (CLR "Int32" vs JVM "Int").
#
# COV1 (2026-07-05 kcc review §2B): the ~120 pure il-* samples used to self-score against DotKt-captured
# fixed strings in verify-il, so a Kotlin-INCORRECT mapping passed green forever. Promoting the JVM-runnable
# subset here makes the JVM oracle (real kotlin/jvm) the ground truth — a regression now reddens the gate.
PURE="m0 m-a1 m-a2 m-a3 m-a4 m-a5 m-a6 m-a7 m-a8 m-b1 m-b2 m-b3 m-b4 m-b5 m-b6 m-b7 m-b8 m-b9 m-b10 m-b11 m-b12 m-b13 m-s1 m-s2 m-s3 \
il-samcmp \
il-boxgen il-pairtostr \
il-genmax il-genseq2"
# il-atomics (kotlin.concurrent.atomics CAS family) migrated to tests/il/fixtures/MigratedIntropCAtomicsTests.kt
# (atomics_interlockedByrefBinding); its PURE JVM-oracle entry was removed in that same change.
# NOTE: this is the SINGLE effective PURE set. It was previously followed by three shadowing PURE=… reassignments
# (a cross-task merge artifact from the C1/C2/C4/C5 review fixes) that dropped most of the list — il-nullableprim /
# il-boxgen were silently un-tested. Consolidated into the union above (kcc review C3, 2026-07-06).

# Run samples concurrently (each does a JVM oracle compile+run plus a CLR compile+run — all independent).
# Each stage in diff_eval captures its own exit status (`|| <stage>=…`) so a broken sample reports its own DIFF
# line — with the failing stage + stderr — instead of dying under `set -e` OR silently comparing empty stdout.
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 2 )) && JOBS=$(( JOBS - 2 ))   # use the box (24c): leave 2 cores headroom. Was capped at 6 (stale — /tmp leak is fixed; MEMORY dev-box-resources-parallelize-aggressively)
gate() { while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n 2>/dev/null || true; done; }
# Kotlin.NET primitive formatting is CLR-native by design; normalize platform-cosmetic differences
# (boolean case true/True, double trailing `.0`) so the harness validates LOGIC, not host formatting.
norm() { sed -E 's/\bTrue\b/true/g; s/\bFalse\b/false/g; s/([0-9])\.0\b/\1/g'; }

# diff_eval <name> — run ONE pure sample on both oracles, capturing STDOUT *and* the exit status of EVERY stage
# (issue #163). A nonzero status at any stage (jvm compile/run, kotc, bir2cir, ilemit, clr run) is a DIFF with
# the failing stage + its stderr recorded — NEVER a silent empty-stdout compare. Before the fix every stage
# carried `|| true` and only non-empty stdout was compared, so a program that printed all the expected output and
# THEN threw / returned non-zero was reported MATCH (its exception went to the now-captured stderr). Sets
# DIFF_VERDICT (the MATCH/DIFF/SKIP line) and DIFF_DET (optional detail). Called from the parallel loop AND the
# self-test, so both drive the identical verdict logic.
diff_eval() {
	local s="$1" src="$ROOT/cases/$1"; DIFF_VERDICT="SKIP  $s (no main)"; DIFF_DET=""
	local mainfile; mainfile="$(grep -lE '^fun main' "$src"/*.kt 2>/dev/null | head -1 || true)"
	[[ -n "$mainfile" ]] || return 0
	local base; base="$(basename "$mainfile" .kt)"; local mainclass="${base^}Kt"
	# A `package p` decl puts the JVM main class at `p.<Base>Kt` (the CLR side runs the bare assembly name, so only
	# the JVM `java <class>` invocation needs the FQN — else ClassNotFound). `|| true`: grep exits 1 with no
	# `package` decl and pipefail (lib.sh) would otherwise fail the substitution under `set -e`.
	local pkg; pkg="$(grep -hE '^package ' "$mainfile" 2>/dev/null | head -1 | awk '{print $2}' || true)"
	local jvmmain="${pkg:+$pkg.}$mainclass"

	# (a) kotlin/jvm oracle — compile, then run. Each stage's status is captured independently (stderr -> a log).
	local jout="$ROOT/build/diff-jvm-$s"; rm -rf "$jout"; mkdir -p "$jout"   # worktree-scoped ($ROOT/build), NOT a shared /tmp path — else two worktree gates clobber each other (false-RED)
	local jvm="" jstage=""
	"$JAVA" -cp "$CCP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler "$src"/*.kt -no-stdlib -classpath "$STDLIBJ" -d "$jout" >"$jout/compile.err" 2>&1 || jstage="jvm-compile"
	[[ -n "$jstage" ]] || jvm="$("$JAVA" -cp "$jout:$STDLIBJ" "$jvmmain" 2>"$jout/run.err")" || jstage="jvm-run"

	# (b) kotlin/clr via the SHIPPING IL backend: kotc (frontend klib) -> BIR -> bir2cir -> CIR -> ilemit -> dll, run.
	local cout="$ROOT/build/diff-clr-$s"; rm -rf "$cout"; mkdir -p "$cout"
	local ccir="$ROOT/build/diff-cir-$s"; rm -rf "$ccir"; mkdir -p "$ccir"
	local clr="" cstage=""
	"$KOTC" $src -no-stdlib -classpath "$FE_KLIB" -d "$cout" >"$cout/kotc.err" 2>&1 || cstage="clr-kotc"
	if [[ -z "$cstage" ]]; then
		local compile_refs; compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")"
		dotnet "$BIR2CIR_DLL" "$ccir" --compile-refs "$compile_refs" "$cout"/*.bir.json >"$cout/bir2cir.err" 2>&1 || cstage="clr-bir2cir"
	fi
	[[ -n "$cstage" ]] || dotnet "$ILEMIT_DLL" "$cout" "$mainclass" --runtime-refs "$STDLIB_RT_DLL" "$ccir"/*.cir.json >"$cout/ilemit.err" 2>&1 || cstage="clr-ilemit"
	if [[ -z "$cstage" ]]; then
		cp "$STDLIB_RT_DLL" "$cout/"
		clr="$(dotnet "$cout/$mainclass.dll" 2>"$cout/run.err")" || cstage="clr-run"
	fi

	# Verdict. A nonzero stage on EITHER oracle is a DIFF (record the stage + its stderr). Otherwise both sides
	# must have produced REAL, non-empty stdout AND match after cosmetic normalization; empty-on-either stays a DIFF.
	if [[ -n "$jstage" || -n "$cstage" ]]; then
		local errlog=""; [[ -n "$jstage" ]] && errlog="$jout/${jstage#jvm-}.err"; [[ -n "$cstage" ]] && errlog="$cout/${cstage#clr-}.err"
		DIFF_VERDICT="DIFF  $s"
		DIFF_DET="$(printf -- 'stage-fail: jvm=%s clr=%s\n--- jvm stdout ---\n%s\n--- clr stdout ---\n%s\n--- stderr (%s) ---\n%s' "${jstage:-ok}" "${cstage:-ok}" "$jvm" "$clr" "${jstage:-$cstage}" "$(tail -20 "$errlog" 2>/dev/null)")"
	elif [[ -n "$jvm" && -n "$clr" && "$(norm <<<"$jvm")" == "$(norm <<<"$clr")" ]]; then
		DIFF_VERDICT="MATCH $s"
	else
		DIFF_VERDICT="DIFF  $s"; DIFF_DET="$(printf -- '--- jvm ---\n%s\n--- clr ---\n%s' "$jvm" "$clr")"
	fi
}

# ---- issue #163 self-test: a sample that prints the EXPECTED text then throws MUST be classified DIFF (never
# MATCH). This drives the REAL diff_eval verdict logic, so it fails loudly if the exit-code hole ever reopens. ----
diff_selftest() {
	local d="$ROOT/cases/diff-selftest"; rm -rf "$d"; mkdir -p "$d"
	printf 'fun main() { println("SELFTEST-EXPECTED"); throw RuntimeException("boom after print") }\n' > "$d/St.kt"
	diff_eval diff-selftest
	rm -rf "$d"
	if [[ "$DIFF_VERDICT" != DIFF* ]]; then
		echo "DIFFERENTIAL GATE RED — #163 self-test FAILED: a print-then-crash sample was classified '$DIFF_VERDICT' (must be DIFF; the exit-code hole is open)"; exit 1
	fi
	echo "SELFTEST diff-selftest (print-then-crash correctly classified DIFF, not MATCH)"
}
diff_selftest

# One atomic result record per sample under $RESULTS/res-<name> (MATCH/DIFF/SKIP + optional detail), mv'd into
# place. The old design echoed MATCH/DIFF straight from the parallel `{ } &` subshells to the shared, redirected
# stdout; those children INHERIT ONE file offset, so when the tool cache is warm and samples finish in tight
# bursts their writes seek to the same offset and CLOBBER each other — most MATCH lines silently vanished (the
# same stdout race verify-il already retired). The verdict is still driven off these records, never off stdout.
RESULTS="$ROOT/build/verify-differential"; rm -rf "$RESULTS"; mkdir -p "$RESULTS"
for s in $PURE; do
	gate
	{ diff_eval "$s"; rec="$RESULTS/res-$s"
	  { echo "$DIFF_VERDICT"; [[ -n "$DIFF_DET" ]] && printf '%s\n' "$DIFF_DET"; } > "$rec.tmp"; mv -f "$rec.tmp" "$rec"
	} &
done
wait
# Aggregate the atomic records (sorted) — this, not stdout, is the source of truth for the verdict.
declare -a diff_fails=(); match_n=0; skip_n=0
for f in "$RESULTS"/res-*; do
	[[ -e "$f" ]] || continue
	cat "$f"
	case "$(head -1 "$f")" in
		MATCH*) match_n=$((match_n+1)) ;;
		SKIP*)  skip_n=$((skip_n+1)) ;;
		DIFF*)  diff_fails+=("$(basename "$f" | sed 's/^res-//')") ;;
	esac
done
echo "------------------------------------"
echo "MATCH $match_n   DIFF ${#diff_fails[@]}${diff_fails[@]+ [${diff_fails[*]}]}   SKIP $skip_n"

# ---- verdict: diff the DIFF set against the XFAIL baseline (lib.sh xfail_diff) ----
echo "------------------------------------"
echo "--- baseline diff (XFAIL = expected DIFF; NEW-FAIL = regression; FIXED = prune the xfail entry) ---"
xfail_diff diff XFAIL_DIFF ${diff_fails[@]+"${diff_fails[@]}"}
if (( ${#XFAIL_NEW[@]} )); then
	echo "DIFFERENTIAL GATE RED — DIFF name(s) outside the XFAIL baseline: ${XFAIL_NEW[*]}"
	exit 1
fi
if (( ${#diff_fails[@]} )); then
	echo "DIFFERENTIAL GATE GREEN (every DIFF is XFAIL-listed; any FIXED line above means prune the baseline)"
else
	echo "ALL MATCH (clr == kotlin/jvm)"
fi
