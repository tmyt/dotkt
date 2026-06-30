#!/usr/bin/env bash
# dotkt — compile Kotlin (.kt) to a .NET assembly with the DotKt toolchain (kotc -> BIR -> CIR -> ilemit -> CIL), from the
# command line. A thin dev wrapper over the same pipeline the MSBuild targets / verify scripts drive, for quick
# one-shot builds (handy while iterating on DotKt.Stdlib or trying a snippet).
#
#   scripts/dotkt.sh [options] <file.kt | dir>...
#
# Options:
#   -o <name>       output assembly name           (default: derived from the first source, else 'app')
#   -d <dir>        output directory               (default: ./dotkt-out)
#   --exe           produce a runnable assembly     (writes <name>.runtimeconfig.json; implied by --run)
#   --run           build, then run it              (implies --exe)
#   --ref <dll>     add a compile/emit reference    (repeatable; e.g. a NuGet/BCL dll or another DotKt assembly)
#   --no-stdlib     do NOT reference DotKt.Stdlib   (the migrated real-Kotlin collection ops)
#   --retarget      repoint BCL refs off System.Private.CoreLib (so a C# project can <Reference> the output)
#   -h | --help     this help
#
# .NET interop: `import System.X` in the sources is resolved automatically (the kotc PSI import scan + facadegen, the
# same C-2 path the .ktproj uses), so no facade boilerplate is needed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
KOTC="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
ILEMIT="$ROOT/build/ilemit-bin/ilemit.dll"
BIR2CIR="$ROOT/build/bir2cir-bin/bir2cir.dll"
FACADEGEN="$ROOT/build/facadegen-bin/facadegen.dll"
RETARGET="$ROOT/build/retarget-bin/retarget.dll"
DOTKT_RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"
# CLR stdlib (the canonical build under runtime/stdlib/, mirrored from scripts/verify-il.sh): the REFERENCE assembly
# (@Clr metadata) feeds bir2cir's @ClrTypeAlias/@ClrIntrinsic substitution; the RUNTIME assembly carries the real
# Kotlin bodies and is ilemit's --ref (and copy-local for the run phase).
STDLIB_REF="$ROOT/build/clr-stdlib/dll/DotKt.Private.Stdlib.dll"
STDLIB_RT="$ROOT/build/clr-stdlib-rt/dll/DotKt.Stdlib.dll"
# kotc -classpath: the CLR FRONTEND jar built FROM our CLR stdlib sources, REPLACING the JVM kotlin-stdlib.jar whose
# java.util.* typealiases leaked into the frontend (e.g. kotlin.Comparator = java.util.Comparator). kotlin.* resolves
# from THIS jar (full Kotlin semantics), never from facadegen --scan-asm. This is the binding verify-il invariant.
JAR="$ROOT/build/clr-stdlib-frontend-jvm/kotlin-stdlib-clr-frontend.jar"
CORO="$(find "$HOME/.gradle/caches" -name 'kotlinx-coroutines-core-jvm-*.jar' 2>/dev/null | head -1)"

# --- args ---------------------------------------------------------------------------------------------------------
out_name=""; out_dir="$PWD/dotkt-out"; make_exe=0; do_run=0; use_stdlib=1; do_retarget=0
declare -a srcs=() extra_refs=()
while (( $# )); do
	case "$1" in
		-o) out_name="$2"; shift 2 ;;
		-d) out_dir="$2"; shift 2 ;;
		--exe) make_exe=1; shift ;;
		--run) do_run=1; make_exe=1; shift ;;
		--ref) extra_refs+=("$2"); shift 2 ;;
		--no-stdlib) use_stdlib=0; shift ;;
		--retarget) do_retarget=1; shift ;;
		-h|--help) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
		-*) echo "dotkt: unknown option '$1'" >&2; exit 2 ;;
		*) srcs+=("$1"); shift ;;
	esac
