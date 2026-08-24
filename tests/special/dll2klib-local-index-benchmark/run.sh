#!/usr/bin/env bash
# Repeatable local benchmark for AssemblyScanner's assembly indexes. Every projected class exercises local TypeDef
# lookup, while every visible namespace creates one SignatureDecoder. Doubling both axes catches either a per-class
# local-definition scan or a per-namespace decoder seed scan: both turn a nominally linear projection superlinear.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-local-index-benchmark
source "$ROOT/scripts/lib.sh"

TYPE_COUNT="${TYPE_COUNT:-4000}"
NAMESPACE_COUNT="${NAMESPACE_COUNT:-$TYPE_COUNT}"
[[ "$TYPE_COUNT" =~ ^[1-9][0-9]*$ ]] || die "TYPE_COUNT must be a positive integer"
[[ "$NAMESPACE_COUNT" =~ ^[1-9][0-9]*$ ]] || die "NAMESPACE_COUNT must be a positive integer"
(( NAMESPACE_COUNT <= TYPE_COUNT )) || die "NAMESPACE_COUNT must not exceed TYPE_COUNT"

OUT="$ROOT/build/dll2klib-local-index-benchmark"
rm -rf "$OUT"
mkdir -p "$OUT/tools" "$OUT/input"

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-local-index-benchmark/Generator.csproj" \
	-c Release -o "$OUT/generator" -v:q --nologo

measure_projection() {
	local count="$1" namespaces="$2" stem="Synthetic${1}N${2}"
	local input="$OUT/input/${stem}.dll" klib="$OUT/klib-${stem}"
	local rsp="$OUT/references-${stem}.rsp" start_ns end_ns elapsed_ms projection_output
	mkdir -p "$klib"
	dotnet "$OUT/generator/Generator.dll" "$input" "$count" "$namespaces"
	printf '%s\n' "$input" > "$rsp"
	start_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	projection_output="$(dotnet "$OUT/tools/dll2klib.dll" --out "$klib" --jobs 1 @"$rsp")"
	end_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	elapsed_ms="$(( (end_ns - start_ns) / 1000000 ))"

	grep -q "${stem}.dll -> ${stem}.klib: ${count} public class(es)" \
		<<<"$projection_output" || die "synthetic projection did not report all $count public classes"
	[[ -s "$klib/${stem}.klib" ]] || die "synthetic projection did not produce a KLIB"
	printf '%s\n' "$elapsed_ms"
}

double_count="$(( TYPE_COUNT * 2 ))"
double_namespaces="$(( NAMESPACE_COUNT * 2 ))"
base_ms="$(measure_projection "$TYPE_COUNT" "$NAMESPACE_COUNT")"
double_ms="$(measure_projection "$double_count" "$double_namespaces")"
(( base_ms > 0 )) || die "base projection completed too quickly to measure"
ratio_hundredths="$(( double_ms * 100 / base_ms ))"
(( ratio_hundredths < 250 )) \
	|| die "dll2klib assembly indexing regressed superlinearly: "\
"${TYPE_COUNT}/${NAMESPACE_COUNT}=${base_ms} ms, ${double_count}/${double_namespaces}=${double_ms} ms"

info "PASS  dll2klib assembly indexing scales below 2.5x: "\
"${TYPE_COUNT}/${NAMESPACE_COUNT}=${base_ms} ms, ${double_count}/${double_namespaces}=${double_ms} ms (${ratio_hundredths}%)"
