#!/usr/bin/env bash
# MSBuild / .ktproj end-to-end integration gate on the SHIPPING IL backend (the default; no C# backend
# involved). Builds & runs real .ktproj (and reverse-interop .csproj) samples via `dotnet run`, asserting
# stdout. This is the only MSBuild-level gate now that the C# backend is retired — its old harness
# (verify-all.sh) was removed because there's no point regression-testing a backend we no longer ship.
# See docs/csharp-retirement-design.md. Inputs: cases/ktproj*/ + the toolchain. Exits nonzero on any FAIL.
source "$(dirname "$0")/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs every .ktproj integration sample (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

fail=0
# Build the compiler launcher once (a plain Java app) so the MSBuild EnsureKotlinClrCompiler bootstrap is a no-op.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1

# ktproj_run <project> <stderr-logfile>  — build+run a .ktproj on the IL backend; echo its noise-filtered stdout;
# RETURN the run's exit status (0 iff `dotnet run` — build AND execution — succeeded). Status and stdout are
# captured INDEPENDENTLY (issue #163): the process status is NOT lost to the grep pipe / `|| true` that used to
# mask a program which printed the expected text and THEN threw / returned non-zero.
ktproj_run() { # <project> <stderr-logfile>
	local proj="$1" log="$2" rc=0 raw
	raw="$(dotnet run --project "$ROOT/$proj" -v q --nologo 2>"$log")" || rc=$?
	printf '%s' "$raw" | grep -vE 'kotlin/clr:|duplicate source root' || true
	return $rc
}

# <name> <project> <expected>  — build+run a project on the IL backend and diff stdout. A non-zero run status is a
# FAIL (recording stderr + the failing stage) BEFORE any output compare — never masked, so one broken sample is
# reported as its own FAIL line and the gate still runs every remaining sample and summarizes at the end.
kt() {
	local name="$1" proj="$2" expected="$3"
	local actual rc=0 log="$ROOT/build/ktproj-run-$name.err"
	mkdir -p "$ROOT/build"
	actual="$(ktproj_run "$proj" "$log")" || rc=$?
	if (( rc != 0 )); then
		echo "FAIL  $name (run exit $rc)"
		printf -- '--- expected ---\n%s\n--- actual (stdout before failure) ---\n%s\n--- stderr ---\n%s\n' "$expected" "$actual" "$(tail -30 "$log" 2>/dev/null)"; fail=1; return
	fi
	if [[ "$actual" == "$expected" ]]; then echo "PASS  $name"; else
		echo "FAIL  $name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; fail=1
	fi
}

# ---- issue #163 self-test: a .ktproj whose main prints the EXPECTED text then throws MUST be REJECTED. Drives the
# real ktproj_run capture path and asserts a non-zero status is observed; a green (exit 0) means the hole is open. ----
ktproj_selftest() {
	local d="$ROOT/cases/ktproj-selftest"; rm -rf "$d"; mkdir -p "$d"
	cat > "$d/app.ktproj" <<'KTPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
  <Import Project="../KotlinClr.targets" />
</Project>
KTPROJ
	printf 'fun main() { println("SELFTEST-EXPECTED"); throw RuntimeException("boom after print") }\n' > "$d/app.kt"
	local rc=0
	ktproj_run "cases/ktproj-selftest/app.ktproj" "$d/run.err" >/dev/null || rc=$?
	rm -rf "$d"
	if (( rc == 0 )); then
		echo "KTPROJ GATE RED — #163 self-test FAILED: a print-then-crash .ktproj was accepted (exit-code hole open)"; exit 1
	fi
	echo "SELFTEST ktproj (print-then-crash correctly REJECTED, run exit $rc)"
}
ktproj_selftest

# A real .ktproj end-to-end.
kt ktproj "cases/ktproj/hello.ktproj" \
	"$(printf 'Hello, Visual Studio, from a .ktproj!\nsum 1..5 = 15')"

# MPP (#119): a multiplatform .ktproj — common/ carries the `expect class Greeter`, clr/ the `actual` + entry.
# <DotKtMultiplatform>true</DotKtMultiplatform> makes the shared targets tag common/ sources with -Xcommon-sources
# (+ -Xmulti-platform -Xexpect-actual-classes), so kotc's app pipeline does the common→platform module split and the
# actual resolves. The only gate coverage of the MPP source-set path through MSBuild.
kt ktproj-mpp "cases/ktproj-mpp/hello-mpp.ktproj" \
	"Hello from the CLR actual"

