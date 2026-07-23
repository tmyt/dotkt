#!/usr/bin/env bash
# The PACKAGED-SDK gate: the ONLY gate that exercises the shipping nupkg-resolution path — a project that
# resolves `DotKt.Sdk` / `DotKt.Sdk.Mpp` + the implicit `DotKt.Toolchain` / `DotKt.Stdlib` PackageReferences
# from real .nupkgs in a NuGet feed. `tests/msbuild/run.sh` uses the IN-REPO dev entry (eng/KotlinClr.targets,
# hard-coded tool paths) and never restores a nupkg, so packaging-only bugs slip past it — 0.9.5 shipped
# broken twice for exactly this reason (#131 stale SDK version, #132 a Library's non-copy-local reference
# never reaching bir2cir/ilemit). This suite packs the 5 nupkgs to a local feed and drives FIVE isolated
# scenarios through `dotnet build`/`dotnet run` from that feed only:
#   exe      — a plain `Sdk="DotKt.Sdk"` Exe: build + RUN, assert stdout.
#   library  — a `Library` that PackageReferences a SECOND DotKt library (packed as its own nupkg) and calls
#              into it. A package runtime dll is NOT copy-local for OutputType=Library, so under the old
#              copy-local-glob targets that reference never reached ilemit -> the emit FAILED (#132-general).
#              This is the case the gate exists to hold: build succeeds + the emitted dll carries the call.
#   mpp      — a `Sdk="DotKt.Sdk.Mpp"` multiplatform Exe (common `expect` + clr `actual`): the MPP SDK path
#              end-to-end through nupkg resolution. build + RUN, assert stdout.
#   template — install DotKt.Templates, scaffold the CLI template, then build + RUN it.
#   mpp-template — scaffold the MPP template and verify both SDK pins before build + RUN.
#
# Isolation (defeats the cache-masking landmine): a per-run nuget.config with <clear/> + the local feed ONLY,
# and an isolated <globalPackagesFolder> under the scratch dir — a stale published 0.9.5 in the user's
# ~/.nuget cache can never mask the freshly-packed one, and the user's global cache is never touched. Green =
# every fail name is in the XFAIL_PKG baseline below (exit 0); any name outside it prints NEW-FAIL, exit 1.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT_NAME=packaged-sdk-tests
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Packs the 5 nupkgs to build/nuget-feed and drives 5 packaged SDK/template scenarios from that feed only.
Green (exit 0) = no fail name outside the XFAIL_PKG baseline declared in this script.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

# The authoritative XFAIL baseline (fail name -> reason). Empty: all five packaged cases must pass. A listed
# name that starts passing prints "FIXED — remove it" WITHOUT reddening the gate; any name NOT listed that
# fails prints NEW-FAIL and reddens. Computed by lib.sh xfail_diff at the bottom.
declare -A XFAIL_PKG=(
)

# The package version the pack stamps (single-sourced in DotKt.Versions.props). The Sdk="DotKt.Sdk/$VER"
# reference, the second library's PackageReference, and the MPP global.json all pin THIS version, so a
# version skew between the props and the SDK's embedded DotKtVersion (the #131 class of bug) surfaces here as
# a restore failure rather than a silent stale-toolchain pull.
VER_PREFIX="$(grep -oP '<DotKtVersionPrefix>\K[^<]+' "$ROOT/packaging/DotKt.Versions.props")"
VER_SUFFIX="$(grep -oP '<DotKtVersionSuffix>\K[^<]*' "$ROOT/packaging/DotKt.Versions.props")"
VER="$VER_PREFIX${VER_SUFFIX:+-$VER_SUFFIX}"
[[ -n "$VER" ]] || die "could not read DotKtVersionPrefix from packaging/DotKt.Versions.props"
FEED="$ROOT/build/nuget-feed"

# 1. Pack the 5 nupkgs FRESH from the current sources (rebuilds the tools + re-copies the shipped targets into
#    DotKt.Toolchain). Uses the cached stdlib dlls when present — correct for an MSBuild/targets-only change;
#    a stdlib/kotc/bir2cir source change is gated by verify-tests, not here.
info "packing 5 nupkgs (version $VER) -> $FEED"
bash "$ROOT/scripts/pack-nuget.sh" >/dev/null || die "pack-nuget.sh failed"
# #223: immediately repeat the standalone pack. Tool builds may refresh output mtimes, but a content-stable
# toolchain must reuse the just-baked frontend KLIB and stdlib pair. The helper also proves that a real same-size
# content change invalidates the new fingerprint, so idempotency does not weaken stale-artifact protection.
bash "$ROOT/tests/packaged-sdk/verify-pack-idempotency.sh"

