#!/usr/bin/env bash
# Declaration facts must not acquire the enclosing adapter's lifted generic substitutions.
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"
need_kotc
need_fe_klib
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
"$KOTC" "$ROOT/tests/roundtrip/frontend/EnumCallableFrame.kt" -classpath "$FE_KLIB" -d "$work"
jq -e '
    [.. | objects | select(.k? == "callInstance" and .method? == "compareTo"
      and .ownerType.name? == "kotlin.Enum") | .overrides[]
      | select(.owner.name == "kotlin.Comparable") | .owner.args[0]]
    | length == 2 and all(.[]; . == {t:"tv",scope:"type",i:0})
' "$work/EnumCallableFrame.bir.json" >/dev/null
printf 'Enum callable-reference override facts retain the selected declaration frame\n'