# Import-driven .NET resolution: plain `import System.Text.StringBuilder` / `import System.Math`, no <KotlinClrFacade>,
# no facade — the facadegen import scan injects the types. Fluent StringBuilder.Append chaining + Math.Max.
# Wired here (COV6, 2026-07-06): was UNWIRED (previously no gate covered the bare-import ktproj path).
kt ktproj-import "cases/ktproj-import/import.ktproj" \
	"dotkt imports just work: 40"

# FORWARD ProjectReference + AssemblyResolver + .NET event subscription from a referenced C# project.
# Also: assign a plain Boolean to the C# `bool?` (Nullable<bool>) property Enabled — facadegen maps Nullable<X> -> X?.
# Also: consume Widget.Name, a reference type from a NON-NRT assembly -> platform type String! (injector flexible type).
kt ktproj-extlib "cases/ktproj-extlib/app.ktproj" \
	"$(printf 'Add(2,3) = 5\nname: gadget (len 6)\nenabled: True\nchanged: 5\nchanged: 9')"

# BIDIRECTIONAL ProjectReference (R-1): cslib.csproj <- klib.ktproj <- app.csproj in one graph.
# forward = Kotlin imports the C# Theme.Palette; reverse = C# consumes the Kotlin Greeter + its List<String>
# at compile time (needs the emitted dll reference-clean via the retarget tool). Running the C# host drives all.
kt ktproj-bidir "cases/ktproj-bidir/app/app.csproj" \
	"$(printf 'Hi, Visual Studio (accent=cyan)\nVisual Studio A, Visual Studio B, Visual Studio C')"

# Framework-direct base class: a Kotlin class inherits Avalonia.Application from a <PackageReference>,
# façade-free, overriding a virtual. (Needs Avalonia in the NuGet cache.)
kt ktproj-avalonia "cases/ktproj-avalonia/app.ktproj" \
	"$(printf 'MyApp.Initialize: Kotlin override of Avalonia.Application\nsubclassed Avalonia.Application from Kotlin via PackageReference')"

# #27: a cross-module Kotlin LIBRARY whose public API takes kotlin.collections.* params — the params compile to their
# BCL @ClrTypeAlias interfaces in Lib.dll (List->IReadOnlyList, MutableList->IList, Map->IDictionary). The app
# references Lib.ktproj and calls those funs with listOf/mutableListOf/mapOf. Before the fix facadegen surfaced the
# raw IReadOnlyList/IList/IDictionary, so the frontend REJECTED the kotlin.collections.* args ("argument type mismatch"
# + "cannot infer type parameter T"). facadegen's reverse map now surfaces them back as kotlin.collections.*, and the
# generic `makeHolder(listOf(...))` inference + `h.items.size` member resolution succeed. Regression guard for #27.
kt ktproj-listparam "cases/ktproj-listparam/app/App.ktproj" \
	"$(printf '2\n3\n2\n2')"

# #29: a cross-module Kotlin LIBRARY nesting kotlin.collections.* INSIDE a user generic (`Box<List<T>>`, `State<List<T>>`).
# bir2cir's Root-V variance collapse lowers the nested read-only `List<T>` to the invariant `IList<T>` (load-bearing for
# reified-generic inhabitance), which collided with `MutableList`'s IList alias -> facadegen surfaced `Box<MutableList<T>>`
# and REJECTED the app's `Box<List<String>>` value. bir2cir now stamps [KotlinCollectionIdentity] (the pre-collapse Kotlin
# type) on each collapsed slot; facadegen restores List vs MutableList from it. A nested MutableList slot (unstamped) must
# still surface as MutableList (read/write split). Regression guard for #29.
kt ktproj-nestedlist "cases/ktproj-nestedlist/app/App.ktproj" \
	"$(printf '2\n3\n3\n[10, 20, 10]')"

