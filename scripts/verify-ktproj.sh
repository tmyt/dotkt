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

# <name> <project> <expected>  — build+run a project on the IL backend and diff stdout.
kt() {
	local name="$1" proj="$2" expected="$3"
	local actual
	# `|| true`: a sample that fails to build/run (non-zero `dotnet run`, surfaced via pipefail) must be reported as
	# its own FAIL line and NOT abort the whole gate under `set -e` — otherwise one broken sample masks every sample
	# after it (the gate is meant to run ALL samples and summarize at the end).
	actual="$(dotnet run --project "$ROOT/$proj" -v q --nologo 2>/dev/null | grep -vE 'kotlin/clr:|duplicate source root' || true)"
	if [[ "$actual" == "$expected" ]]; then echo "PASS  $name"; else
		echo "FAIL  $name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; fail=1
	fi
}

# A real .ktproj end-to-end.
kt ktproj "cases/ktproj/hello.ktproj" \
	"$(printf 'Hello, Visual Studio, from a .ktproj!\nsum 1..5 = 15')"

# MPP (#119): a multiplatform .ktproj — common/ carries the `expect class Greeter`, clr/ the `actual` + entry.
# <DotKtMultiplatform>true</DotKtMultiplatform> makes the shared targets tag common/ sources with -Xcommon-sources
# (+ -Xmulti-platform -Xexpect-actual-classes), so kotc's app pipeline does the common→platform module split and the
# actual resolves. The only gate coverage of the MPP source-set path through MSBuild.
kt ktproj-mpp "cases/ktproj-mpp/hello-mpp.ktproj" \
	"Hello from the CLR actual"

# The README-advertised pure-IL starter project (README:139 points users at cases/ktproj-il/): a two-file
# .ktproj (App.kt with `fun main` + a Greeter class) built entirely on the IL backend via ../KotlinClr.targets.
# Wired here (COV6, 2026-07-06) so the user-facing sample can't rot unverified — it previously had NO gate.
kt ktproj-il "cases/ktproj-il/hello-il.ktproj" \
	"$(printf 'Hello, ktproj, from IL!\nsum 1..5 = 15')"

# A stdlib op MIGRATED off the COLLECTION_OPS lowering (getOrElse): the targets auto-reference DotKt.Stdlib, so the
# call routes to its real Kotlin body. End-to-end proof that the lowering-retirement pipeline works through MSBuild.
kt ktproj-stdlib "cases/ktproj-stdlib/app.ktproj" \
	"$(printf '20\n500')"

# Façade-FREE FIR injection via import scan (the C-2 single path for taking in .NET types).
kt ktproj-inject "cases/ktproj-inject/inject.ktproj" \
	"no-facade via import scan; abs(-5)=5"

# Import-driven .NET resolution: plain `import System.Text.StringBuilder` / `import System.Math`, no <KotlinClrFacade>,
# no facade — the facadegen --meta import scan injects the types. Fluent StringBuilder.Append chaining + Math.Max.
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

# KOTLIN -> KOTLIN ProjectReference round-trip: app.ktproj consumes lib.ktproj AS KOTLIN (top-level generic/plain
# functions + a top-level extension infix + classes). The round-trip path through MSBuild.
kt ktproj-roundtrip "cases/ktproj-roundtrip/app/App.ktproj" \
	"$(printf '7\n5\nhi\n3\n40')"

# APP + LIB via <ProjectReference>: App.ktproj (Exe) references Shapes.ktproj (Library), which DotKt emits as a real
# .NET assembly (Shapes.dll) the app consumes WITHOUT recompiling the lib's sources. Exercises a richer library API
# than ktproj-roundtrip — a class with a computed property + member fn, a data-class toString, an enum constant, a
# top-level fn, and a top-level extension fn — all re-imported AS KOTLIN from the referenced dll's round-trip metadata.
kt ktproj-applib "cases/ktproj-applib/app/App.ktproj" \
	"$(printf 'Rectangle 3x4 area=12\n48\nPoint(x=-2, y=5)\n7\nBLUE')"

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

