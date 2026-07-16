#!/usr/bin/env bash
# dotkt — compile Kotlin (.kt) to a .NET assembly with the DotKt toolchain (kotc -> BIR -> bir2cir ->
# CIR -> ilemit -> CIL), from the command line. A thin dev wrapper over the same pipeline the MSBuild
# targets / verify scripts drive, for quick one-shot builds (handy while iterating on the stdlib or
# trying a snippet). `import System.X` in the sources is resolved automatically (the kotc PSI import
# scan + facadegen, the same C-2 path the .ktproj uses) — no facade boilerplate needed. Inputs: .kt
# files/dirs (+ the cached toolchain artifacts, built on demand). Output: <name>.dll in -d <dir>.
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME [options] <file.kt | dir>...

Options:
  -o <name>       output assembly name           (default: derived from the first source, else 'app')
  -d <dir>        output directory               (default: ./dotkt-out)
  --exe           produce a runnable assembly    (writes <name>.runtimeconfig.json; implied by --run)
  --run           build, then run it             (implies --exe)
  --ref <dll>     add a compile/emit reference   (repeatable; e.g. a NuGet/BCL dll or another DotKt assembly)
  --no-stdlib     do NOT reference DotKt.Stdlib
  --retarget      repoint BCL refs off System.Private.CoreLib (so a C# project can <Reference> the output)
  -h, --help      this help
EOF
}

# --- args -------------------------------------------------------------------------------------------
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
		-h|--help) usage; exit 0 ;;
		-*) usage_error "unknown option '$1'" ;;
		*) srcs+=("$1"); shift ;;
	esac
done
(( ${#srcs[@]} )) || usage_error "no .kt sources given"

# Expand directories to their .kt files; collect the flat source list.
declare -a kts=()
for s in "${srcs[@]}"; do
	if [[ -d "$s" ]]; then while IFS= read -r f; do kts+=("$f"); done < <(find "$s" -name '*.kt'); else kts+=("$s"); fi
done
(( ${#kts[@]} )) || die "no .kt files found in: ${srcs[*]}"
[[ -n "$out_name" ]] || { base="$(basename "${kts[0]}" .kt)"; out_name="${base^}"; [[ "$out_name" =~ Kt$ ]] || out_name="${out_name}Kt"; }

# --- bootstrap the toolchain if missing -------------------------------------------------------------
need_kotc; need_tool ilemit; need_tool bir2cir; need_tool facadegen
# kotc -classpath: the CLR FRONTEND klib built FROM our CLR stdlib sources (scripts/build-stdlib-klib.sh).
# kotlin.* resolves from THIS klib (full Kotlin semantics), never from facadegen — the
# binding verify-il invariant.
need_fe_klib
# The CLR stdlib ref/rt assemblies are the canonical CACHED builds (scripts/build-stdlib-{ref,rt}.sh
# --emit). Do NOT auto-rebuild them here: the runtime emit is the slow, blocker-prone path; a cached
# green pair is what we want.
if (( use_stdlib )); then
	[[ -f "$STDLIB_REF_DLL" ]] || die "missing $STDLIB_REF_DLL — build it with: scripts/build-stdlib-ref.sh --emit (or pass --no-stdlib)"
	[[ -f "$STDLIB_RT_DLL" ]]  || die "missing $STDLIB_RT_DLL — build it with: scripts/build-stdlib-rt.sh --emit (or pass --no-stdlib)"
fi
(( do_retarget )) && need_tool retarget

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
bir="$work/bir"; cir="$work/cir"; mkdir -p "$bir" "$cir" "$out_dir"
cp="$FE_KLIB"

# Reference assemblies. Mirroring verify-il, the two backend stages take DIFFERENT stdlib refs: bir2cir
# reads the @Clr-metadata REFERENCE stdlib (for @ClrTypeAlias/@ClrIntrinsic substitution), ilemit gets
# the RUNTIME stdlib (the real Kotlin bodies). The [Kotlin*] round-trip attributes are SYNTHESIZED
# per-assembly by ilemit (no DotKt.Runtime). The targeting pack is the compile universe for facadegen,
# bir2cir, and retarget; ilemit resolves platform types from the runtime host and receives only implementation refs.
need_dotnet_reference_sets
extra_refset="$(refset_join "${extra_refs[@]}")"
bir_compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$extra_refset")"
facade_compile_refs="$bir_compile_refs"
runtime_refs="$extra_refset"
retarget_compile_refs="$bir_compile_refs"
if (( use_stdlib )); then
	bir_compile_refs="$(refset_join "$bir_compile_refs" "$STDLIB_REF_DLL")"
	facade_compile_refs="$(refset_join "$facade_compile_refs" "$STDLIB_RT_DLL")"
	runtime_refs="$(refset_join "$runtime_refs" "$STDLIB_RT_DLL")"
	retarget_compile_refs="$(refset_join "$retarget_compile_refs" "$STDLIB_RT_DLL")"
fi

# 1. .NET type injection: scan the sources' .NET imports (PSI) -> facadegen generates ONLY .NET-space facades.
#    kotlin.* (the WHOLE stdlib) is supplied to kotc via the KLIB (-classpath), which carries full Kotlin semantics
#    (inline/reified/operator/...). facadegen must NEVER inject kotlin.* -- it cannot restore those semantics, and a
#    facadegen-produced kotlin.* symbol collides with the klib's (e.g. non-reified vs reified arrayOf -> ambiguity).
meta="$work/clrtypes.meta"; implist="$work/imports.txt"
"$KOTC" --scan-imports --output "$implist" "${kts[@]}" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$meta" --compile-refs "$facade_compile_refs" --import-list "$implist" >/dev/null 2>&1 || true

# 2. kotc: .kt -> BIR.
info "compiling ${#kts[@]} file(s) -> BIR" >&2
CLR_TYPES_METADATA="$meta" "$KOTC" "${kts[@]}" -no-stdlib -classpath "$cp" -d "$bir"

# 3. bir2cir: BIR -> CIR (the single type-lowering path; mode is env-gated, not a flag). Reads the
#    @Clr-metadata REFERENCE stdlib for the @ClrTypeAlias/@ClrIntrinsic substitution.
info "lowering BIR -> CIR" >&2
dotnet "$BIR2CIR_DLL" "$cir" --compile-refs "$bir_compile_refs" "$bir"/*.bir.json >/dev/null

# 4. ilemit: CIR -> CIL. Gets the RUNTIME stdlib (real Kotlin bodies); [Kotlin*] attrs synthesized per-assembly.
info "emitting $out_name.dll" >&2
dotnet "$ILEMIT_DLL" "$out_dir" "$out_name" --runtime-refs "$runtime_refs" "$cir"/*.cir.json

# 5. optional retarget (for compile-time C# <Reference>).
(( do_retarget )) && dotnet "$RETARGET_DLL" "$out_dir/$out_name.dll" --compile-refs "$retarget_compile_refs" >/dev/null

# 6. exe scaffolding: copy copy-local refs + write a runtimeconfig so `dotnet <name>.dll` runs.
if (( make_exe )); then
	(( use_stdlib )) && cp "$STDLIB_RT_DLL" "$out_dir/" 2>/dev/null || true
	for r in "${extra_refs[@]}"; do cp "$r" "$out_dir/" 2>/dev/null || true; done
	cat > "$out_dir/$out_name.runtimeconfig.json" <<JSON
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
JSON
fi

info "built $out_dir/$out_name.dll"
if (( do_run )); then echo "----"; ( cd "$out_dir" && dotnet "$out_name.dll" ); fi