# #33: a DIRECT read of a cross-module generic member whose declared return is the OWNER's type variable
# (`Pair2<Int, MutableList<Int>>.b` = the open B; `Wrap<Int>.items` = List<X>). bir2cir's StaticTypeResolver.Surface
# left the return as a bare `tv`, so the println collection-wrap misfired and printed the raw BCL `List`1`. Surface now
# substitutes tv(type,i) against the receiver's concrete instantiation (index 1 + nested List<X> exercised). Guard for #33.
kt ktproj-genmember "cases/ktproj-genmember/app/App.ktproj" \
	"$(printf '7\n[1, 2]\n[9, 8, 7]')"

# #15 EMIT-HALF: the pathological layout where the app's recursive `**/*.kt` glob pulls in a NESTED
# <ProjectReference> lib's SOURCE (App.kt + lib/Demo.kt) AND references that lib's dll — so `demo.Plain`/
# `demo.hello` are BOTH compiled LOCALLY and exported by the referenced Demo.dll. The frontend "source wins"
# fix (#15 core) suppresses the injected copy; bir2cir must then PREFER the local BIR type over the referenced
# dll of the same FQN — emitting a local `new demo.Plain` (this-assembly-emitted), NOT a `newClr` against
# Demo.dll (which made the app both emit `demo.Plain` locally AND newClr the ref copy → ilemit conflict).
# Before the fix: bir2cir/ilemit error. Regression guard for the local-over-ref resolution in ResolveNetType.
kt ktproj-injectemit "cases/ktproj-injectemit/App.ktproj" \
	"$(printf '42\nplain')"

# #17: a DIRECT property get/set on a re-imported cross-module Kotlin type whose package starts with `kotlinx.`
# (the atomicfu-port shape). App.ktproj references the `kotlinx.cell` Library and reads/writes `c.value`. The
# `kotlinx.` FQN makes bir2cir's NetInteropBinding skip the owner, so MemberCallSubstitution must lower the
# property access to the get_value/set_value accessor call — else ilemit's external ResolveMethod fails with
# "method kotlinx.cell.Cell.value() not found". Regression guard for the #17 instance-property-marker reconstruct.
kt ktproj-reprop "cases/ktproj-reprop/app/App.ktproj" \
	"$(printf '10\n42\n84')"

# #26: a cross-module Kotlin LIBRARY whose package FQN STARTS WITH `dotkt` (`dotktx.foo.bar`) but is a USER
# package, NOT the compiler's own `dotkt`/`dotkt$…` synthetic vocabulary. The app captures a local of the lib's
# `State<Int>` inside a lambda stored as a delegate and fired later cross-module. Before the fix, bir2cir's
# ResolveNetType matched the owner FQN with a bare `StartsWith("dotkt")`, so `dotktx.foo.bar.State` was wrongly
# skipped as "not a .NET/reference type" → the captured cross-module local was mishandled → runtime NRE/
# InvalidProgram (compile clean). The guard now matches `dotkt` only as a full segment (`dotkt`/`dotkt.`/`dotkt$`).
kt ktproj-dotktpkg "cases/ktproj-dotktpkg/app/App.ktproj" \
	"2"

# #18: a re-imported cross-module GENERIC factory `fun <T> holderOf(n): Holder<T?>`. bir2cir object-erases the nested
# `Nullable(Tv)` to `Holder<object>`; the [KotlinNullableGeneric] round-trip attribute (stamped by RoundtripMetadata,
# restored by facadegen) recovers `Holder<T?>` so the app's `h.size` + `h[0]` resolve. Before the fix `h` degraded to
# `Any?` and the app FAILED TO COMPILE (unresolved `size`/indexer). Regression guard for the coroutines-port blocker.
kt ktproj-genq "cases/ktproj-genq/app/App.ktproj" \
	"$(printf '3\nempty\ncell-null')"

# #25: a re-imported cross-module GENERIC top-level fun among a same-name OVERLOAD SET (the reduced kotlinx-atomicfu
# `atomic` shape: non-generic `atomic(Int/Long/Boolean/Double)` + generic `atomic(T)` + a defaulted-sibling
# `atomic(T, trace=None)` + a sole-generic `arrOf<T>(n)`). kotc emits a generic call as `callStatic shapeTypes=[…]`
# with NO `sig`; because the owner is `kotlinx.*` NetInteropBinding leaves it a plain callStatic, so ilemit's
# overload resolution needs `sig`. bir2cir now promotes `shapeTypes`->`sig` so `atomic<String?>(null)` binds to the
# ARITY-1 `atomic(T)` (tag "gen1", NOT the arity-2 defaulted sibling -> NRE) and `arrOf<String>(3)` is found (was
# "static method not found"). Regression guard for the atomicfu-port cross-module generic-overload blocker.
kt ktproj-genov "cases/ktproj-genov/app/App.ktproj" \
	"$(printf '3\ngen1\nint')"

