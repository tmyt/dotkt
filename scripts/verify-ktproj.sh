#!/usr/bin/env bash
# MSBuild / .ktproj end-to-end integration on the SHIPPING IL backend (the default; no C# backend involved).
# Builds & runs real .ktproj (and a reverse-interop .csproj) via `dotnet run`, asserting stdout. This is the
# only MSBuild-level gate now that the C# backend is retired — its old harness (verify-all.sh) was removed
# because there's no point regression-testing a backend we no longer ship. See docs/csharp-retirement-design.md.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# No KotlinClrBackend override -> the default IL backend.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
fail=0

# Build the compiler launcher once (a plain Java app) so the MSBuild EnsureKotlinClrCompiler bootstrap is a no-op.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1

# <name> <project> <expected>  — build+run a project on the IL backend and diff stdout.
kt() {
	local name="$1" proj="$2" expected="$3"
	local actual
	actual="$(dotnet run --project "$ROOT/$proj" -v q --nologo 2>/dev/null | grep -vE 'kotlin/clr:|duplicate source root')"
	if [[ "$actual" == "$expected" ]]; then echo "PASS  $name"; else
		echo "FAIL  $name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; fail=1
	fi
}

# A real .ktproj end-to-end.
kt ktproj "cases/ktproj/hello.ktproj" \
	"$(printf 'Hello, Visual Studio, from a .ktproj!\nsum 1..5 = 15')"

# Façade-FREE FIR injection via import scan (the C-2 single path for taking in .NET types).
kt ktproj-inject "cases/ktproj-inject/inject.ktproj" \
	"no-facade via import scan; abs(-5)=5"

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
# functions + a top-level extension infix + classes). The round-trip path through MSBuild; it regressed because ilemit
# wasn't passed --ref DotKt.Runtime, so it silently skipped stamping [KotlinFileClass]/[KotlinFunction] and the consumer's
# `import mylib.boxed` resolved to nothing.
kt ktproj-roundtrip "cases/ktproj-roundtrip/app/App.ktproj" \
	"$(printf '7\n5\nhi\n3\n40')"

# Clean each sample's build output.
rm -rf "$ROOT"/cases/ktproj/bin "$ROOT"/cases/ktproj/obj \
       "$ROOT"/cases/ktproj-roundtrip/*/bin "$ROOT"/cases/ktproj-roundtrip/*/obj \
       "$ROOT"/cases/ktproj-inject/bin "$ROOT"/cases/ktproj-inject/obj \
       "$ROOT"/cases/ktproj-extlib/bin "$ROOT"/cases/ktproj-extlib/obj \
       "$ROOT"/cases/ktproj-extlib/extlib/bin "$ROOT"/cases/ktproj-extlib/extlib/obj \
       "$ROOT"/cases/ktproj-bidir/*/bin "$ROOT"/cases/ktproj-bidir/*/obj \
       "$ROOT"/cases/ktproj-avalonia/bin "$ROOT"/cases/ktproj-avalonia/obj

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