# 2. Scratch workspace: an isolated globalPackagesFolder + a local-only feed, so restore can ONLY see the
#    freshly-packed nupkgs (no cache masking, no touching the user's ~/.nuget).
WS="$ROOT/build/verify-packaged-sdk"
rm -rf "$WS"; mkdir -p "$WS/pkgs"
RESULTS="$WS/results"; mkdir -p "$RESULTS"
NUGET_CONFIG="$WS/nuget.config"
cat > "$NUGET_CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$WS/pkgs" />
  </config>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
  </packageSources>
</configuration>
EOF

# One atomic result record per case + a fail-name list for the verdict.
declare -a FAILS=()
pass() { # <name>
	echo "PASS  pkgsdk:$1"; echo "PASS  pkgsdk:$1" > "$RESULTS/$1.tmp"; mv -f "$RESULTS/$1.tmp" "$RESULTS/$1"
}
fail() { # <name> <reason> [detail]
	local detail="${3:-}"
	echo "FAIL  pkgsdk:$1 ($2)"
	[[ -n "$detail" ]] && printf '%s\n' "$detail"
	{ echo "FAIL  pkgsdk:$1 ($2)"; [[ -n "$detail" ]] && printf '%s\n' "$detail"; } > "$RESULTS/$1.tmp"; mv -f "$RESULTS/$1.tmp" "$RESULTS/$1"
	FAILS+=("$1")
}
# Strip the compiler's own chatter from a run's stdout so the assert compares only program output.
run_out() { grep -vE 'kotlin/clr:|duplicate source root' || true; }

# run_project <dir> <stderr-logfile> — build+run a packaged project in <dir>; echo its noise-filtered program
# stdout; RETURN the run's exit status (0 iff build AND execution succeeded). Build/restore output is kept out of
# the captured program stdout: on a clean GitHub runner NuGet can print certificate and package-install messages
# to stdout even at quiet verbosity. Status and stdout are captured independently (issue #163), so a project
# which prints the expected text and THEN throws / returns non-zero remains rejected.
run_project() { # <dir> <stderr-logfile>
	local dir="$1" log="$2" build_log="${2%.err}.build.log"
	if ! (cd "$dir" && dotnet build -v q --nologo >"$build_log" 2>&1); then
		cp "$build_log" "$log"
		return 1
	fi
	local rc=0 raw
	raw="$(cd "$dir" && dotnet run --no-build --no-restore -v q 2>"$log")" || rc=$?
	printf '%s' "$raw" | run_out
	return $rc
}

# A tiny metadata-only reflection checker (does the emitted dll declare owner.member?) — built ONCE with the
# DEFAULT NuGet config (it needs System.Reflection.MetadataLoadContext from nuget.org; it is a build-time
# tool, NOT part of the isolated SDK-resolution test). Lives OUTSIDE $WS so the isolated local-only
# nuget.config there does not govern its restore, and is cached across runs.
REFCHECK="$ROOT/build/verify-packaged-sdk-tool"
build_refcheck() {
	mkdir -p "$REFCHECK/src"
	cat > "$REFCHECK/src/refcheck.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><AssemblyName>refcheck</AssemblyName></PropertyGroup>
  <ItemGroup><PackageReference Include="System.Reflection.MetadataLoadContext" Version="9.0.0" /></ItemGroup>
</Project>
EOF
	cat > "$REFCHECK/src/Program.cs" <<'EOF'
using System; using System.Linq; using System.Reflection;
// refcheck <dll> <ownerFqn> <memberName> [exactRefs] -> exit 0 iff the dll declares the requested member.
class P {
    static int Main(string[] a) {
        var dll = System.IO.Path.GetFullPath(a[0]);
        // The TPA list is the runtime host's resolved platform set. Non-platform dependencies are explicit; never
        // turn the input assembly's parent directory into an implicit reference universe.
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (a.Length > 3) paths.AddRange(a[3].Split(';', StringSplitOptions.RemoveEmptyEntries));
        paths.Add(dll);
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Distinct()));
        var asm = mlc.LoadFromAssemblyPath(dll);
        Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); }
        var owner = ts.FirstOrDefault(t => t.FullName == a[1]);
        if (owner == null) { Console.Error.WriteLine($"refcheck: type {a[1]} not found"); return 1; }
        var m = owner.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .FirstOrDefault(x => x.Name == a[2]);
        if (m == null) { Console.Error.WriteLine($"refcheck: {a[1]}.{a[2]} not found"); return 1; }
        return 0;
    }
}
EOF
	dotnet build "$REFCHECK/src" -c Release -o "$REFCHECK/bin" -v q --nologo >/dev/null 2>&1 || return 1
}

