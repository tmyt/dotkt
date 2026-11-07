#!/usr/bin/env bash
# Exercise the real frontend configuration, without repeating CLR lowering/emission.
set -euo pipefail
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"
need_kotc
need_fe_klib
work="$(mktemp -d "$ROOT/build/klib-zip-cache.XXXXXX")"
trap 'rm -rf "$work"' EXIT
probe="$ROOT/tests/msbuild/klib-cache/Probe.kt"

for mode in default disabled bounded; do
    options=()
    case "$mode" in
        disabled) options=(-Xklib-zip-file-accessor-cache-limit=0) ;;
        bounded) options=(-Xklib-zip-file-accessor-cache-limit=1) ;;
    esac
    "$KOTC" "$probe" -classpath "$FE_KLIB" -d "$work/$mode" "${options[@]}" > "$work/$mode.log" 2>&1
done
for mode in disabled bounded; do
    if ! cmp -s "$work/default/Probe.bir.json" "$work/$mode/Probe.bir.json"; then
        echo "FAIL klib-zip-cache: $mode changed frontend output"
        exit 1
    fi
done

for limit in -1 invalid; do
    if "$KOTC" "$probe" -classpath "$FE_KLIB" -d "$work/bad-$limit" \
        "-Xklib-zip-file-accessor-cache-limit=$limit" > "$work/bad-$limit.log" 2>&1; then
        echo "FAIL klib-zip-cache: invalid cache limit '$limit' was ignored"
        exit 1
    fi
    if ! grep -Fiq 'cannot parse -Xklib-zip-file-accessor-cache-limit value:' "$work/bad-$limit.log" \
        || ! grep -Fq 'It must be an integer >= 0.' "$work/bad-$limit.log" \
        || ! grep -Fq 'kotc finished: COMPILATION_ERROR' "$work/bad-$limit.log"; then
        cat "$work/bad-$limit.log"
        echo "FAIL klib-zip-cache: expected upstream option diagnostic"
        exit 1
    fi
    if [[ -e "$work/bad-$limit/Probe.bir.json" ]]; then
        echo "FAIL klib-zip-cache: emitted BIR after configuration error"
        exit 1
    fi
done
echo 'PASS klib-zip-cache: default/bounded/disabled output equality and upstream option validation'