# #25 RESIDUAL: a GENERIC top-level factory declared in a MULTIPLATFORM library's COMMON fragment (file class
# `GenovCommonKt`), consumed cross-module. kotc emits the generic call as `callStatic ownerType=…GenovCommonKt
# typeArgs=[…] shapeTypes=[…]` with NO `sig`; the sig-less generic call yields an EMPTY receiver-key, so bir2cir's
# TryResolveTopLevelStatic can't disambiguate the owner once the bare fun name lives under >1 file-class in the ref
# index (here `arrOfNulls` also exists in the sibling package `GenovAltKt`) — the first #25 fix's owner-attribution
# was skipped, leaving the call un-promoted -> ilemit "static method not found: arrOfNulls". bir2cir now adopts
# kotc's facadegen-injected `ownerType` as the owner and promotes `shapeTypes`->`sig`. Regression guard for the
# common-fragment (`*CommonKt`) arm of the atomicfu-port cross-module generic-factory blocker.
kt ktproj-genov-common "cases/ktproj-genov-common/app/App.ktproj" \
	"3"

# PRACTICAL COLLECTIONS app consuming the real CLR stdlib (DotKt.Stdlib.dll): a List held as an app local (resolves as
# the referenced IReadOnlyList), member access (size/indexing), TOP-LEVEL stdlib funs (first/getOrElse/contains/indexOf/
# count/isEmpty/take) which kotc emits as `callStatic owner=null` and bir2cir attributes to their file-class owner
# (kotlin.collections._CollectionsKt), AND `for (x in list)` (the iterator protocol re-pointed at the real referenced
# kotlin.collections.Iterator<E> via the rt bridge). The whole app-consume gap, end-to-end through MSBuild.
kt ktproj-coll "cases/ktproj-coll/app.ktproj" \
	"$(printf '5\n30\n10\n20\n-1\nTrue\n3\n5\nFalse\n2\n150\nAPPLE\npear\n5\n4\n3')"

# #37 finding 1 (RID-aware identity selection): a PackageReference (System.IO.Ports) whose copy-local set carries
# BOTH lib/<tfm>/Foo.dll and runtimes/<rid>/lib/<tfm>/Foo.dll for ONE identity. ilemit's runtime catalog used to
# hard-fail at emit on the duplicate simple name; it now dedups by identity and selects the host-RID asset. On Linux
# the runtimes/unix/lib build is the REAL impl (the plain lib asset is a PlatformNotSupported placeholder), so
# GetPortNames() returning a count (0 here) — not throwing — proves the RID-correct asset was selected (keep-first
# would have picked the placeholder). Regression guard for #37 finding 1.
kt ktproj-runtimetargets "cases/ktproj-runtimetargets/app.ktproj" \
	"ports 0"

# #37 finding 3 (catalog-first, TPA-fallback): framework/inbox types NOT copy-local (absent from the runtime
# catalog) — System.Text.Json.JsonSerializerOptions + System.Net.Http.HttpClient — must resolve via the fallback
# onto ilemit's own host framework (TPA). Before the fix these hard-failed "cannot resolve .NET type". Regression
# guard for #37 finding 3.
kt ktproj-inbox "cases/ktproj-inbox/app.ktproj" \
	"$(printf 'indented False\ntimeout 100')"