if [[ -f "$REFCHECK/bin/refcheck.dll" ]]; then
	info "reflection checker cached"
else
	info "building reflection checker"
	build_refcheck || warn "refcheck build failed — library case will assert build-success only"
fi

# ---------------------------------------------------------------------------------------------------------
# Case: exe — a plain packaged Exe, build + run.
# ---------------------------------------------------------------------------------------------------------
case_exe() {
	local d="$WS/exe"; mkdir -p "$d"; cp "$NUGET_CONFIG" "$d/nuget.config"
	cat > "$d/App.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
EOF
	cat > "$d/app.kt" <<'EOF'
fun main() { println("packaged exe ok: " + (2 + 3)) }
EOF
	local expected="packaged exe ok: 5" actual rc=0
	actual="$(run_project "$d" "$d/run.err")" || rc=$?
	if (( rc != 0 )); then fail exe "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$d/run.err" 2>/dev/null)")"
	elif [[ "$actual" == "$expected" ]]; then pass exe
	else fail exe "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---------------------------------------------------------------------------------------------------------
# Case: library — the #132-general reproducer. A `Library` that PackageReferences a SECOND DotKt library
# (packed as its own nupkg) and calls into it. The second library's runtime dll is NOT copy-local for a
# Library, so the OLD copy-local-glob targets starved ilemit of it and the emit FAILED. Under the general
# @(ReferencePath) rule it flows through. Assert: consumer builds AND the emitted dll declares the call.
# ---------------------------------------------------------------------------------------------------------
case_library() {
	# (a) build the second DotKt library.
	local lib="$WS/lib"; mkdir -p "$lib"; cp "$NUGET_CONFIG" "$lib/nuget.config"
	cat > "$lib/MyDotKtLib.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
EOF
	cat > "$lib/lib.kt" <<'EOF'
package mylib
fun libValue(): Int = 42
class Doubler { fun twice(n: Int): Int = n * 2 }
EOF
	if ! (cd "$lib" && dotnet build -v q --nologo >"$lib/build.log" 2>&1); then
		fail library "second-library build failed" "$(tail -20 "$lib/build.log")"; return
	fi
	local libdll; libdll="$(find "$lib/bin" -name 'MyDotKtLib.dll' | head -1)"
	[[ -f "$libdll" ]] || { fail library "second-library dll not emitted"; return; }

	# (b) pack the emitted dll as a NuGet package into the feed (build-time only, default config is fine).
	local pw="$WS/packwrap"; mkdir -p "$pw"
	cat > "$pw/PackWrap.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <PackageId>MyDotKtLib</PackageId>
    <Version>$VER</Version>
    <NoWarn>NU5128;NU5127</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <None Include="$libdll" Pack="true" PackagePath="lib/net10.0/" />
  </ItemGroup>
</Project>
EOF
	if ! (cd "$pw" && dotnet pack -o "$FEED" -v q --nologo >"$pw/pack.log" 2>&1); then
		fail library "packing the second library failed" "$(tail -20 "$pw/pack.log")"; return
	fi
	# The consumer restores it fresh into the isolated packages folder (nothing cached there yet).
	rm -rf "$WS/pkgs/mydotktlib"

	# (c) the consuming Library — PackageReference into the packed second library.
	local con="$WS/consumer"; mkdir -p "$con"; cp "$NUGET_CONFIG" "$con/nuget.config"
	cat > "$con/Consumer.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MyDotKtLib" Version="$VER" />
  </ItemGroup>
</Project>
EOF
	cat > "$con/consumer.kt" <<'EOF'
package consumer
import mylib.libValue
import mylib.Doubler
fun compute(): Int = libValue() + Doubler().twice(10)
EOF
	if ! (cd "$con" && dotnet build -v q --nologo >"$con/build.log" 2>&1); then
		fail library "consumer Library build failed (the #132-general gap)" "$(tail -25 "$con/build.log")"; return
	fi
	local condll; condll="$(find "$con/bin" -name 'Consumer.dll' | head -1)"
	[[ -f "$condll" ]] || { fail library "consumer dll not emitted"; return; }
	# The emitted dll must declare the call — proves the cross-package reference resolved through bir2cir+ilemit.
	if [[ -x "$REFCHECK/bin/refcheck" || -f "$REFCHECK/bin/refcheck.dll" ]]; then
		if ! dotnet "$REFCHECK/bin/refcheck.dll" "$condll" "consumer.ConsumerKt" "compute" "$libdll" >"$con/refcheck.log" 2>&1; then
			fail library "emitted Consumer.dll missing consumer.ConsumerKt.compute" "$(cat "$con/refcheck.log")"; return
		fi
	fi
	pass library
}

