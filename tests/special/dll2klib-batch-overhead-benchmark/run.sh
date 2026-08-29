#!/usr/bin/env bash
# Compare equal total TypeDef work in one assembly and many assemblies. Absolute timings vary by host, but both cold
# conversion and a full warm-cache check should scale with metadata volume rather than repeatedly opening every input
# for each batch catalog.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-batch-overhead-benchmark
source "$ROOT/scripts/lib.sh"

BATCH_COUNT="${BATCH_COUNT:-512}"
TYPES_PER_ASSEMBLY="${TYPES_PER_ASSEMBLY:-1}"
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
dotnet "$OUT/generator/Generator.dll" --batch "$OUT/input" \
	"$BATCH_COUNT" "$TYPES_PER_ASSEMBLY" "$TYPES_PER_ASSEMBLY"
for ((i = 0; i < BATCH_COUNT; i++)); do
	input="$OUT/input/Batch$(printf '%03d' "$i").dll"
	printf '%s\n' "$input" >> "$OUT/batch.rsp"
done

measure() {
	local output="$1" rsp="$2" log="${3:-/dev/null}" start_ns end_ns
	start_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	dotnet "$OUT/tools/dll2klib.dll" --out "$output" --jobs 1 @"$rsp" >"$log"
	end_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	printf '%s\n' "$(( (end_ns - start_ns) / 1000000 ))"
}

# Warm the managed host, tool assembly, input metadata, and generator outputs symmetrically before comparing fresh
# KLIB output directories. This keeps cold-cache order from making the single-assembly denominator artificially large.
measure "$OUT/warm-single-klib" "$OUT/single.rsp" >/dev/null
measure "$OUT/warm-batch-klib" "$OUT/batch.rsp" >/dev/null
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

warm_single_log="$OUT/warm-single.log"
warm_batch_log="$OUT/warm-batch.log"
warm_single_ms="$(measure "$OUT/single-klib" "$OUT/single.rsp" "$warm_single_log")"
warm_batch_ms="$(measure "$OUT/batch-klib" "$OUT/batch.rsp" "$warm_batch_log")"
grep -q '1 KLIB(s) up to date' "$warm_single_log" \
	|| die "single warm-cache check did not hit the KLIB cache"
grep -q "$BATCH_COUNT KLIB(s) up to date" "$warm_batch_log" \
	|| die "batch warm-cache check did not hit every KLIB cache entry"
(( warm_single_ms > 0 )) || die "single warm-cache check completed too quickly to measure"
warm_ratio_hundredths="$(( warm_batch_ms * 100 / warm_single_ms ))"
(( warm_ratio_hundredths < 170 )) \
	|| die "dll2klib split-batch warm-cache discovery regressed: "\
"one/${total_types}=${warm_single_ms} ms, ${BATCH_COUNT}/${TYPES_PER_ASSEMBLY}=${warm_batch_ms} ms "\
"(${warm_ratio_hundredths}%)"

info "PASS  dll2klib split-batch overhead stays below 6x for equal TypeDef work: "\
"cold one/${total_types}=${single_ms} ms, ${BATCH_COUNT}/${TYPES_PER_ASSEMBLY}=${batch_ms} ms "\
"(${ratio_hundredths}%); warm=${warm_single_ms}/${warm_batch_ms} ms (${warm_ratio_hundredths}%)"
