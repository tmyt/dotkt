#!/usr/bin/env bash
# dotkt — compile Kotlin (.kt) to a .NET assembly with the DotKt toolchain (kotc -> BIR -> bir2cir ->
# CIR -> ilemit -> CIL), from the command line. A thin dev wrapper over the same pipeline the MSBuild
# targets / verify scripts drive, for quick one-shot builds (handy while iterating on the stdlib or
# trying a snippet). Every resolved reference DLL is projected to a standard metadata-only KLIB before
# kotc starts, so declarations are available independent of the source import set. Inputs: .kt files/dirs
# (+ the cached toolchain artifacts, built on demand). Output: <name>.dll in -d <dir>.
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
  --target-rid <rid>  select copy-local RID assets for this TARGET runtime (default: host RID)
  -h, --help      this help
EOF
}

# --- args -------------------------------------------------------------------------------------------
out_name=""; out_dir="$PWD/dotkt-out"; make_exe=0; do_run=0; use_stdlib=1; target_rid=""
declare -a srcs=() extra_refs=()
while (( $# )); do
	case "$1" in
		-o) out_name="$2"; shift 2 ;;
		-d) out_dir="$2"; shift 2 ;;
		--exe) make_exe=1; shift ;;
		--run) do_run=1; make_exe=1; shift ;;
		--ref) extra_refs+=("$2"); shift 2 ;;
		--no-stdlib) use_stdlib=0; shift ;;
		--target-rid) target_rid="$2"; shift 2 ;;
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
need_kotc; need_tool ilemit; need_tool bir2cir; need_tool dll2klib
# kotc -classpath: the CLR FRONTEND klib built FROM our CLR stdlib sources (scripts/build-stdlib-klib.sh).
# kotlin.* resolves from THIS klib (full Kotlin semantics).
need_fe_klib
# The CLR stdlib ref/rt assemblies are the canonical CACHED builds (scripts/build-stdlib-{ref,rt}.sh
# --emit). Do NOT auto-rebuild them here: the runtime emit is the slow, blocker-prone path; a cached
# green pair is what we want.
if (( use_stdlib )); then
	[[ -f "$STDLIB_REF_DLL" ]] || die "missing $STDLIB_REF_DLL — build it with: scripts/build-stdlib-ref.sh --emit (or pass --no-stdlib)"
	[[ -f "$STDLIB_RT_DLL" ]]  || die "missing $STDLIB_RT_DLL — build it with: scripts/build-stdlib-rt.sh --emit (or pass --no-stdlib)"
fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
bir="$work/bir"; cir="$work/cir"; klibs="$work/reference-klibs"
mkdir -p "$bir" "$cir" "$klibs" "$out_dir"

# Reference assemblies. Mirroring verify-tests, the two backend stages take DIFFERENT stdlib refs: bir2cir
# reads the @Clr-metadata REFERENCE stdlib (for @ClrTypeAlias/@ClrIntrinsic substitution), ilemit gets
# the RUNTIME stdlib (the real Kotlin bodies). CIR carries the per-assembly [Kotlin*] round-trip attributes and
# ilemit stamps them mechanically (no DotKt.Runtime). The targeting pack is the sole compile universe for dll2klib,
# bir2cir, and ilemit; runtime refs only disambiguate/copy implementation assets.
need_dotnet_reference_sets
extra_refset="$(refset_join "${extra_refs[@]}")"
bir_compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$extra_refset")"
runtime_refs="$extra_refset"
emit_compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$extra_refset")"
if (( use_stdlib )); then
	bir_compile_refs="$(refset_join "$bir_compile_refs" "$STDLIB_REF_DLL")"
	runtime_refs="$(refset_join "$runtime_refs" "$STDLIB_RT_DLL")"
	emit_compile_refs="$(refset_join "$emit_compile_refs" "$STDLIB_RT_DLL")"
fi

# 1. Project every resolved CLR reference to one standard KLIB. DotKt.Stdlib is deliberately represented by
#    the authoritative frontend KLIB instead of projecting its physical runtime/reference assembly.
rsp="$work/references.rsp"
printf '%s\n' "${FRAMEWORK_COMPILE_REF_PATHS[@]}" "${extra_refs[@]}" > "$rsp"
jobs="${DOTKT_DLL2KLIB_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '1')}"
dotnet "$DLL2KLIB_DLL" --out "$klibs" --jobs "$jobs" @"$rsp" >/dev/null
case "${OS:-}" in
	Windows_NT) klib_cp_sep=';' ;;
	*) klib_cp_sep=':' ;;
esac
cp="$FE_KLIB"
while IFS= read -r klib; do cp+="${cp:+$klib_cp_sep}$klib"; done < <(find "$klibs" -maxdepth 1 -type f -name '*.klib' | LC_ALL=C sort)

# 2. kotc: .kt -> BIR.
info "compiling ${#kts[@]} file(s) -> BIR" >&2
"$KOTC" "${kts[@]}" -no-stdlib -classpath "$cp" -d "$bir"

# 3. bir2cir: BIR -> CIR (the single type-lowering path; mode is env-gated, not a flag). Reads the
#    @Clr-metadata REFERENCE stdlib for the @ClrTypeAlias/@ClrIntrinsic substitution.
info "lowering BIR -> CIR" >&2
dotnet "$BIR2CIR_DLL" "$cir" --compile-refs "$bir_compile_refs" "$bir"/*.bir.json >/dev/null

# 4. ilemit: CIR -> CIL. Gets the RUNTIME stdlib (real Kotlin bodies); [Kotlin*] attrs synthesized per-assembly.
#    --target-rid (#51): when cross-targeting, pick the target runtime's runtimes/<rid>/lib copy-local asset instead of
#    the build host's; empty (the default) makes ilemit fall back to the host RID. The SDK RID graph is auto-discovered.
info "emitting $out_name.dll" >&2
dotnet "$ILEMIT_DLL" "$out_dir" "$out_name" --compile-refs "$emit_compile_refs" --runtime-refs "$runtime_refs" \
	--target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" --target-rid "$target_rid" "$cir"/*.cir.json

# 5. exe scaffolding: copy copy-local refs + write a runtimeconfig from this driver's explicit target settings.
if (( make_exe )); then
	(( use_stdlib )) && cp "$STDLIB_RT_DLL" "$out_dir/" 2>/dev/null || true
	for r in "${extra_refs[@]}"; do cp "$r" "$out_dir/" 2>/dev/null || true; done
	write_runtimeconfig "$out_dir" "$out_name"
fi

info "built $out_dir/$out_name.dll"
if (( do_run )); then echo "----"; ( cd "$out_dir" && dotnet "$out_name.dll" ); fi
