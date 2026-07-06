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
STDLIBJ="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"
EMB="$(find "$HOME/.gradle" -name 'kotlin-compiler-embeddable-2.2.0.jar' | head -1)"
REFLECT="$(find "$HOME/.gradle/caches" -name 'kotlin-reflect-*.jar' | head -1)"
SCRIPT="$(find "$HOME/.gradle/caches" -name 'kotlin-script-runtime-2.2.0.jar' | head -1)"
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
# The CLR stdlib (kotlin.*) is supplied to kotc via the FRONTEND JAR on the clr side's -classpath,
# REPLACING the JVM kotlin-stdlib.jar (the JVM oracle above keeps the JVM jar — it IS the oracle).
# bir2cir then reads the REFERENCE assembly for the @Clr labels, and ilemit references the RUNTIME
# assembly so a stdlib op resolves to its real Kotlin body — exactly the canonical ref/rt stdlib that
# dotkt.sh / verify-il use. kotlin.* comes from the jar, never a facadegen reconstruction.
need_fe_jar; need_stdlib_ref; need_stdlib_rt

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
# CLR-SPECIFIC-BY-DESIGN (would DIFF for a documented reason, not a bug): il-bmore / il-fmt (String.format
# uses .NET COMPOSITE format strings `{0:F2}`/`{0:D5}` — literal text on the JVM, not printf); il-reified
# (prints `Int::class.simpleName` = the CLR name "Int32", whereas the JVM prints "Int").
#
# COV1 (2026-07-05 kcc review §2B): the ~120 pure il-* samples used to self-score against DotKt-captured
# fixed strings in verify-il, so a Kotlin-INCORRECT mapping passed green forever. Promoting the JVM-runnable
# subset here makes the JVM oracle (real kotlin/jvm) the ground truth — a regression now reddens the gate.
PURE="m0 m-a1 m-a2 m-a3 m-a4 m-a5 m-a6 m-a7 m-a8 m-b1 m-b2 m-b3 m-b4 m-b5 m-b6 m-b7 m-b8 m-b9 m-b10 m-b11 m-b12 m-b13 m-s1 m-s2 m-s3 \
il-seq il-char il-sort il-funref il-getclass il-localdeleg il-langfeat il-mapdes il-ctorref il-collmore il-tryexpr il-localclass il-collops2 il-refcell il-annot il-props il-mixnum il-arrops \
il-arr il-blank il-bymap il-bytearg il-charminus il-charseq il-charseqs il-charseqx il-chunk il-closure il-cmpord il-coerce il-coll il-coll2 il-coll3 il-collrealkt il-colstr il-comparable il-comparator il-cp il-ctor il-customexc il-deleg il-deleg2 il-digittoint il-dsl il-duration il-emptymap il-enum il-enumbody il-enumrich il-exc il-excmap il-exprbody il-ext il-for il-genbase il-genclosure il-gencolladd il-genctor il-generic il-generic2 il-generic3 il-generic4 il-generic5 il-generic6 il-genhof il-genstatic il-gfac il-groupvalues il-hashset2 il-iface il-inline il-inline2 il-inner il-interpnull il-iscoll il-iter il-iterable il-lambda il-langtail il-lazy il-loopjump il-math il-mapfilter il-mapforin il-mapgen il-mapof1 il-maptostr il-mfclosure il-mflambda il-mutcoll il-mutset il-nan il-nancmp il-nested il-nestedstr il-nestedtry il-ntostr il-null il-nulltostr il-object il-objexpr il-objgen il-op il-ops il-overload il-overrideprop il-pair il-printlnnull il-rangein il-regex il-regexgroups il-regexreplace il-reqnn il-result il-rwp il-safecallnv il-samcmp il-scope il-seqfilter il-setlocalbox il-smartcast il-str il-strnum il-strops il-subseq il-substr il-throwexpr il-tryexprop il-trynullable il-unsigned il-use il-valclass il-vis il-volatile il-whensubj il-xfaceimpl il-xinline il-xprop \
il-nullableprim il-boxgen il-mathabs il-radix il-strhash il-pairtostr il-extprop il-defargs il-defargs2 il-negzero il-listeq il-indices il-pairnest il-mapmerge \
il-genmax il-listplus il-divmin il-nestlam il-genseq2 il-cwindowed il-cwindowedv il-eachcount il-groupby2 il-mapvalues il-indicesv \
il-triple il-typealias il-atomics il-tailrec il-copydef il-equalscall"
# COV2/COV3/COV4 (kcc review §2B, 2026-07-06): il-atomics (kotlin.concurrent.atomics — the @ClrRefArgument
# Interlocked byref binding; API restricted to the released 2.2.0 surface so the JVM oracle resolves it),
# il-typealias (typealias over stdlib generic / function type / user class across a fn boundary), il-triple
# (Triple ctor/destructure/componentN/full-arg copy/toString). All three JVM-oracle-verified.
# il-tailrec (2026-07-06, §2b CLOSED): deep `tailrec` is now TCO'd to a back-jump loop in kotc, so a
# million-frame self/when/extension-receiver/member tailrec runs in constant stack and MATCHes the JVM
# oracle (which also TCOs it) — promoted into PURE (was excluded as a documented deviation/crash).
# il-copydef (C3): data-class copy(field=x) partial fill; il-equalscall (§5a): explicit .equals() routing.
# NOTE: this is the SINGLE effective PURE set. It was previously followed by three shadowing PURE=… reassignments
# (a cross-task merge artifact from the C1/C2/C4/C5 review fixes) that dropped most of the list — il-nullableprim /
# il-boxgen were silently un-tested. Consolidated into the union above (kcc review C3, 2026-07-06).

