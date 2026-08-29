#!/usr/bin/env bash
# Compare equal total TypeDef work in one assembly and many assemblies. Absolute timings vary by host, but starting a
# fresh managed process and reparsing the complete catalog per DLL makes the split batch an order of magnitude slower.
# The in-process bounded scheduler should keep the remaining per-KLIB ZIP/package overhead within a modest ratio.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-batch-overhead-benchmark
source "$ROOT/scripts/lib.sh"

BATCH_COUNT="${BATCH_COUNT:-32}"
TYPES_PER_ASSEMBLY="${TYPES_PER_ASSEMBLY:-16}"
[[ "$BATCH_COUNT" =~ ^[1-9][0-9]*$ ]] || die "BATCH_COUNT must be a positive integer"
[[ "$TYPES_PER_ASSEMBLY" =~ ^[1-9][0-9]*$ ]] || die "TYPES_PER_ASSEMBLY must be a positive integer"

OUT="$ROOT/build/dll2klib-batch-overhead-benchmark"
rm -rf "$OUT"
mkdir -p "$OUT/tools" "$OUT/generator" "$OUT/input" "$OUT/single-klib" "$OUT/batch-klib"

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-local-index-benchmark/Generator.csproj" \
	-c Release -o "$OUT/generator" -v:q --nologo

total_types="$(( BATCH_COUNT * TYPES_PER_ASSEMBLY ))"
single="$OUT/input/Single${total_types}.dll"
dotnet "$OUT/generator/Generator.dll" "$single" "$total_types" "$total_types"
printf '%s\n' "$single" > "$OUT/single.rsp"

: > "$OUT/batch.rsp"
for ((i = 0; i < BATCH_COUNT; i++)); do
	input="$OUT/input/Batch$(printf '%03d' "$i").dll"
	dotnet "$OUT/generator/Generator.dll" "$input" "$TYPES_PER_ASSEMBLY" "$TYPES_PER_ASSEMBLY"
	printf '%s\n' "$input" >> "$OUT/batch.rsp"
done

measure() {
	local output="$1" rsp="$2" start_ns end_ns
	start_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	dotnet "$OUT/tools/dll2klib.dll" --out "$output" --jobs 1 @"$rsp" >/dev/null
	end_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	printf '%s\n' "$(( (end_ns - start_ns) / 1000000 ))"
}

single_ms="$(measure "$OUT/single-klib" "$OUT/single.rsp")"
batch_ms="$(measure "$OUT/batch-klib" "$OUT/batch.rsp")"
batch_outputs="$(find "$OUT/batch-klib" -maxdepth 1 -type f -name '*.klib' | wc -l)"
[[ "$batch_outputs" -eq "$BATCH_COUNT" ]] \
	|| die "batch projection produced $batch_outputs KLIBs, expected $BATCH_COUNT"
(( single_ms > 0 )) || die "single projection completed too quickly to measure"
ratio_hundredths="$(( batch_ms * 100 / single_ms ))"
(( ratio_hundredths < 600 )) \
	|| die "dll2klib per-assembly batch overhead regressed: "\
"one/${total_types}=${single_ms} ms, ${BATCH_COUNT}/${TYPES_PER_ASSEMBLY}=${batch_ms} ms (${ratio_hundredths}%)"

info "PASS  dll2klib split-batch overhead stays below 6x for equal TypeDef work: "\
"one/${total_types}=${single_ms} ms, ${BATCH_COUNT}/${TYPES_PER_ASSEMBLY}=${batch_ms} ms (${ratio_hundredths}%)"