# ---------------------------------------------------------------------------------------------------------
# Case: mpp — a packaged MULTIPLATFORM Exe via Sdk="DotKt.Sdk.Mpp" (common `expect` + clr `actual`). The Mpp
# SDK nests a version-LESS import of the base DotKt.Sdk, whose version the NuGet SDK resolver reads ONLY from
# global.json's msbuild-sdks — so a global.json pinning both is REQUIRED (and part of what this covers).
# ---------------------------------------------------------------------------------------------------------
case_mpp() {
	local d="$WS/mpp"; mkdir -p "$d/common" "$d/clr"; cp "$NUGET_CONFIG" "$d/nuget.config"
	cat > "$d/global.json" <<EOF
{ "msbuild-sdks": { "DotKt.Sdk.Mpp": "$VER", "DotKt.Sdk": "$VER" } }
EOF
	cat > "$d/App.ktproj" <<'EOF'
<Project Sdk="DotKt.Sdk.Mpp">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
EOF
	cat > "$d/common/Greeter.kt" <<'EOF'
package mpp.greeter
expect class Greeter { fun say(): String }
EOF
	cat > "$d/clr/Greeter.kt" <<'EOF'
package mpp.greeter
actual class Greeter { actual fun say(): String = "Hello from the CLR actual (packaged MPP SDK)" }
EOF
	cat > "$d/clr/Main.kt" <<'EOF'
package mpp.greeter
fun main() { println(Greeter().say()) }
EOF
	local expected="Hello from the CLR actual (packaged MPP SDK)" actual rc=0
	actual="$(run_project "$d" "$d/run.err")" || rc=$?
	if (( rc != 0 )); then fail mpp "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$d/run.err" 2>/dev/null)")"
	elif [[ "$actual" == "$expected" ]]; then pass mpp
	else fail mpp "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---------------------------------------------------------------------------------------------------------
