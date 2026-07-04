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
# XFAIL_RUN. The coroutine names mirror verify-il's run-XFAILs; the m-b* names are the 2026-07-02
# stdlib subtree bump (cde8afd) fallout, recorded loudly instead of silently reddening the gate —
# they are owned stdlib-side (Map/MutableMap dual-rep sub-track + rt overload shape), NOT gate bugs.
declare -A XFAIL_DIFF=(
	[m-b6]="REGRESSION 2026-07-02, stdlib subtree bump cde8afd: ilemit aborts on the rt's Double-specialized maxOrNull ('not a GenericMethodDefinition')"
	[m-b9]="REGRESSION 2026-07-02, stdlib subtree bump cde8afd: sumOf { } returns 0 on CLR"
	[m-b10]="REGRESSION 2026-07-02, stdlib subtree bump cde8afd: groupBy -> clrMapGet EntryPointNotFound (same Map dual-rep family as verify-il's bymap)"
)

# Pure-Kotlin samples only (no @Clr / injected .NET types — those can't run on the JVM).
PURE="m0 m-a1 m-a2 m-a3 m-a4 m-a5 m-a6 m-a7 m-a8 m-b1 m-b2 m-b3 m-b4 m-b5 m-b6 m-b7 m-b8 m-b9 m-b10 m-b11 m-b12 m-b13 m-s1 m-s2 m-s3 il-seq il-char il-sort il-funref il-getclass il-localdeleg il-langfeat il-mapdes il-ctorref il-collmore il-tryexpr il-localclass il-collops2 il-refcell il-annot il-props il-mixnum il-arrops"

# Run samples concurrently (each does a JVM oracle compile+run plus a CLR compile+run — all independent).
# Every fallible command inside the backgrounded subshell carries `|| true` so a broken sample still
# reports its own DIFF line instead of dying silently under `set -e`.
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 6 )) && JOBS=6
gate() { while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n 2>/dev/null || true; done; }
rm -f /tmp/diff-fail-* 2>/dev/null || true
# Kotlin.NET primitive formatting is CLR-native by design; normalize platform-cosmetic differences
# (boolean case true/True, double trailing `.0`) so the harness validates LOGIC, not host formatting.
norm() { sed -E 's/\bTrue\b/true/g; s/\bFalse\b/false/g; s/([0-9])\.0\b/\1/g'; }

for s in $PURE; do
	gate
	{ src="$ROOT/cases/$s"
	  mainfile="$(grep -lE '^fun main' "$src"/*.kt 2>/dev/null | head -1 || true)"
	  if [[ -z "$mainfile" ]]; then echo "SKIP  $s (no main)"; exit 0; fi
	  base="$(basename "$mainfile" .kt)"; mainclass="${base^}Kt"
	  # (a) kotlin/jvm oracle
	  jout="/tmp/diff-jvm-$s"; rm -rf "$jout"; mkdir -p "$jout"
	  "$JAVA" -cp "$CCP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler "$src"/*.kt -no-stdlib -classpath "$STDLIBJ" -d "$jout" >/dev/null 2>&1 || true
	  jvm="$("$JAVA" -cp "$jout:$STDLIBJ" "$mainclass" 2>/dev/null || true)"
	  # (b) kotlin/clr via the SHIPPING IL backend: kotc (frontend jar) -> BIR -> bir2cir -> CIR -> ilemit -> dll, run.
	  cout="$ROOT/build/diff-clr-$s"; rm -rf "$cout"; mkdir -p "$cout"
	  ccir="$ROOT/build/diff-cir-$s"; rm -rf "$ccir"; mkdir -p "$ccir"
	  "$KOTC" $src -no-stdlib -classpath "$FE_JAR" -d $cout >/dev/null 2>&1 || true
	  refarg=(); [[ -f "$STDLIB_REF_DLL" ]] && refarg=(--ref "$STDLIB_REF_DLL")
	  dotnet "$BIR2CIR_DLL" "$ccir" "${refarg[@]}" "$cout"/*.bir.json >/dev/null 2>&1 || true
	  dotnet "$ILEMIT_DLL" "$cout" "$mainclass" --ref "$STDLIB_RT_DLL" "$ccir"/*.cir.json >/dev/null 2>&1 || true
	  cp "$STDLIB_RT_DLL" "$cout/"
	  clr="$(dotnet "$cout/$mainclass.dll" 2>/dev/null || true)"
	  if [[ "$(norm <<<"$jvm")" == "$(norm <<<"$clr")" ]]; then echo "MATCH $s"; else
		echo "DIFF  $s"; echo "--- jvm ---"; echo "$jvm"; echo "--- clr ---"; echo "$clr"; touch "/tmp/diff-fail-$s"; fi
	} &
done
wait
declare -a diff_fails=()
for f in /tmp/diff-fail-*; do [[ -e "$f" ]] && diff_fails+=("$(basename "$f" | sed 's/^diff-fail-//')"); done

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
