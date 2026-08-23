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
mkdir -p "$OUT/tools" "$OUT/input"

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-local-index-benchmark/Generator.csproj" \
	-c Release -o "$OUT/generator" -v:q --nologo

measure_projection() {
	local count="$1" input="$OUT/input/Synthetic${1}.dll" klib="$OUT/klib-${1}"
	local rsp="$OUT/references-${1}.rsp" start_ns end_ns elapsed_ms projection_output
	mkdir -p "$klib"
	dotnet "$OUT/generator/Generator.dll" "$input" "$count"
	printf '%s\n' "$input" > "$rsp"
	start_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	projection_output="$(dotnet "$OUT/tools/dll2klib.dll" --out "$klib" --jobs 1 @"$rsp")"
	end_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
	elapsed_ms="$(( (end_ns - start_ns) / 1000000 ))"

	grep -q "Synthetic${count}.dll -> Synthetic${count}.klib: ${count} public class(es)" \
		<<<"$projection_output" || die "synthetic projection did not report all $count public classes"
	[[ -s "$klib/Synthetic${count}.klib" ]] || die "synthetic projection did not produce a KLIB"
	printf '%s\n' "$elapsed_ms"
}

double_count="$(( TYPE_COUNT * 2 ))"
base_ms="$(measure_projection "$TYPE_COUNT")"
double_ms="$(measure_projection "$double_count")"
(( base_ms > 0 )) || die "base projection completed too quickly to measure"
ratio_hundredths="$(( double_ms * 100 / base_ms ))"
(( ratio_hundredths < 300 )) \
	|| die "local TypeDef projection regressed superlinearly: ${TYPE_COUNT}=${base_ms} ms, ${double_count}=${double_ms} ms"

info "PASS  local TypeDef projection scales below 3x: ${TYPE_COUNT}=${base_ms} ms, ${double_count}=${double_ms} ms (${ratio_hundredths}%)"