done
(( ${#srcs[@]} )) || { echo "dotkt: no .kt sources given (try -h)" >&2; exit 2; }

# Expand directories to their .kt files; collect the flat source list.
declare -a kts=()
for s in "${srcs[@]}"; do
	if [[ -d "$s" ]]; then while IFS= read -r f; do kts+=("$f"); done < <(find "$s" -name '*.kt'); else kts+=("$s"); fi
done
(( ${#kts[@]} )) || { echo "dotkt: no .kt files found in: ${srcs[*]}" >&2; exit 2; }
[[ -n "$out_name" ]] || { base="$(basename "${kts[0]}" .kt)"; out_name="${base^}"; [[ "$out_name" =~ Kt$ ]] || out_name="${out_name}Kt"; }

# --- bootstrap the toolchain if missing --------------------------------------------------------------------------
[[ -x "$KOTC" ]] || { echo "dotkt: building kotc..." >&2; (cd "$ROOT" && ./gradlew -q :kotc:installDist); }
[[ -f "$ILEMIT" ]] || { echo "dotkt: building ilemit..." >&2; dotnet build "$ROOT/toolchain/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo; }
[[ -f "$BIR2CIR" ]] || { echo "dotkt: building bir2cir..." >&2; dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo; }
[[ -f "$FACADEGEN" ]] || { echo "dotkt: building facadegen..." >&2; dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo; }
[[ -f "$DOTKT_RT" ]] || { echo "dotkt: building DotKt.Runtime..." >&2; dotnet build "$ROOT/runtime/DotKt.Runtime" -c Release -o "$ROOT/build/dotkt-runtime" -v q --nologo; }
# The CLR frontend stdlib jar (kotc -classpath): build once if missing — exactly as verify-il bootstraps it.
[[ -f "$JAR" ]] || { echo "dotkt: building CLR frontend stdlib jar..." >&2; bash "$ROOT/scripts/build-clr-stdlib-frontend.sh" >/dev/null; }
# The CLR stdlib ref/rt assemblies are the canonical CACHED builds (scripts/build-clr-stdlib{,-runtime}.sh --emit). Do
# NOT auto-rebuild them here: the runtime emit is the slow, blocker-prone path; a cached green pair is what we want.
if (( use_stdlib )); then
	[[ -f "$STDLIB_REF" ]] || { echo "dotkt: missing $STDLIB_REF — build it with: scripts/build-clr-stdlib.sh --emit (or pass --no-stdlib)" >&2; exit 1; }
	[[ -f "$STDLIB_RT" ]]  || { echo "dotkt: missing $STDLIB_RT — build it with: scripts/build-clr-stdlib-runtime.sh --emit (or pass --no-stdlib)" >&2; exit 1; }
fi
if (( do_retarget )) && [[ ! -f "$RETARGET" ]]; then dotnet build "$ROOT/toolchain/retarget" -c Release -o "$ROOT/build/retarget-bin" -v q --nologo; fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
bir="$work/bir"; cir="$work/cir"; mkdir -p "$bir" "$cir" "$out_dir"
cp="$JAR"; [[ -n "$CORO" ]] && cp="$cp:$CORO"

# Reference assemblies. Mirroring verify-il, the two backend stages take DIFFERENT stdlib refs: bir2cir reads the
# @Clr-metadata REFERENCE stdlib (for @ClrTypeAlias/@ClrIntrinsic substitution), ilemit gets DotKt.Runtime (the
# [Kotlin*] attribute types + runtime helpers) plus the RUNTIME stdlib (the real Kotlin bodies). ilemit resolves the
# BCL itself by runtime reflection, so the ref-pack is only for facadegen's .NET-type resolution (and retarget).
refpack="$(dirname "$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -name 'System.Runtime.dll' -path '*net10.0*' 2>/dev/null | head -1)")"
declare -a bir2cir_refs=() ilemit_refs=(--ref "$DOTKT_RT")
refs_semi="$(ls "$refpack"/*.dll 2>/dev/null | tr '\n' ';')$DOTKT_RT"
(( use_stdlib )) && { bir2cir_refs+=(--ref "$STDLIB_REF"); ilemit_refs+=(--ref "$STDLIB_RT"); refs_semi="$refs_semi;$STDLIB_RT"; }
for r in "${extra_refs[@]}"; do bir2cir_refs+=(--ref "$r"); ilemit_refs+=(--ref "$r"); refs_semi="$refs_semi;$r"; done

# 1. .NET type injection: scan the sources' .NET imports (PSI) -> facadegen generates ONLY .NET-space facades.
#    kotlin.* (the WHOLE stdlib) is supplied to kotc via the JAR (-classpath), which carries full Kotlin semantics
#    (inline/reified/operator/...). facadegen must NEVER inject kotlin.* -- it cannot restore those semantics, and a
#    facadegen-produced kotlin.* symbol collides with the jar's (e.g. non-reified vs reified arrayOf -> ambiguity).
meta="$work/clrtypes.meta"; implist="$work/imports.txt"
"$KOTC" --scan-imports --output "$implist" "${kts[@]}" >/dev/null 2>&1 || true
dotnet "$FACADEGEN" --meta "$meta" --refs "$refs_semi" --import-list "$implist" >/dev/null 2>&1 || true

# 2. kotc: .kt -> BIR.
echo "dotkt: compiling ${#kts[@]} file(s) -> BIR" >&2
CLR_TYPES_METADATA="$meta" "$KOTC" "${kts[@]}" -no-stdlib -classpath "$cp" -d "$bir"

# 3. bir2cir: BIR -> CIR (the single type-lowering path; mode is env-gated, not a flag). Reads the @Clr-metadata
#    REFERENCE stdlib for the @ClrTypeAlias/@ClrIntrinsic substitution.
echo "dotkt: lowering BIR -> CIR" >&2
dotnet "$BIR2CIR" "$cir" "${bir2cir_refs[@]}" "$bir"/*.bir.json >/dev/null

# 4. ilemit: CIR -> CIL. Gets DotKt.Runtime + the RUNTIME stdlib (real Kotlin bodies).
echo "dotkt: emitting $out_name.dll" >&2
dotnet "$ILEMIT" "$out_dir" "$out_name" "${ilemit_refs[@]}" "$cir"/*.cir.json

# 5. optional retarget (for compile-time C# <Reference>).
(( do_retarget )) && dotnet "$RETARGET" "$out_dir/$out_name.dll" --refs "$refs_semi" >/dev/null

# 6. exe scaffolding: copy copy-local refs + write a runtimeconfig so `dotnet <name>.dll` runs.
if (( make_exe )); then
	cp "$DOTKT_RT" "$out_dir/" 2>/dev/null || true
	(( use_stdlib )) && cp "$STDLIB_RT" "$out_dir/" 2>/dev/null || true
	for r in "${extra_refs[@]}"; do cp "$r" "$out_dir/" 2>/dev/null || true; done
	cat > "$out_dir/$out_name.runtimeconfig.json" <<JSON
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
JSON
fi

echo "dotkt: built $out_dir/$out_name.dll"
if (( do_run )); then echo "----"; ( cd "$out_dir" && dotnet "$out_name.dll" ); fi
