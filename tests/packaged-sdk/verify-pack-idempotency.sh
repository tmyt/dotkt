#!/usr/bin/env bash
# Regression gate for #223. The caller has just completed one successful pack, so every baked artifact and
# sidecar must be current. Prove both halves of the contract:
#   1. the content fingerprint ignores metadata-only touches but changes for real content/path changes;
#   2. a second unchanged standalone pack does not rebuild the frontend KLIB or either stdlib DLL.
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

info "PASS: content changes invalidate stamps; metadata-only changes and a second unchanged pack do not"
