#!/usr/bin/env bash
# F1 differential harness: for pure-Kotlin samples (language + stdlib, no .NET interop), run the SAME
# source on (a) kotlin/jvm — the ground-truth oracle — and (b) kotlin/clr, and assert stdout matches.
# This validates our codegen + stdlib mappings against real Kotlin semantics (not hand-written expecteds).
set -uo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Prefer the local JDK path, then $JAVA_HOME (CI), then `java` on PATH.
JAVA=/usr/lib/jvm/java-21-openjdk-amd64/bin/java
[[ -x "$JAVA" ]] || JAVA="${JAVA_HOME:+$JAVA_HOME/bin/java}"
[[ -x "$JAVA" ]] || JAVA="$(command -v java)"
STDLIBJ="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"
EMB="$(find "$HOME/.gradle" -name 'kotlin-compiler-embeddable-2.2.0.jar' | head -1)"
COR="$(find "$HOME/.gradle/caches" -name 'kotlinx-coroutines-core-jvm-*.jar' | head -1)"
REFLECT="$(find "$HOME/.gradle/caches" -name 'kotlin-reflect-*.jar' | head -1)"
SCRIPT="$(find "$HOME/.gradle/caches" -name 'kotlin-script-runtime-2.2.0.jar' | head -1)"
ANNOT="$(find "$HOME/.gradle/caches" -path '*org.jetbrains/annotations*' -name 'annotations-*.jar' | head -1)"
CCP="$EMB:$STDLIBJ:$COR:$REFLECT:$SCRIPT:$ANNOT"   # classpath to RUN the kotlin/jvm compiler

# E-2: the clr side runs through the SHIPPING IL backend (kotc -> bir2cir -> ilemit -> CIL), not C#, so this harness
# validates the actual shipping path against real Kotlin semantics. Build ilemit + bir2cir once.
dotnet build "$ROOT/toolchain/ilemit"  -c Release -o "$ROOT/build/ilemit-bin"  -v q --nologo >/dev/null 2>&1
dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null 2>&1

# Build the compiler launcher once (plain Java app) — per-sample compiles cost ~2s instead of ~9s for gradlew.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
LAUNCHER="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"

# The CLR stdlib (kotlin.*) is supplied to kotc via the FRONTEND JAR (scripts/build-clr-stdlib-frontend.sh) on the clr
# side's -classpath, REPLACING the JVM kotlin-stdlib.jar (the JVM oracle below keeps the JVM jar — it IS the oracle).
# bir2cir then reads the REFERENCE assembly (DotKt.Private.Stdlib.dll) for the @Clr labels, and ilemit references the
# RUNTIME assembly (DotKt.Stdlib.dll) so a stdlib op resolves to its real Kotlin body — exactly the canonical ref/rt
# stdlib that dotkt.sh / verify-il use (NOT the stale build-dotkt-stdlib.sh). The banned facadegen --scan-asm of the
# stdlib is GONE: kotlin.* comes from the jar, never a facadegen reconstruction.
FE_JAR="$ROOT/build/clr-stdlib-frontend-jvm/kotlin-stdlib-clr-frontend.jar"
STDLIB_REF_DLL="$ROOT/build/clr-stdlib/dll/DotKt.Private.Stdlib.dll"
STDLIB_DLL="$ROOT/build/clr-stdlib-rt/dll/DotKt.Stdlib.dll"
[[ -f "$FE_JAR" ]]         || bash "$ROOT/scripts/build-clr-stdlib-frontend.sh" >/dev/null 2>&1
[[ -f "$STDLIB_REF_DLL" ]] || bash "$ROOT/scripts/build-clr-stdlib.sh" --emit >/dev/null 2>&1
[[ -f "$STDLIB_DLL" ]]     || bash "$ROOT/scripts/build-clr-stdlib-runtime.sh" --emit >/dev/null 2>&1

# Pure-Kotlin samples only (no @Clr / injected .NET types — those can't run on the JVM).
PURE="m0 m-a1 m-a2 m-a3 m-a4 m-a5 m-a6 m-a7 m-a8 m-b1 m-b2 m-b3 m-b4 m-b5 m-b6 m-b7 m-b8 m-b9 m-b10 m-b11 m-b12 m-b13 m-s1 m-s2 m-s3 il-seq il-char il-sort il-funref il-getclass il-localdeleg il-langfeat il-mapdes il-ctorref il-collmore il-tryexpr il-localclass il-collops2 il-refcell il-annot il-props il-mixnum il-arrops"
fail=0

# Run samples concurrently (each does a JVM oracle compile+run plus a CLR compile+run — all independent).
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 6 )) && JOBS=6
gate() { while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n 2>/dev/null || true; done; }
rm -f /tmp/diff-fail-* 2>/dev/null || true
# Kotlin.NET primitive formatting is CLR-native by design; normalize platform-cosmetic differences
# (boolean case true/True, double trailing `.0`) so the harness validates LOGIC, not host formatting.
norm() { sed -E 's/\bTrue\b/true/g; s/\bFalse\b/false/g; s/([0-9])\.0\b/\1/g'; }

for s in $PURE; do
	gate
	{ src="$ROOT/cases/$s"
	  mainfile="$(grep -lE '^fun main' "$src"/*.kt 2>/dev/null | head -1)"
	  if [[ -z "$mainfile" ]]; then echo "SKIP  $s (no main)"; exit 0; fi
	  base="$(basename "$mainfile" .kt)"; mainclass="${base^}Kt"
	  # (a) kotlin/jvm oracle
	  jout="/tmp/diff-jvm-$s"; rm -rf "$jout"; mkdir -p "$jout"
	  "$JAVA" -cp "$CCP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler "$src"/*.kt -no-stdlib -classpath "$STDLIBJ" -d "$jout" >/dev/null 2>&1
	  jvm="$("$JAVA" -cp "$jout:$STDLIBJ" "$mainclass" 2>/dev/null)"
	  # (b) kotlin/clr via the SHIPPING IL backend: kotc (frontend jar) -> BIR -> bir2cir -> CIR -> ilemit -> dll, run.
	  cout="$ROOT/build/diff-clr-$s"; rm -rf "$cout"; mkdir -p "$cout"
	  ccir="$ROOT/build/diff-cir-$s"; rm -rf "$ccir"; mkdir -p "$ccir"
	  "$LAUNCHER" $src -no-stdlib -classpath "$FE_JAR" -d $cout >/dev/null 2>&1
	  refarg=(); [[ -f "$STDLIB_REF_DLL" ]] && refarg=(--ref "$STDLIB_REF_DLL")
	  dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$ccir" "${refarg[@]}" "$cout"/*.bir.json >/dev/null 2>&1
	  dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$cout" "$mainclass" --ref "$STDLIB_DLL" "$ccir"/*.cir.json >/dev/null 2>&1
	  cp "$STDLIB_DLL" "$cout/"
	  clr="$(dotnet "$cout/$mainclass.dll" 2>/dev/null)"
	  if [[ "$(norm <<<"$jvm")" == "$(norm <<<"$clr")" ]]; then echo "MATCH $s"; else
		echo "DIFF  $s"; echo "--- jvm ---"; echo "$jvm"; echo "--- clr ---"; echo "$clr"; touch "/tmp/diff-fail-$s"; fi
	} &
done
wait
for f in /tmp/diff-fail-*; do [[ -e "$f" ]] && fail=1; done

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL MATCH (clr == kotlin/jvm)" || { echo "SOME DIFFER"; exit 1; }
