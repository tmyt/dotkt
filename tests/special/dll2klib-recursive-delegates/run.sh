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
	grep -Fq "recursive CLR delegate graph cannot be represented as a finite Kotlin function type" "$log" \
		|| die "$name did not report the recursive delegate contract"
	! grep -Fq "Stack overflow" "$log" || die "$name still overflowed the worker stack"
}

expect_recursive_failure local "$OUT/generated/Recursive.Local.dll"
expect_recursive_failure generic "$OUT/generated/Recursive.Generic.dll"
expect_recursive_failure return "$OUT/generated/Recursive.Return.dll"
expect_recursive_failure array "$OUT/generated/Recursive.Array.dll"
expect_recursive_failure container "$OUT/generated/Recursive.Container.dll"
expect_recursive_failure cross \
	"$OUT/generated/Recursive.CrossA.dll" \
	"$OUT/generated/Recursive.CrossB.dll"

if ! grep -Fq "Recursive.Local/Recursive.Local.A -> Recursive.Local/Recursive.Local.B -> Recursive.Local/Recursive.Local.A" "$OUT/local.log" && \
	! grep -Fq "Recursive.Local/Recursive.Local.B -> Recursive.Local/Recursive.Local.A -> Recursive.Local/Recursive.Local.B" "$OUT/local.log"; then
	die "local mutual-recursion diagnostic did not retain the cycle path"
fi
grep -Fq "Recursive.Generic/Recursive.Generic.Self -> Recursive.Generic/Recursive.Generic.Self" \
	"$OUT/generic.log" || die "generic self-recursion diagnostic did not retain the cycle path"
if ! grep -Fq "Recursive.CrossA/Recursive.Cross.A -> Recursive.CrossB/Recursive.Cross.B -> Recursive.CrossA/Recursive.Cross.A" "$OUT/cross.log" && \
	! grep -Fq "Recursive.CrossB/Recursive.Cross.B -> Recursive.CrossA/Recursive.Cross.A -> Recursive.CrossB/Recursive.Cross.B" "$OUT/cross.log"; then
	die "cross-assembly diagnostic did not retain the cycle path"
fi

printf '%s\n' "$OUT/generated/Recursive.Modifier.dll" > "$OUT/modifier.rsp"
dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/modifier-klib" --jobs 1 "@$OUT/modifier.rsp" \
	>"$OUT/modifier.log" 2>&1
[[ -f "$OUT/modifier-klib/Recursive.Modifier.klib" ]] \
	|| die "recursive delegate used only as an erased custom modifier did not project"
! grep -Fq "recursive CLR delegate graph" "$OUT/modifier.log" \
	|| die "erased custom modifier expanded an irrelevant recursive delegate graph"

info "PASS  recursive CLR delegate graphs terminate across local/generic/cross-assembly shapes; erased modifiers stay finite"
