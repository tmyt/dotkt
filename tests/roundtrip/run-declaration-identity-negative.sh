#!/usr/bin/env bash
# A compiler-produced cross-module call with a trusted declaration identity must never fall back to a sibling whose
# signature happens to match. Corrupt the current BIR signature while retaining the selected identity and require
# bir2cir to reject that exact declaration/call-site pair with source context.
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)/scripts/lib.sh"

need_tool bir2cir
need_dotnet_reference_sets

consumer_obj="$ROOT/tests/roundtrip/consumer/obj/Debug/net10.0"
consumer_bin="$ROOT/tests/roundtrip/consumer/bin/Debug/net10.0"
source_bir="$consumer_obj/bir/RoundtripSurfaceTests.bir.json"
[[ -f "$source_bir" ]] || die "roundtrip consumer BIR is missing: $source_bir"
[[ -d "$consumer_bin" ]] || die "roundtrip consumer output is missing: $consumer_bin"

selected_id="$(jq -r '
  first(.. | objects |
    select(.method? == "overloadedReceiver" and .declarationId? != null and .sig[0].name? == "kotlin.String")) |
  .declarationId // empty
' "$source_bir")"
[[ -n "$selected_id" ]] || die "selected overloadedReceiver declaration identity is absent"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bir" "$work/cir"

# Change the selected String overload's semantic signature to the sibling's `() -> String` first slot. A resolver
# that searches again by name/signature would silently select that sibling; the identity-authoritative resolver must
# validate the selected MethodDef and reject the malformed current BIR instead.
jq --arg id "$selected_id" '
  (.types[] | select(.name == "RoundtripSurfaceTests") | .methods[] |
    select(.name == "receiverFunctionOverloadsRetainTheirSelectedCrossModuleMember")) as $target |
  ([$target.body[] | .. | objects | .method? |
    select(type == "string" and startswith("dotkt:lambda:"))] | unique) as $lambdas |
  .methods |= map(select(.name as $name | $lambdas | index($name))) |
  .types |= map(select(.name == "RoundtripSurfaceTests")) |
  .types[0].methods = [$target] |
  walk(
    if type == "object" and .declarationId? == $id and .method? == "overloadedReceiver" then
      .sig[0] = {
        "t": "fn",
        "suspend": false,
        "ret": { "t": "fqn", "name": "kotlin.String" },
        "params": []
      }
    else . end
  )
' "$source_bir" > "$work/bir/RoundtripSurfaceTests.bir.json"

mapfile -t extra_refs < <(find "$consumer_bin" -maxdepth 1 -type f -name '*.dll' \
  ! -name 'RoundtripConsumer.Tests.dll' ! -name 'DotKt.Stdlib.dll' | LC_ALL=C sort)
refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "${extra_refs[@]}")"

if log="$(dotnet "$BIR2CIR_DLL" "$work/cir" --compile-refs "$refs" \
    "$work/bir/RoundtripSurfaceTests.bir.json" 2>&1)"; then
  die "bir2cir accepted a call whose selected declaration identity disagrees with its signature"
fi
for expected in \
  "frontend declaration identity '$selected_id'" \
  "RoundtripSurfaceTests.kt" \
  "receiverFunctionOverloadsRetainTheirSelectedCrossModuleMember" \
  "validates the call signature"
do
  [[ "$log" == *"$expected"* ]] || die "identity refusal is missing '$expected': $log"
done

echo "declaration identity mismatch rejected with exact identity and call-site context"
