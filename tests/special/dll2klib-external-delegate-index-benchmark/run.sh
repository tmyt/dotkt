#!/usr/bin/env bash
# Repeatable benchmark for external signature prerequisites. The external assembly supplies a large TypeDef table;
# every consumer namespace mentions its delegate directly and implements its interface, exercising both external
# delegate lookup and DecoderFor. Doubling both axes catches reopening or rescanning the external assembly once per
# package while retaining linear work; the shape checks keep package-local NameTable isolation observable.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-external-delegate-index-benchmark
source "$ROOT/scripts/lib.sh"

TYPE_COUNT="${TYPE_COUNT:-8000}"
NAMESPACE_COUNT="${NAMESPACE_COUNT:-1000}"
[[ "$TYPE_COUNT" =~ ^[1-9][0-9]*$ ]] || die "TYPE_COUNT must be a positive integer"
[[ "$NAMESPACE_COUNT" =~ ^[1-9][0-9]*$ ]] || die "NAMESPACE_COUNT must be a positive integer"

OUT="$ROOT/build/dll2klib-external-delegate-index-benchmark"
rm -rf "$OUT"
mkdir -p "$OUT/tools" "$OUT/input"

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-external-delegate-index-benchmark/Generator.csproj" \
	-c Release -o "$OUT/generator" -v:q --nologo

measure_projection() {
	local count="$1" namespaces="$2" stem="Synthetic${1}N${2}"
	local input="$OUT/input/$stem" klib="$OUT/klib-$stem" rsp="$OUT/references-$stem.rsp"
	local start_ns end_ns elapsed_ms projection_output last_namespace
	mkdir -p "$input" "$klib"
	dotnet "$OUT/generator/Generator.dll" "$input" "$count" "$namespaces"
	printf '%s\n' "$input/$stem.External.dll" "$input/$stem.Consumer.dll" > "$rsp"
	start_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	projection_output="$(dotnet "$OUT/tools/dll2klib.dll" --out "$klib" --jobs 1 @"$rsp")"
	end_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	elapsed_ms="$(( (end_ns - start_ns) / 1000000 ))"

	grep -q "$stem.Consumer.dll -> $stem.Consumer.klib: $namespaces public class(es)" \
		<<<"$projection_output" || die "consumer projection did not report all $namespaces public classes"
	grep -q "$stem.External.dll -> $stem.External.klib: $(( count + 2 )) public class(es)" \
		<<<"$projection_output" || die "external projection did not report all $(( count + 2 )) public classes"
	[[ -s "$klib/$stem.Consumer.klib" ]] || die "consumer projection did not produce a KLIB"
	[[ -s "$klib/$stem.External.klib" ]] || die "external projection did not produce a KLIB"
	printf -v last_namespace '%06d' "$(( namespaces - 1 ))"
	dotnet "$OUT/generator/Generator.dll" --verify "$klib/$stem.Consumer.klib" \
		"Consumer.N000000.UseDelegate" "Consumer.N${last_namespace}.UseDelegate" \
		|| die "consumer delegate/interface shapes are not isolated in the first and last package"
	printf '%s\n' "$elapsed_ms"
}

double_count="$(( TYPE_COUNT * 2 ))"
double_namespaces="$(( NAMESPACE_COUNT * 2 ))"
base_ms="$(measure_projection "$TYPE_COUNT" "$NAMESPACE_COUNT")"
double_ms="$(measure_projection "$double_count" "$double_namespaces")"
(( base_ms > 0 )) || die "base projection completed too quickly to measure"
ratio_hundredths="$(( double_ms * 100 / base_ms ))"
(( ratio_hundredths < 250 )) \
	|| die "dll2klib external delegate indexing regressed superlinearly: "\
"${TYPE_COUNT}/${NAMESPACE_COUNT}=${base_ms} ms, ${double_count}/${double_namespaces}=${double_ms} ms"

info "PASS  dll2klib external delegate indexing scales below 2.5x: "\
"${TYPE_COUNT}/${NAMESPACE_COUNT}=${base_ms} ms, ${double_count}/${double_namespaces}=${double_ms} ms (${ratio_hundredths}%)"
