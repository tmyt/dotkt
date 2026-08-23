#!/usr/bin/env bash
# Repeatable local benchmark for AssemblyScanner's local TypeDef resolution. Every projected class exercises the
# enumerable-pattern self lookup, so the former whole-TypeDef-table scan grows quadratically with TYPE_COUNT.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-local-index-benchmark
source "$ROOT/scripts/lib.sh"

TYPE_COUNT="${TYPE_COUNT:-4000}"
[[ "$TYPE_COUNT" =~ ^[1-9][0-9]*$ ]] || die "TYPE_COUNT must be a positive integer"

OUT="$ROOT/build/dll2klib-local-index-benchmark"
rm -rf "$OUT"
mkdir -p "$OUT/tools" "$OUT/input" "$OUT/klib"

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-local-index-benchmark/Generator.csproj" \
	-c Release -o "$OUT/generator" -v:q --nologo

input="$OUT/input/Synthetic${TYPE_COUNT}.dll"
dotnet "$OUT/generator/Generator.dll" "$input" "$TYPE_COUNT"
printf '%s\n' "$input" > "$OUT/references.rsp"

start_ns="$(date +%s%N)"
projection_output="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 1 @"$OUT/references.rsp")"
end_ns="$(date +%s%N)"
elapsed_ms="$(( (end_ns - start_ns) / 1000000 ))"

grep -q "Synthetic${TYPE_COUNT}.dll -> Synthetic${TYPE_COUNT}.klib: ${TYPE_COUNT} public class(es)" \
	<<<"$projection_output" || die "synthetic projection did not report all $TYPE_COUNT public classes"
[[ -s "$OUT/klib/Synthetic${TYPE_COUNT}.klib" ]] || die "synthetic projection did not produce a KLIB"

info "PASS  projected $TYPE_COUNT local TypeDefs in ${elapsed_ms} ms"