# #50: INCREMENTAL deletion-safety + staleness through MSBuild. A single dir is built TWICE with the SAME obj/ (no
# clean) — the incremental path the shared targets guard. Between the builds a top-level `class Shape` is MOVED out of
# its own Shape.kt into App.kt and Shape.kt is DELETED. Pre-#50 the BIR was globbed from $(DotKtOut), which was never
# cleaned, so the deleted Shape.kt left a stale Shape.bir.json behind → Shape was emitted TWICE (App.cir.json's moved
# copy + the orphan Shape.cir.json) → ilemit "type already defined" → the second build FAILED. The fix wipes
# $(DotKtOut) on every recompile, so the stale artifact cannot survive. This case reproduces that exact failure and
# asserts BOTH builds run "12" (the deleted source is gone from the emitted dll). The dir is generated + removed here
# (not a committed sample) because the assertion is a stateful two-build mutation, not a single `dotnet run`.
incr="$ROOT/cases/ktproj-incr"
rm -rf "$incr"; mkdir -p "$incr"
cat > "$incr/app.ktproj" <<'KTPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <Import Project="../KotlinClr.targets" />
</Project>
KTPROJ
# STATE 1: `class Shape` lives in its own file. (Both builds capture the run status independently — issue #163 —
# so a build/run that prints "12" then fails is not silently accepted.)
printf 'fun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\n' > "$incr/Shape.kt"
incr_rc1=0; incr1="$(ktproj_run "cases/ktproj-incr/app.ktproj" "$incr/run1.err")" || incr_rc1=$?
# STATE 2: MOVE `class Shape` into App.kt and DELETE Shape.kt — rebuild on the SAME obj/ (incremental).
rm -f "$incr/Shape.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\nfun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
incr_rc2=0; incr2="$(ktproj_run "cases/ktproj-incr/app.ktproj" "$incr/run2.err")" || incr_rc2=$?
if [[ $incr_rc1 -eq 0 && $incr_rc2 -eq 0 && "$incr1" == "12" && "$incr2" == "12" ]]; then echo "PASS  ktproj-incr"; else
	echo "FAIL  ktproj-incr (build1 exit $incr_rc1, build2 exit $incr_rc2)"
	printf -- '--- build1 (want 12) ---\n%s\n--- build2 incremental after delete (want 12) ---\n%s\n--- stderr build2 ---\n%s\n' "$incr1" "$incr2" "$(tail -20 "$incr/run2.err" 2>/dev/null)"; fail=1
fi
rm -rf "$incr"

# Clean each sample's build output.
rm -rf "$ROOT"/cases/ktproj/bin "$ROOT"/cases/ktproj/obj \
       "$ROOT"/cases/ktproj-mpp/bin "$ROOT"/cases/ktproj-mpp/obj \
       "$ROOT"/cases/ktproj-listparam/*/bin "$ROOT"/cases/ktproj-listparam/*/obj \
       "$ROOT"/cases/ktproj-nestedlist/*/bin "$ROOT"/cases/ktproj-nestedlist/*/obj \
       "$ROOT"/cases/ktproj-genmember/*/bin "$ROOT"/cases/ktproj-genmember/*/obj \
       "$ROOT"/cases/ktproj-injectemit/bin "$ROOT"/cases/ktproj-injectemit/obj \
       "$ROOT"/cases/ktproj-injectemit/lib/bin "$ROOT"/cases/ktproj-injectemit/lib/obj \
       "$ROOT"/cases/ktproj-reprop/*/bin "$ROOT"/cases/ktproj-reprop/*/obj \
       "$ROOT"/cases/ktproj-dotktpkg/*/bin "$ROOT"/cases/ktproj-dotktpkg/*/obj \
       "$ROOT"/cases/ktproj-genq/*/bin "$ROOT"/cases/ktproj-genq/*/obj \
       "$ROOT"/cases/ktproj-genov/*/bin "$ROOT"/cases/ktproj-genov/*/obj \
       "$ROOT"/cases/ktproj-genov-common/*/bin "$ROOT"/cases/ktproj-genov-common/*/obj \
       "$ROOT"/cases/ktproj-import/bin "$ROOT"/cases/ktproj-import/obj \
       "$ROOT"/cases/ktproj-extlib/bin "$ROOT"/cases/ktproj-extlib/obj \
       "$ROOT"/cases/ktproj-extlib/extlib/bin "$ROOT"/cases/ktproj-extlib/extlib/obj \
       "$ROOT"/cases/ktproj-bidir/*/bin "$ROOT"/cases/ktproj-bidir/*/obj \
       "$ROOT"/cases/ktproj-coll/bin "$ROOT"/cases/ktproj-coll/obj \
       "$ROOT"/cases/ktproj-runtimetargets/bin "$ROOT"/cases/ktproj-runtimetargets/obj \
       "$ROOT"/cases/ktproj-inbox/bin "$ROOT"/cases/ktproj-inbox/obj \
       "$ROOT"/cases/ktproj-avalonia/bin "$ROOT"/cases/ktproj-avalonia/obj

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