# PRACTICAL COLLECTIONS app consuming the real CLR stdlib (DotKt.Stdlib.dll): a List held as an app local (resolves as
# the referenced IReadOnlyList), member access (size/indexing), TOP-LEVEL stdlib funs (first/getOrElse/contains/indexOf/
# count/isEmpty/take) which kotc emits as `callStatic owner=null` and bir2cir attributes to their file-class owner
# (kotlin.collections._CollectionsKt), AND `for (x in list)` (the iterator protocol re-pointed at the real referenced
# kotlin.collections.Iterator<E> via the rt bridge). The whole app-consume gap, end-to-end through MSBuild.
kt ktproj-coll "cases/ktproj-coll/app.ktproj" \
	"$(printf '5\n30\n10\n20\n-1\nTrue\n3\n5\nFalse\n2\n150\nAPPLE\npear\n5\n4\n3')"

# The <KotlinClrRefRt>true</KotlinClrRefRt> MSBuild property: build against the stdlib REFERENCE assembly and run
# against the RUNTIME assembly (the ref->rt handoff, exactly as verify-il/dotkt do it). A single self-contained
# .ktproj consuming the real CLR stdlib (listOf/size/indexing/uppercase/for). Wired here (COV6, 2026-07-06): was
# UNWIRED — the only gate coverage of the <KotlinClrRefRt> app property.
kt ktproj-refrt "cases/ktproj-refrt/app.ktproj" \
	"$(printf '3\nAPPLE\n12')"

# <KotlinClrRefRt> + a Kotlin->Kotlin <ProjectReference>: App.ktproj consumes Lib.ktproj (Library) AS KOTLIN
# (top-level `greeting`/`sumTo` from package mylib) with the ref->rt stdlib flow on BOTH projects. Wired here
# (COV6, 2026-07-06): was UNWIRED — covers the refrt property across a project-reference graph.
kt ktproj-refrt-pr "cases/ktproj-refrt-pr/app/App.ktproj" \
	"$(printf 'Hello, WORLD!\n55\n3\nHello, Z!')"

# Clean each sample's build output.
rm -rf "$ROOT"/cases/ktproj/bin "$ROOT"/cases/ktproj/obj \
       "$ROOT"/cases/ktproj-mpp/bin "$ROOT"/cases/ktproj-mpp/obj \
       "$ROOT"/cases/ktproj-il/bin "$ROOT"/cases/ktproj-il/obj \
       "$ROOT"/cases/ktproj-roundtrip/*/bin "$ROOT"/cases/ktproj-roundtrip/*/obj \
       "$ROOT"/cases/ktproj-applib/*/bin "$ROOT"/cases/ktproj-applib/*/obj \
       "$ROOT"/cases/ktproj-injectemit/bin "$ROOT"/cases/ktproj-injectemit/obj \
       "$ROOT"/cases/ktproj-injectemit/lib/bin "$ROOT"/cases/ktproj-injectemit/lib/obj \
       "$ROOT"/cases/ktproj-reprop/*/bin "$ROOT"/cases/ktproj-reprop/*/obj \
       "$ROOT"/cases/ktproj-genq/*/bin "$ROOT"/cases/ktproj-genq/*/obj \
       "$ROOT"/cases/ktproj-genov/*/bin "$ROOT"/cases/ktproj-genov/*/obj \
       "$ROOT"/cases/ktproj-inject/bin "$ROOT"/cases/ktproj-inject/obj \
       "$ROOT"/cases/ktproj-import/bin "$ROOT"/cases/ktproj-import/obj \
       "$ROOT"/cases/ktproj-refrt/bin "$ROOT"/cases/ktproj-refrt/obj \
       "$ROOT"/cases/ktproj-refrt-pr/*/bin "$ROOT"/cases/ktproj-refrt-pr/*/obj \
       "$ROOT"/cases/ktproj-extlib/bin "$ROOT"/cases/ktproj-extlib/obj \
       "$ROOT"/cases/ktproj-extlib/extlib/bin "$ROOT"/cases/ktproj-extlib/extlib/obj \
       "$ROOT"/cases/ktproj-bidir/*/bin "$ROOT"/cases/ktproj-bidir/*/obj \
       "$ROOT"/cases/ktproj-coll/bin "$ROOT"/cases/ktproj-coll/obj \
       "$ROOT"/cases/ktproj-avalonia/bin "$ROOT"/cases/ktproj-avalonia/obj

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
