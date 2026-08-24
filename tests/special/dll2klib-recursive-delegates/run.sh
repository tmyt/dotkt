#!/usr/bin/env bash
# CLR delegate graphs can be recursive even though Kotlin function types cannot. Verify that local, generic, and
# cross-assembly cycles terminate with the same deliberate diagnostic instead of overflowing a worker stack.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-recursive-delegates
source "$ROOT/scripts/lib.sh"

OUT="$ROOT/build/dll2klib-recursive-delegates"
rm -rf "$OUT"
mkdir -p "$OUT/tools" "$OUT/generated"

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-recursive-delegates/Generator.csproj" \
	-c Release -o "$OUT/generator" -v:q --nologo
dotnet "$OUT/generator/Generator.dll" "$OUT/generated"

expect_recursive_failure() {
	local name="$1"
	shift
	local rsp="$OUT/$name.rsp"
	printf '%s\n' "$@" > "$rsp"
	local log="$OUT/$name.log"
	if dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/$name-klib" --jobs 1 "@$rsp" >"$log" 2>&1; then
		die "$name recursive delegate graph unexpectedly projected"
	fi
	grep -q "recursive CLR delegate graph cannot be represented as a finite Kotlin function type" "$log" \
		|| die "$name did not report the recursive delegate contract"
	! grep -q "Stack overflow" "$log" || die "$name still overflowed the worker stack"
}

expect_recursive_failure local "$OUT/generated/Recursive.Local.dll"
expect_recursive_failure generic "$OUT/generated/Recursive.Generic.dll"
expect_recursive_failure cross \
	"$OUT/generated/Recursive.CrossA.dll" \
	"$OUT/generated/Recursive.CrossB.dll"

grep -Eq "Recursive.Local/Recursive.Local.A -> Recursive.Local/Recursive.Local.B -> Recursive.Local/Recursive.Local.A|Recursive.Local/Recursive.Local.B -> Recursive.Local/Recursive.Local.A -> Recursive.Local/Recursive.Local.B" \
	"$OUT/local.log" || die "local mutual-recursion diagnostic did not retain the cycle path"
grep -q "Recursive.Generic/Recursive.Generic.Self -> Recursive.Generic/Recursive.Generic.Self" \
	"$OUT/generic.log" || die "generic self-recursion diagnostic did not retain the cycle path"
grep -Eq "Recursive.CrossA/Recursive.Cross.A -> Recursive.CrossB/Recursive.Cross.B -> Recursive.CrossA/Recursive.Cross.A|Recursive.CrossB/Recursive.Cross.B -> Recursive.CrossA/Recursive.Cross.A -> Recursive.CrossB/Recursive.Cross.B" \
	"$OUT/cross.log" || die "cross-assembly diagnostic did not retain the cycle path"

info "PASS  recursive CLR delegate graphs terminate with a stable local/generic/cross-assembly diagnostic"
