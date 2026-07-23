#!/usr/bin/env bash
# Formal IL verification for the categorized NUnit suites — run ONCE over each DotKt-emitted test
# assembly (not per-case). ilverify checks only the target assembly's own methods; the -r sets are resolution
# scopes only (the shared framework + the assembly's own output dir, which already holds NUnit/stdlib/producer).
# Whole-assembly verification replaces the former per-case, per-dll shell invocations.
#
# Green (exit 0) iff every ilverify finding matches an ILVERIFY_XFAIL substring below — the same
# machine-readable "one reason per known finding" discipline. Any finding
# outside the baseline is a NEW-FAIL and reddens the gate.
#
# Usage: tests/run-ilverify.sh <emitted-test-assembly.dll> [<more.dll> ...]
set -euo pipefail

# Known runtime-safe, formal-only findings (substring -> tracking issue + reason). A finding line must contain
# one of these substrings to be tolerated. Keys are narrow fixture/method or emitted-type identifiers so they only mask
# the documented shape.
declare -A ILVERIFY_XFAIL=(
	# --- CorA coroutine batch (DotKt.Tests.Coroutines.dll) migrated from verify-compiler-tests.sh (cases/il-coctxkey / il-cointercept /
	#     il-awaitintercept / il-classdeleg). Each carries the SAME runtime-safe formal-only finding its verify-compiler-tests.sh
	#     XFAIL_ILVERIFY entry carried before migration; re-expressed for the battery types. All coroutines fixtures RUN green.
	# #12 (formal-only, closed-#2 follow-up): a self-ref-bounded CoroutineContext.Key<E : Element> star-projected to Key<*> is
	# realized as a Key<Self> companion where the invariant Key<Element> slot is formally expected (StackUnexpected). Runtime
	# -safe (the reference is only stored/compared, never variance-cast). A bir2cir/representation follow-up, NOT ilemit codegen.
	["CorACtxkElem::.ctor()"]="#12 (formal-only, closed-#2 follow-up): AbstractCoroutineContextElement subtype passes its Key<Self> companion where invariant Key<Element> is expected — runtime-safe (RUN green)"
	["CorAIceptInterceptor::get_key()"]="#12 (formal-only, closed-#2 follow-up): ContinuationInterceptor impl get_key() returns Key<Self> where invariant Key<Element> is expected — runtime-safe (RUN green)"
	["CorAAwiCountingInterceptor::get_key()"]="#12 (formal-only, closed-#2 follow-up): counting-interceptor get_key() returns Key<Self> where invariant Key<Element> is expected — runtime-safe, #7 await-resume precedence RUN green"
	# #174: the generic class-delegation (#81) forwarder narrows the MutableList iterator()/listIterator() return to the
	# read-only Iterator/ListIterator where the Mutable slot is formally expected. Runtime-safe (the backing MutableList
	# returns a real Mutable iterator; RUN green). Keyed by the emitted type name (backtick-free — a raw generic-arity
	# backtick in a bash double-quoted map key triggers command substitution) to cover all three narrowed forwarders.
	["CorADelTracked"]="#174: class-delegation (#81) forwarder narrows MutableList iterator()/listIterator() return to the read-only Iterator/ListIterator where Mutable is expected — runtime-safe covariance-erasure (RUN green)"
	# #18 (migrated ktproj-genq): a re-imported generic factory `holderOf(): Vault<T?>` whose bir2cir
	# NullableGenericReturnErasure object-erases the nested Nullable(Tv) to `Vault<object>`; the [KotlinNullableGeneric]
	# round-trip restores `Vault<String?>` at the frontend, so the call's erased `Vault<object>` return meets the
	# consumer's restored `Vault<string>` slot — StackUnexpected. Runtime-SAFE (object/string are reference-compatible;
	# the erased Vault holds the string; the value-assert RUN lane is green). Same object-erasure formal-only family.
	["GenericMetadataRoundtripTests::nullableGenericMembersRoundTrip()"]="#18 nullable-generic object-erasure: holderOf's erased Vault<object> return vs the restored Vault<string> slot — runtime-safe (RUN green)"
	# #29 (migrated ktproj-nestedlist): the Root-V variance collapse lowers a nested read-only `List<T>` to its
	# INVARIANT CLR sibling `IList<T>`; at a use site the read-only `IReadOnlyCollection<T>` shape is expected, so the
	# collapsed `IList<int32>` meets an `IReadOnlyCollection<int32>` slot — StackUnexpected. Runtime-SAFE (the concrete
	# list implements both interfaces; the value-assert RUN lane is green). Same covariant-collection formal-only family.
	["GenericMetadataRoundtripTests::nestedGenericCollectionsRoundTrip()"]="#29 Root-V collapse: nested List<T> lowered to invariant IList<int32> vs an expected IReadOnlyCollection<int32> — runtime-safe (RUN green)"
	# #127/#86: copyOf on a value-element array returns Array<T?>, represented as object[] while the formal callsite
	# expects Nullable<Int>[]; all prefix/tail value assertions run green.
	["ArrayTests::copyOfGrowsWithNullTail()"]="#127/#86 nullable value-array object erasure: copyOf returns object[] where Nullable<Int>[] is formally expected — runtime-safe (RUN green)"
	# #12 (formal-only follow-up of closed #2): the migrated il-genbaseext (CorBSequenceTests) declares an external
	# generic base (AbstractCoroutineContextKey) over a companion CoroutineContext.Key; its `get_key()` returns the
	# Key<Self> companion where the invariant Key<Element> is formally expected (star-projection covariance the CLR
	# has no equivalent for). Runtime-SAFE (the value-assert RUN lane is green). Mirror of the verify-compiler-tests.sh
	# [genbaseext] XFAIL_ILVERIFY entry, re-expressed for DotKt.Tests.Coroutines.dll.
	["CorBGbeBase::get_key()"]="#12 formal-only covariance: external-generic-base get_key() returns Key<Self> companion where invariant Key<Element> is expected — runtime-safe (RUN green)"
	# localloc is intentionally unverifiable ECMA-335 IL. The runtime test validates the resulting Span writes/reads.
	["StackBufferTests::stackAllocationAndSpanInterop()"]="by design: stackalloc emits localloc, which ILVerify must report as unverifiable; runtime assertions are green"
)

ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
[[ -n "$ILV" ]] || { echo "ilverify: ILVerify.dll not found — install: dotnet tool install -g dotnet-ilverify"; exit 1; }
RTDIR="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/* 2>/dev/null | sort -V | tail -1)"
[[ -d "$RTDIR" ]] || { echo "ilverify: Microsoft.NETCore.App shared framework not found"; exit 1; }