# Run samples concurrently (each does a JVM oracle compile+run plus a CLR compile+run — all independent).
# Every fallible command inside the backgrounded subshell carries `|| true` so a broken sample still
# reports its own DIFF line instead of dying silently under `set -e`.
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 6 )) && JOBS=6
gate() { while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n 2>/dev/null || true; done; }
# Kotlin.NET primitive formatting is CLR-native by design; normalize platform-cosmetic differences
# (boolean case true/True, double trailing `.0`) so the harness validates LOGIC, not host formatting.
norm() { sed -E 's/\bTrue\b/true/g; s/\bFalse\b/false/g; s/([0-9])\.0\b/\1/g'; }

# One atomic result record per sample under $RESULTS/res-<name> (MATCH/DIFF/SKIP + optional detail), mv'd into
# place. The old design echoed MATCH/DIFF straight from the parallel `{ } &` subshells to the shared, redirected
# stdout; those children INHERIT ONE file offset, so when the tool cache is warm and samples finish in tight
# bursts their writes seek to the same offset and CLOBBER each other — most MATCH lines silently vanished (the
# same stdout race verify-il already retired). The verdict is still driven off these records, never off stdout.
RESULTS="$ROOT/build/verify-differential"; rm -rf "$RESULTS"; mkdir -p "$RESULTS"
for s in $PURE; do
	gate
	{ src="$ROOT/cases/$s"; rec="$RESULTS/res-$s"; verdict="SKIP  $s (no main)"; det=""
	  mainfile="$(grep -lE '^fun main' "$src"/*.kt 2>/dev/null | head -1 || true)"
	  if [[ -n "$mainfile" ]]; then
	    base="$(basename "$mainfile" .kt)"; mainclass="${base^}Kt"
	    # A `package p` decl puts the JVM main class at `p.<Base>Kt` (the CLR side runs the bare assembly name,
	    # so only the JVM `java <class>` invocation needs the FQN — else ClassNotFound -> empty JVM stdout -> false DIFF).
	    # `|| true`: grep exits 1 when there is no `package` decl, and `set -o pipefail` (lib.sh) would then
	    # make this whole substitution fail under `set -e`, killing the background job before it writes its record.
	    pkg="$(grep -hE '^package ' "$mainfile" 2>/dev/null | head -1 | awk '{print $2}' || true)"
	    jvmmain="${pkg:+$pkg.}$mainclass"
	    # (a) kotlin/jvm oracle
	    jout="/tmp/diff-jvm-$s"; rm -rf "$jout"; mkdir -p "$jout"
	    "$JAVA" -cp "$CCP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler "$src"/*.kt -no-stdlib -classpath "$STDLIBJ" -d "$jout" >/dev/null 2>&1 || true
	    jvm="$("$JAVA" -cp "$jout:$STDLIBJ" "$jvmmain" 2>/dev/null || true)"
	    # (b) kotlin/clr via the SHIPPING IL backend: kotc (frontend jar) -> BIR -> bir2cir -> CIR -> ilemit -> dll, run.
	    cout="$ROOT/build/diff-clr-$s"; rm -rf "$cout"; mkdir -p "$cout"
	    ccir="$ROOT/build/diff-cir-$s"; rm -rf "$ccir"; mkdir -p "$ccir"
	    "$KOTC" $src -no-stdlib -classpath "$FE_JAR" -d $cout >/dev/null 2>&1 || true
	    refarg=(); [[ -f "$STDLIB_REF_DLL" ]] && refarg=(--ref "$STDLIB_REF_DLL")
	    dotnet "$BIR2CIR_DLL" "$ccir" "${refarg[@]}" "$cout"/*.bir.json >/dev/null 2>&1 || true
	    dotnet "$ILEMIT_DLL" "$cout" "$mainclass" --ref "$STDLIB_RT_DLL" "$ccir"/*.cir.json >/dev/null 2>&1 || true
	    cp "$STDLIB_RT_DLL" "$cout/"
	    clr="$(dotnet "$cout/$mainclass.dll" 2>/dev/null || true)"
	    # A MATCH requires BOTH the jvm oracle and the clr side to have produced REAL, non-empty output.
	    # Every fallible command above carries `|| true`, so a sample that fails to compile/run yields an
	    # EMPTY stdout; without this guard two empty outputs compare equal -> a FALSE "MATCH" that silently
	    # passes a broken sample (the latent empty==empty hole). Empty on EITHER side is therefore a FAIL.
	    if [[ -n "$jvm" && -n "$clr" && "$(norm <<<"$jvm")" == "$(norm <<<"$clr")" ]]; then verdict="MATCH $s"; else
	      verdict="DIFF  $s"; det="$(printf -- '--- jvm ---\n%s\n--- clr ---\n%s' "$jvm" "$clr")"; fi
	  fi
	  { echo "$verdict"; [[ -n "$det" ]] && printf '%s\n' "$det"; } > "$rec.tmp"; mv -f "$rec.tmp" "$rec"
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
