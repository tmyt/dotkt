#!/usr/bin/env bash
# The producer is a DLL, never frontend source or a friend module. Public inline execution is checked by NUnit;
# these isolated frontend consumers must reject direct access specifically for Kotlin internal visibility.
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"
[[ $# == 1 ]] || usage_error "expected the consumer's reference KLIB directory"
classpath="$FE_KLIB"
case "${OS:-}" in Windows_NT) separator=';' ;; *) separator=':' ;; esac
while IFS= read -r klib; do classpath+="$separator$klib"; done \
    < <(find "$1" -maxdepth 1 -type f -name '*.klib' | LC_ALL=C sort)
[[ -f "$1/RoundtripProducer.klib" ]] || die "missing projected producer KLIB"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for source in "$ROOT/tests/roundtrip/visibility/"*.kt; do
    name="$(basename "$source" .kt)"
    if "$KOTC" "$source" -no-stdlib -classpath "$classpath" -d "$work/$name" \
        >"$work/$name.log" 2>&1; then
        die "$name: source-internal declaration was accepted"
    fi
    if ! rg -qi "cannot access.*internal" "$work/$name.log"; then
        tail -20 "$work/$name.log"
        die "$name: failure did not establish internal visibility"
    fi
    echo "$name: direct access is source-internal"
done