is_xfail() { # <finding line> -> 0 if it matches a baseline substring
	local line="$1" key
	for key in "${!ILVERIFY_XFAIL[@]}"; do [[ "$line" == *"$key"* ]] && return 0; done
	return 1
}

rc=0
for dll in "$@"; do
	[[ -f "$dll" ]] || { echo "ilverify: MISSING $dll"; rc=1; continue; }
	bindir="$(dirname "$dll")"
	out="$(dotnet "$ILV" "$dll" -r "$RTDIR/*.dll" -r "$bindir/*.dll" 2>&1 || true)"
	# Finding lines look like:  [IL]: Error [Kind]: [<asm> : Fixture::method()][offset ...] <msg>
	mapfile -t findings < <(grep -E '\[IL\]: Error|Error \[' <<<"$out" || true)
	declare -a newfails=() xfailed=()
	for f in "${findings[@]}"; do
		if is_xfail "$f"; then xfailed+=("$f"); else newfails+=("$f"); fi
	done
	if (( ${#newfails[@]} == 0 )); then
		echo "VERIFY  $(basename "$dll")${xfailed[0]:+  (${#xfailed[@]} XFAIL finding(s), all baseline-listed)}"
		for f in ${xfailed[@]+"${xfailed[@]}"}; do echo "    XFAIL: $f"; done
	else
		echo "VERIFY FAIL  $(basename "$dll") — ${#newfails[@]} finding(s) outside the ILVERIFY_XFAIL baseline:"
		for f in "${newfails[@]}"; do echo "    NEW-FAIL: $f"; done
		rc=1
	fi
	unset newfails xfailed
done
exit $rc