# Case: template — the #53 reproducer. Install the packed DotKt.Templates via `dotnet new install`, scaffold a
# project with `dotnet new dotkt-cli`, and build+run it from the isolated feed. A stale template Sdk pin (the
# 0.9.5-while-release-is-0.9.6 drift) makes restore pull a version the feed does not carry -> this fails. The
# generated project file must pin the RELEASE version (proves the pack-time Sdk-version substitution worked).
# NB `dotnet new install/uninstall` touches the machine-global template store (unavoidable for `dotnet new`);
# installed from the exact packed nupkg with --force and uninstalled in a trap so a failure still cleans up.
# ---------------------------------------------------------------------------------------------------------
case_template() {
	local d="$WS/template"; mkdir -p "$d"
	local nupkg; nupkg="$(find "$FEED" -maxdepth 1 -name "DotKt.Templates.$VER.nupkg" | head -1)"
	[[ -f "$nupkg" ]] || { fail template "DotKt.Templates.$VER.nupkg not packed"; return; }
	# Uninstall on any exit from this case so the global template store is never left dirty.
	trap 'dotnet new uninstall DotKt.Templates >/dev/null 2>&1 || true' RETURN
	if ! dotnet new install "$nupkg" --force >"$d/install.log" 2>&1; then
		fail template "dotnet new install failed" "$(tail -20 "$d/install.log")"; return
	fi
	local proj="$d/hello"; rm -rf "$proj"
	if ! dotnet new dotkt-cli -o "$proj" >"$d/new.log" 2>&1; then
		fail template "dotnet new dotkt-cli failed" "$(tail -20 "$d/new.log")"; return
	fi
	cp "$NUGET_CONFIG" "$proj/nuget.config"
	# The scaffolded project must pin the release SDK version (the #53 drift shipped it pinned to a stale one).
	if ! grep -q "Sdk=\"DotKt.Sdk/$VER\"" "$proj"/*.csproj 2>/dev/null; then
		fail template "generated project does not pin DotKt.Sdk/$VER" "$(cat "$proj"/*.csproj 2>/dev/null)"; return
	fi
	local expected="Hello, World, from DotKt — Kotlin on .NET!" actual rc=0
	actual="$(run_project "$proj" "$proj/run.err")" || rc=$?
	if (( rc != 0 )); then fail template "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$proj/run.err" 2>/dev/null)")"
	elif [[ "$actual" == "$expected" ]]; then pass template
	else fail template "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---------------------------------------------------------------------------------------------------------
# Case: mpp-template — the #133 reproducer. Install DotKt.Templates, scaffold `dotnet new dotkt-mpp`, and
# build+run it WITHOUT hand-writing a global.json. The MPP SDK nests a version-LESS import of the base
# DotKt.Sdk whose version the NuGet resolver reads ONLY from global.json — so before #133 a scaffolded MPP
# project failed to resolve the nested SDK. The template now ships that global.json (pinning both SDKs to the
# release version, substituted at pack), so this must build + run out of the box. Distinct from case_mpp
# (which hand-writes the global.json): this proves the SHIPPED template carries it.
# ---------------------------------------------------------------------------------------------------------
case_mpp_template() {
	local d="$WS/mpp-template"; mkdir -p "$d"
	local nupkg; nupkg="$(find "$FEED" -maxdepth 1 -name "DotKt.Templates.$VER.nupkg" | head -1)"
	[[ -f "$nupkg" ]] || { fail mpp-template "DotKt.Templates.$VER.nupkg not packed"; return; }
	trap 'dotnet new uninstall DotKt.Templates >/dev/null 2>&1 || true' RETURN
	if ! dotnet new install "$nupkg" --force >"$d/install.log" 2>&1; then
		fail mpp-template "dotnet new install failed" "$(tail -20 "$d/install.log")"; return
	fi
	local proj="$d/hello-mpp"; rm -rf "$proj"
	if ! dotnet new dotkt-mpp -o "$proj" >"$d/new.log" 2>&1; then
		fail mpp-template "dotnet new dotkt-mpp failed" "$(tail -20 "$d/new.log")"; return
	fi
	cp "$NUGET_CONFIG" "$proj/nuget.config"
	# The scaffolded MPP project must ship the global.json pinning both SDKs to the release version (the #133 fix).
	if ! grep -q "\"DotKt.Sdk.Mpp\": \"$VER\"" "$proj/global.json" 2>/dev/null || ! grep -q "\"DotKt.Sdk\": \"$VER\"" "$proj/global.json" 2>/dev/null; then
		fail mpp-template "scaffolded global.json does not pin both SDKs to $VER" "$(cat "$proj/global.json" 2>/dev/null)"; return
	fi
	local expected="Hello, World, from a DotKt multiplatform app on .NET!" actual rc=0
	actual="$(run_project "$proj" "$proj/run.err")" || rc=$?
	if (( rc != 0 )); then fail mpp-template "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$proj/run.err" 2>/dev/null)")"
	elif [[ "$actual" == "$expected" ]]; then pass mpp-template
	else fail mpp-template "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---- issue #163 self-test: a packaged Exe whose main prints the EXPECTED text then throws MUST be REJECTED.
# Drives the real run_project capture path from the isolated feed and asserts a non-zero status is observed. ----
selftest() {
	local d="$WS/selftest"; mkdir -p "$d"; cp "$NUGET_CONFIG" "$d/nuget.config"
	cat > "$d/App.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
</Project>
EOF
	printf 'fun main() { println("SELFTEST-EXPECTED"); throw RuntimeException("boom after print") }\n' > "$d/app.kt"
	local rc=0
	run_project "$d" "$d/run.err" >/dev/null || rc=$?
	if (( rc == 0 )); then
		echo "PACKAGED-SDK GATE RED — #163 self-test FAILED: a print-then-crash packaged exe was accepted (exit-code hole open)"; exit 1
	fi
	info "self-test OK: a print-then-crash packaged exe is REJECTED (run exit $rc)"
}
selftest

case_exe
case_library
case_mpp
case_template
case_mpp_template

echo "------------------------------------"
xfail_diff pkgsdk XFAIL_PKG "${FAILS[@]}"
if (( ${#XFAIL_NEW[@]} == 0 )); then echo "PACKAGED-SDK OK"; else echo "PACKAGED-SDK FAIL"; exit 1; fi
