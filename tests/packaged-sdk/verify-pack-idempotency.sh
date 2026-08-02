#!/usr/bin/env bash
# Regression gate for #223. The caller has just completed one successful pack, so every baked artifact and
# sidecar must be current. Prove both halves of the contract:
#   1. the content fingerprint ignores metadata-only touches but changes for real content/path changes;
#   2. a second unchanged standalone pack does not rebuild the frontend KLIB or either stdlib DLL;
#   3. Make's prerequisite path and standalone pack share the same stamp-aware builders.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT_NAME=pack-idempotency
source "$ROOT/scripts/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME
Checks content-fingerprint semantics and runs a second unchanged pack, which must reuse all baked stdlib artifacts.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Unit-level signal: same path+bytes after touch is stable; a same-size byte change and a path-set change are not.
mkdir -p "$work/inputs"
printf 'alpha\n' > "$work/inputs/tool.bin"
fp_initial="$(_toolstamp "$work/inputs")"
touch "$work/inputs/tool.bin"
fp_touched="$(_toolstamp "$work/inputs")"
[[ "$fp_touched" == "$fp_initial" ]] ||
	die "metadata-only touch changed the content fingerprint"

printf 'bravo\n' > "$work/inputs/tool.bin" # same byte count as alpha
fp_changed="$(_toolstamp "$work/inputs")"
[[ "$fp_changed" != "$fp_initial" ]] ||
	die "same-size content change did not invalidate the fingerprint"

printf 'extra\n' > "$work/inputs/second.bin"
fp_added="$(_toolstamp "$work/inputs")"
[[ "$fp_added" != "$fp_changed" ]] ||
	die "adding an input path did not invalidate the fingerprint"

artifact="$work/artifact"
printf 'artifact\n' > "$artifact"
printf '%s\n' "$fp_added" > "$artifact.toolstamp"
_stamp_fresh "$artifact" "$fp_added" ||
	die "matching artifact content stamp was not accepted"
if _stamp_fresh "$artifact" "$fp_changed"; then
	die "stale artifact content stamp was accepted"
fi

# Integration signal: the first pack (the caller) wrote fresh sidecars. A second pack may rebuild/re-copy the
# tools and reassemble nupkgs, but must not invoke any of the three expensive baked-artifact builders.
artifacts=("$FE_KLIB" "$STDLIB_REF_DLL" "$STDLIB_RT_DLL")
declare -a stamp_times=()
for a in "${artifacts[@]}"; do
	[[ -e "$a" ]] || die "first pack did not produce $a"
	[[ -f "$a.toolstamp" ]] || die "first pack did not produce $a.toolstamp"
	stamp_times+=("$(stat -c '%y' "$a.toolstamp")")
done

# The frontend KLIB is intentionally one standard ZIP archive. A directory here would make NuGet restore create
# hundreds of tiny metadata files again; a malformed archive could pass the existence/fingerprint checks above but
# would fail only later when kotc tried to resolve the stdlib.
[[ -f "$FE_KLIB" ]] || die "frontend stdlib KLIB is not a packed file: $FE_KLIB"
unzip -tqq "$FE_KLIB" || die "frontend stdlib KLIB is not a valid ZIP archive"
unzip -Z1 "$FE_KLIB" | grep -qx 'default/manifest' ||
	die "packed frontend stdlib KLIB has no default/manifest"

# Guard the actual shipping shape, not just the build artifact. The nupkg must contain exactly one frontend-KLIB
# entry and no tools/kotlin-stdlib-clr-frontend.klib/** tree for NuGet to expand into small files.
mapfile -t toolchain_packages < <(find "$ROOT/build/nuget-feed" -maxdepth 1 -type f -name 'DotKt.Toolchain.*.nupkg')
[[ ${#toolchain_packages[@]} -eq 1 ]] ||
	die "expected exactly one DotKt.Toolchain nupkg, found ${#toolchain_packages[@]}"
mapfile -t shipped_klib_entries < <(unzip -Z1 "${toolchain_packages[0]}" | grep '^tools/kotlin-stdlib-clr-frontend\.klib' || true)
[[ ${#shipped_klib_entries[@]} -eq 1 && "${shipped_klib_entries[0]}" == 'tools/kotlin-stdlib-clr-frontend.klib' ]] ||
	die "DotKt.Toolchain must ship the frontend stdlib as one packed KLIB (found: ${shipped_klib_entries[*]:-none})"

log="$work/second-pack.log"
if ! bash "$ROOT/scripts/pack-nuget.sh" >"$log" 2>&1; then
	tail -80 "$log" >&2
	die "second unchanged pack failed"
fi

if grep -E 'building CLR frontend stdlib klib|building stdlib (REFERENCE|RUNTIME) dll' "$log" >&2; then
	die "second unchanged pack rebuilt a fingerprinted stdlib artifact"
fi

for i in "${!artifacts[@]}"; do
	a="${artifacts[$i]}"
	after="$(stat -c '%y' "$a.toolstamp")"
	[[ "$after" == "${stamp_times[$i]}" ]] ||
		die "second unchanged pack rewrote $a.toolstamp"
done

# Exercise Make's stale-target recipes without changing any content. Before #223's fresh-Make follow-up those
# recipes invoked build-stdlib-*.sh directly (which deleted/no longer wrote the sidecars), then pack-nuget rebuilt
# the same three artifacts again. The unified recipes call need_* instead: the metadata-only touch is accepted by
# the content stamp, Make touches each target current, and the nested standalone pack also reuses it.
touch "$ROOT/scripts/lib.sh"
for i in "${!artifacts[@]}"; do
	stamp_times[$i]="$(stat -c '%y' "${artifacts[$i]}.toolstamp")"
done

make_log="$work/make-pack.log"
if ! make -C "$ROOT" pack >"$make_log" 2>&1; then
	tail -80 "$make_log" >&2
	die "Make pack path failed after metadata-only dependency touch"
fi

if grep -E 'building CLR frontend stdlib klib|building stdlib (REFERENCE|RUNTIME) dll|^bash scripts/build-stdlib-(klib|ref|rt)\.sh' "$make_log" >&2; then
	die "Make pack path rebuilt a content-stable stdlib artifact"
fi

for i in "${!artifacts[@]}"; do
	a="${artifacts[$i]}"
	after="$(stat -c '%y' "$a.toolstamp")"
	[[ "$after" == "${stamp_times[$i]}" ]] ||
		die "Make pack path rewrote $a.toolstamp after a metadata-only dependency touch"
done

info "PASS: content changes invalidate stamps; standalone and Make pack paths reuse content-stable artifacts"
