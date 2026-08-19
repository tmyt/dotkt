#!/usr/bin/env bash
# Stateful MSBuild integration tests that cannot be expressed as independent NUnit fixtures. They reuse
# one obj/ tree across two builds and deliberately mutate/delete source files between those builds.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT_NAME=msbuild-tests
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the stateful MSBuild integration tests (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

fail=0
WORK="$ROOT/build/tests-msbuild"
mkdir -p "$WORK"
# Build the compiler launcher once (a plain Java app) so the MSBuild EnsureKotlinClrCompiler bootstrap is a no-op.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1

# ktproj_run <absolute-project> <stderr-logfile> — build+run a .ktproj; echo its noise-filtered stdout;
# RETURN the run's exit status (0 iff `dotnet run` — build AND execution — succeeded). Status and stdout are
# captured INDEPENDENTLY (issue #163): the process status is NOT lost to the grep pipe / `|| true` that used to
# mask a program which printed the expected text and THEN threw / returned non-zero.
ktproj_run() { # <project> <stderr-logfile>
	local proj="$1" log="$2" rc=0 raw
	raw="$(dotnet run --project "$proj" -v q --nologo 2>"$log")" || rc=$?
	printf '%s' "$raw" | grep -vE 'kotlin/clr:|duplicate source root' || true
	return $rc
}

# ---- issue #163 self-test: a .ktproj whose main prints the EXPECTED text then throws MUST be REJECTED. Drives the
# real ktproj_run capture path and asserts a non-zero status is observed; a green (exit 0) means the hole is open. ----
ktproj_selftest() {
	local d="$WORK/selftest"; rm -rf "$d"; mkdir -p "$d"
	cat > "$d/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
</Project>
KTPROJ
	printf 'fun main() { println("SELFTEST-EXPECTED"); throw RuntimeException("boom after print") }\n' > "$d/app.kt"
	local rc=0
	ktproj_run "$d/app.ktproj" "$d/run.err" >/dev/null || rc=$?
	rm -rf "$d"
	if (( rc == 0 )); then
		echo "KTPROJ GATE RED — #163 self-test FAILED: a print-then-crash .ktproj was accepted (exit-code hole open)"; exit 1
	fi
	echo "SELFTEST ktproj (print-then-crash correctly REJECTED, run exit $rc)"
}
ktproj_selftest


# Static PackageReference and bidirectional ProjectReference samples now run as NUnit tests under tests/interop and
# tests/roundtrip. This shell gate otherwise remains for stateful MSBuild behavior, plus process-boundary assertions
# (such as the synthesized suspend-main entry point) that cannot be expressed by an in-process NUnit fixture.

# #140: a genuinely-suspending main whose resumed body faults must surface the RAW exception. Task.Wait() wrapped it
# in AggregateException; GetAwaiter().GetResult() follows normal .NET await semantics. This must be a separate process:
# invoking the original suspend declaration via blockOn would bypass the compiler-synthesized plain main drain.
main_fault="$WORK/suspend-main-fault"
rm -rf "$main_fault"; mkdir -p "$main_fault"
cat > "$main_fault/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
</Project>
KTPROJ
cat > "$main_fault/App.kt" <<'KOTLIN'
import System.Threading.Tasks.Task

suspend fun main() {
    Task.Delay(1).await()
    throw IllegalStateException("async-main-boom")
}
KOTLIN
main_fault_rc=0
ktproj_run "$main_fault/app.ktproj" "$main_fault/run.err" >/dev/null || main_fault_rc=$?
if (( main_fault_rc != 0 )) \
    && grep -q 'System.InvalidOperationException: async-main-boom' "$main_fault/run.err" \
    && ! grep -q 'AggregateException' "$main_fault/run.err"; then
	echo "PASS  ktproj-suspend-main-raw-fault"
else
	echo "FAIL  ktproj-suspend-main-raw-fault (run exit $main_fault_rc; want raw InvalidOperationException, no AggregateException)"
	tail -20 "$main_fault/run.err" 2>/dev/null
	fail=1
fi
rm -rf "$main_fault"

# #50: INCREMENTAL deletion-safety + staleness through MSBuild. A single dir is built TWICE with the SAME obj/ (no
# clean) — the incremental path the shared targets guard. Between the builds a top-level `class Shape` is MOVED out of
# its own Shape.kt into App.kt and Shape.kt is DELETED. Pre-#50 the BIR was globbed from the BIR intermediate path, which was never
# cleaned, so the deleted Shape.kt left a stale Shape.bir.json behind → Shape was emitted TWICE (App.cir.json's moved
# copy + the orphan Shape.cir.json) → ilemit "type already defined" → the second build FAILED. The fix wipes
# the BIR path on every recompile, so the stale artifact cannot survive. This case reproduces that exact failure and
# asserts BOTH builds run "12" (the deleted source is gone from the emitted dll). The dir is generated + removed here
# (not a committed sample) because the assertion is a stateful two-build mutation, not a single `dotnet run`.
incr="$WORK/incremental-delete"
rm -rf "$incr"; mkdir -p "$incr"
cat > "$incr/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
</Project>
KTPROJ
# STATE 1: `class Shape` lives in its own file. (Both builds capture the run status independently — issue #163 —
# so a build/run that prints "12" then fails is not silently accepted.)
printf 'fun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\n' > "$incr/Shape.kt"
incr_rc1=0; incr1="$(ktproj_run "$incr/app.ktproj" "$incr/run1.err")" || incr_rc1=$?
# STATE 2: MOVE `class Shape` into App.kt and DELETE Shape.kt — rebuild on the SAME obj/ (incremental).
rm -f "$incr/Shape.kt"
printf 'class Shape(val w: Int, val h: Int) { fun area() = w * h }\nfun main() { println(Shape(3, 4).area()) }\n' > "$incr/App.kt"
incr_rc2=0; incr2="$(ktproj_run "$incr/app.ktproj" "$incr/run2.err")" || incr_rc2=$?
if [[ $incr_rc1 -eq 0 && $incr_rc2 -eq 0 && "$incr1" == "12" && "$incr2" == "12" ]]; then echo "PASS  ktproj-incr"; else
	echo "FAIL  ktproj-incr (build1 exit $incr_rc1, build2 exit $incr_rc2)"
	printf -- '--- build1 (want 12) ---\n%s\n--- build2 incremental after delete (want 12) ---\n%s\n--- stderr build2 ---\n%s\n' "$incr1" "$incr2" "$(tail -20 "$incr/run2.err" 2>/dev/null)"; fail=1
fi
rm -rf "$incr"

# #467: every compiler-produced intermediate follows $(IntermediateOutputPath), not the project-wide
# $(BaseIntermediateOutputPath). Build one project as Debug and Release CONCURRENTLY while each configuration selects
# a different DLL with the SAME assembly identity and a different Kotlin source. Shared BIR/CIR/KLIB/rsp state can
# therefore fail by deletion races, project the wrong reference surface, or silently reuse the other configuration's
# stamps; distinct directory spelling alone is not enough to pass this case. Then prove independent no-op state,
# Debug-only source rename/deletion safety, and configuration-scoped clean behavior.
config="$WORK/config-isolation"
rm -rf "$config"
mkdir -p "$config/ref-src" "$config/refs/debug" "$config/refs/release" "$config/app"
cat > "$config/ref-src/ConfigReference.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>ConfigReference</AssemblyName>
    <DefineConstants Condition="'$(ProbeDebug)' == 'true'">DEBUG_VARIANT</DefineConstants>
  </PropertyGroup>
</Project>
CSPROJ
cat > "$config/ref-src/ConfigApi.cs" <<'CSHARP'
namespace ConfigReference;
public static class ConfigApi
{
#if DEBUG_VARIANT
    public static int DebugValue() => 101;
#else
    public static int ReleaseValue() => 202;
#endif
}
CSHARP
dotnet build "$config/ref-src/ConfigReference.csproj" -c Release -o "$config/refs/debug" \
	-p:ProbeDebug=true -p:BaseIntermediateOutputPath="$config/refs/debug/obj/" -v q --nologo \
	>"$config/ref-debug.log" 2>&1 \
	|| die "config-isolation: Debug reference fixture failed to build — see build/tests-msbuild/config-isolation/ref-debug.log"
dotnet build "$config/ref-src/ConfigReference.csproj" -c Release -o "$config/refs/release" \
	-p:ProbeDebug=false -p:BaseIntermediateOutputPath="$config/refs/release/obj/" -v q --nologo \
	>"$config/ref-release.log" 2>&1 \
	|| die "config-isolation: Release reference fixture failed to build — see build/tests-msbuild/config-isolation/ref-release.log"

cat > "$config/app/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="ConfigReference" Condition="'\$(Configuration)' == 'Debug'">
      <HintPath>$config/refs/debug/ConfigReference.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="ConfigReference" Condition="'\$(Configuration)' == 'Release'">
      <HintPath>$config/refs/release/ConfigReference.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
  <ItemGroup Condition="'\$(Configuration)' == 'Debug'">
    <KotlinCompile Remove="ReleaseConfig.kt" />
  </ItemGroup>
  <ItemGroup Condition="'\$(Configuration)' == 'Release'">
    <KotlinCompile Remove="Debug*.kt" />
  </ItemGroup>
</Project>
KTPROJ
cat > "$config/app/DebugConfig.kt" <<'KOTLIN'
import ConfigReference.ConfigApi
fun selectedConfigValue(): Int = ConfigApi.DebugValue()
fun selectedConfigMarker(): String = "debug-config-marker"
KOTLIN
cat > "$config/app/ReleaseConfig.kt" <<'KOTLIN'
import ConfigReference.ConfigApi
fun selectedConfigValue(): Int = ConfigApi.ReleaseValue()
fun selectedConfigMarker(): String = "release-config-marker"
KOTLIN

dotnet restore "$config/app/app.ktproj" -v q --nologo >"$config/restore.log" 2>&1 \
	|| die "config-isolation: app restore failed — see build/tests-msbuild/config-isolation/restore.log"
cfg_debug_rc=0
cfg_release_rc=0
dotnet build "$config/app/app.ktproj" -c Debug --no-restore -v n --nologo >"$config/debug-build.log" 2>&1 &
cfg_debug_pid=$!
dotnet build "$config/app/app.ktproj" -c Release --no-restore -v n --nologo >"$config/release-build.log" 2>&1 &
cfg_release_pid=$!
wait "$cfg_debug_pid" || cfg_debug_rc=$?
wait "$cfg_release_pid" || cfg_release_rc=$?

cfg_debug_root="$config/app/obj/Debug/net10.0"
cfg_release_root="$config/app/obj/Release/net10.0"
config_msg=""
if (( cfg_debug_rc != 0 || cfg_release_rc != 0 )); then
	config_msg="concurrent builds failed (Debug=$cfg_debug_rc, Release=$cfg_release_rc)"
fi
for cfg_root in "$cfg_debug_root" "$cfg_release_root"; do
	if [[ -z "$config_msg" ]] && {
		[[ ! -f "$cfg_root/bir/.stamp" ]] || [[ ! -f "$cfg_root/cir/.stamp" ]] \
			|| [[ ! -f "$cfg_root/klib/ConfigReference.klib" ]] \
			|| [[ ! -f "$cfg_root/dotkt-reference-klibs.rsp" ]] \
			|| [[ ! -f "$cfg_root/dotkt-compile-options.txt" ]] \
			|| [[ ! -f "$cfg_root/bir/_DotKtPlaceholder.cs" ]];
	}; then
		config_msg="incomplete compiler state under ${cfg_root#"$ROOT/"}"
	fi
done
if [[ -z "$config_msg" ]] && {
	! grep -q 'DebugValue' "$cfg_debug_root/bir/DebugConfig.bir.json" \
		|| ! grep -q 'debug-config-marker' "$cfg_debug_root/bir/DebugConfig.bir.json" \
		|| ! grep -q 'ReleaseValue' "$cfg_release_root/bir/ReleaseConfig.bir.json" \
		|| ! grep -q 'release-config-marker' "$cfg_release_root/bir/ReleaseConfig.bir.json";
}; then
	config_msg="configuration-conditioned source/reference semantics crossed or disappeared"
fi
if [[ -z "$config_msg" ]] && { [[ -e "$config/app/obj/dotkt-bir" ]] \
	|| [[ -e "$config/app/obj/dotkt-cir" ]] || [[ -e "$config/app/obj/dotkt-reference-klibs" ]]; }; then
	config_msg="legacy BaseIntermediateOutputPath-rooted compiler state was created"
fi

if [[ -z "$config_msg" ]]; then
	dotnet build "$config/app/app.ktproj" -c Debug --no-restore -v n --nologo >"$config/debug-noop.log" 2>&1 \
		|| config_msg="Debug no-op rebuild failed"
fi
if [[ -z "$config_msg" ]]; then
	dotnet build "$config/app/app.ktproj" -c Release --no-restore -v n --nologo >"$config/release-noop.log" 2>&1 \
		|| config_msg="Release no-op rebuild failed"
fi
if [[ -z "$config_msg" ]] && grep -Eq 'DotKt: (compiling|lowering|emitting)' \
	"$config/debug-noop.log" "$config/release-noop.log"; then
	config_msg="a configuration-local no-op rebuild reran the compiler pipeline"
fi

if [[ -z "$config_msg" ]]; then
	mv "$config/app/DebugConfig.kt" "$config/app/DebugRenamed.kt"
	# `mv` preserves the old timestamp. Make the renamed source a real changed input, matching #50's deletion-safety
	# contract: once a recompile is required, that configuration's BIR directory is rebuilt without stale files.
	printf '\n' >> "$config/app/DebugRenamed.kt"
	dotnet build "$config/app/app.ktproj" -c Debug --no-restore -v n --nologo >"$config/debug-rename.log" 2>&1 \
		|| config_msg="Debug rebuild after source rename failed"
fi
if [[ -z "$config_msg" ]] && { [[ -e "$cfg_debug_root/bir/DebugConfig.bir.json" ]] \
	|| [[ ! -f "$cfg_debug_root/bir/DebugRenamed.bir.json" ]] \
	|| ! grep -q 'release-config-marker' "$cfg_release_root/bir/ReleaseConfig.bir.json"; }; then
	config_msg="Debug rename/deletion cleanup damaged Debug or Release BIR state"
fi

if [[ -z "$config_msg" ]]; then
	dotnet clean "$config/app/app.ktproj" -c Debug -v q --nologo >"$config/debug-clean.log" 2>&1 \
		|| config_msg="Debug clean failed"
fi
if [[ -z "$config_msg" ]] && { [[ -e "$cfg_debug_root/bir" ]] || [[ -e "$cfg_debug_root/cir" ]] \
	|| [[ -e "$cfg_debug_root/klib" ]] || [[ -e "$cfg_debug_root/dotkt-reference-klibs.rsp" ]] \
	|| [[ -e "$cfg_debug_root/dotkt-compile-options.txt" ]] \
	|| [[ ! -f "$cfg_release_root/bir/ReleaseConfig.bir.json" ]] \
	|| [[ ! -f "$cfg_release_root/cir/ReleaseConfig.cir.json" ]] \
	|| [[ ! -f "$cfg_release_root/klib/ConfigReference.klib" ]]; }; then
	config_msg="Debug clean removed the wrong configuration or left Debug compiler state behind"
fi

if [[ -z "$config_msg" ]]; then
	echo "PASS  ktproj-config-intermediate-isolation (concurrent Debug/Release, conditioned refs, no-op, rename, clean)"
else
	echo "FAIL  ktproj-config-intermediate-isolation: $config_msg"
	for config_log in debug-build release-build debug-noop release-noop debug-rename debug-clean; do
		[[ -f "$config/$config_log.log" ]] || continue
		printf -- '--- %s.log ---\n' "$config_log"
		grep -E 'DotKt:|error |Exception' "$config/$config_log.log" | tail -8 || true
	done
	fail=1
fi

# #192: CROSS-TARGET reference-asset selection (target RID != host RID), end to end through MSBuild.
# A RID-impl package ships ONE assembly identity as several physical files — a RID-neutral `lib/` placeholder plus
# `runtimes/<rid>/lib/` implementations — and ilemit must load the asset the TARGET runtime would use.
# DotKt.Toolchain.targets drives that by passing $(RuntimeIdentifier) / $(RuntimeIdentifierGraphPath) to the tool.
# The HOST-RID half of the contract is covered in-process by tests/interop; only a real `dotnet build -r <other-rid>`
# can cover the cross-target half, so it lives in this process-boundary gate.
#
# The probe package's RID-neutral placeholder deliberately OMITS the marker method that its `ref/` compile surface
# declares. #336 requires emission to remain contract-shaped regardless of that runtime implementation difference;
# the target-RID selection is observed through the runtime-catalog diagnostic, plus the emitted metadata is checked
# against the compile surface. Two probe assemblies cover both selection paths —
# DotKt.Tests.Rid.Exact ships an EXACT target-RID asset, DotKt.Tests.Rid.Family ships only the RID FAMILY asset
# (win / unix), which is reachable ONLY by walking the RID fallback chain (win-x64 -> win; linux-x64 -> unix).
#
# The RID-neutral file is packaged under a DIFFERENT FILE NAME (`*.Portable.dll`) on purpose: a RID-specific restore
# replaces a package's whole `lib/` runtime group with the matching `runtimes/<rid>/lib` group, and the SDK's
# copy-local conflict resolution is keyed on the output file name — so this is the only shape that hands the tool
# both candidates of one assembly identity while $(RuntimeIdentifier) is non-empty.
rid="$WORK/rid-crosstarget"
rm -rf "$rid"
mkdir -p "$rid/feed" "$rid/asm" "$rid/app"

# Derive the HOST RID and pick a target RID that genuinely differs, so the scenario is meaningful on any dev box.
# Every extraction below ends in `|| true`: under this script's `set -euo pipefail` a non-matching grep/sed pipeline
# would otherwise kill the whole gate mid-scenario instead of reaching the explicit diagnosis that follows it.
rid_host="$(dotnet --info 2>/dev/null | sed -n 's/^[[:space:]]*RID:[[:space:]]*//p' | head -1)" || true
if [[ -z "$rid_host" ]]; then
	case "$(uname -s)" in
		MINGW*|MSYS*|CYGWIN*|Windows_NT) rid_host=win-x64 ;;
		Darwin) rid_host=osx-x64 ;;
		*) rid_host=linux-x64 ;;
	esac
fi
if [[ "$rid_host" == win-* ]]; then rid_target=linux-x64; rid_family=unix; else rid_target=win-x64; rid_family=win; fi

# The four probe assemblies (see tests/msbuild/rid-probe/Probe.cs for the variant matrix). Each variant gets its own
# $(BaseIntermediateOutputPath): two of them share an assembly identity and differ only by a preprocessor symbol,
# which MSBuild's timestamp-based up-to-date check cannot see.
rid_probe() { # <out-name> <assembly-name> <family true|false> <full true|false>
	dotnet build "$ROOT/tests/msbuild/rid-probe/Probe.csproj" -c Release -o "$rid/asm/$1" \
		-p:ProbeAssemblyName="$2" -p:ProbeFamily="$3" -p:ProbeFull="$4" \
		-p:BaseIntermediateOutputPath="$rid/asm/$1/obj/" -v q --nologo >"$rid/asm-$1.log" 2>&1 \
		|| die "rid-crosstarget: probe assembly '$1' failed to build — see build/tests-msbuild/rid-crosstarget/asm-$1.log"
}
rid_probe exact-full     DotKt.Tests.Rid.Exact  false true
rid_probe exact-neutral  DotKt.Tests.Rid.Exact  false false
rid_probe family-full    DotKt.Tests.Rid.Family true  true
rid_probe family-neutral DotKt.Tests.Rid.Family true  false

# Pack the probe assets into a throwaway feed. Each argument is `<source dll>=><path inside the package>`.
rid_pack() { # <package-id> <src=>packagepath>...
	local id="$1" spec; shift
	local d="$rid/pack/$id"; mkdir -p "$d"
	{
		printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n'
		printf '    <TargetFramework>net10.0</TargetFramework>\n'
		printf '    <PackageId>%s</PackageId>\n    <Version>1.0.0</Version>\n' "$id"
		printf '    <Description>DotKt cross-target RID-asset gate fixture.</Description>\n'
		printf '    <IncludeBuildOutput>false</IncludeBuildOutput>\n'
		printf '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n'
		printf '    <NoWarn>NU5128;CS2008</NoWarn>\n'
		printf '  </PropertyGroup>\n  <ItemGroup>\n'
		for spec in "$@"; do
			printf '    <None Include="%s" Pack="true" PackagePath="%s" />\n' "${spec%%=>*}" "${spec#*=>}"
		done
		printf '  </ItemGroup>\n</Project>\n'
	} > "$d/p.csproj"
	dotnet pack "$d/p.csproj" -o "$rid/feed" -v q --nologo >"$rid/pack-$id.log" 2>&1 \
		|| die "rid-crosstarget: packing '$id' failed — see build/tests-msbuild/rid-crosstarget/pack-$id.log"
}
# The compile (`ref/`) surface declares the markers; the RID-neutral runtime (`lib/`) placeholders do not.
rid_pack DotKt.Tests.Rid.Neutral \
	"$rid/asm/exact-full/DotKt.Tests.Rid.Exact.dll=>ref/net10.0/DotKt.Tests.Rid.Exact.dll" \
	"$rid/asm/family-full/DotKt.Tests.Rid.Family.dll=>ref/net10.0/DotKt.Tests.Rid.Family.dll" \
	"$rid/asm/exact-neutral/DotKt.Tests.Rid.Exact.dll=>lib/net10.0/DotKt.Tests.Rid.Exact.Portable.dll" \
	"$rid/asm/family-neutral/DotKt.Tests.Rid.Family.dll=>lib/net10.0/DotKt.Tests.Rid.Family.Portable.dll"
rid_pack DotKt.Tests.Rid.ExactRids \
	"$rid/asm/exact-full/DotKt.Tests.Rid.Exact.dll=>runtimes/win-x64/lib/net10.0/DotKt.Tests.Rid.Exact.dll" \
	"$rid/asm/exact-full/DotKt.Tests.Rid.Exact.dll=>runtimes/linux-x64/lib/net10.0/DotKt.Tests.Rid.Exact.dll"
rid_pack DotKt.Tests.Rid.FamilyRids \
	"$rid/asm/family-full/DotKt.Tests.Rid.Family.dll=>runtimes/win/lib/net10.0/DotKt.Tests.Rid.Family.dll" \
	"$rid/asm/family-full/DotKt.Tests.Rid.Family.dll=>runtimes/unix/lib/net10.0/DotKt.Tests.Rid.Family.dll"

# An isolated feed + package cache: the fixture packages keep version 1.0.0 across runs, so a shared cache would
# serve a previous run's content.
cat > "$rid/app/nuget.config" <<CFG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="rid-gate" value="$rid/feed" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="$rid/pkgcache" />
  </config>
</configuration>
CFG
# A LIBRARY: a foreign-RID executable would pull the target's apphost pack and could not be run here anyway.
# CopyLocalLockFileAssemblies puts the package's runtime assets on @(ReferenceCopyLocalPaths), which is ilemit's
# runtime reference set.
cat > "$rid/app/app.ktproj" <<KTPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DotKt.Tests.Rid.Neutral" Version="1.0.0" />
    <PackageReference Include="DotKt.Tests.Rid.ExactRids" Version="1.0.0" />
    <PackageReference Include="DotKt.Tests.Rid.FamilyRids" Version="1.0.0" />
  </ItemGroup>
  <Import Project="$ROOT/eng/KotlinClr.targets" />
</Project>
KTPROJ
cat > "$rid/app/App.kt" <<'KOTLIN'
import DotKt.Tests.Rid.ExactRidProbe
import DotKt.Tests.Rid.FamilyRidProbe

// Both markers exist ONLY in the runtimes/<rid>/lib assets, never in the RID-neutral placeholder.
fun exactMarker(): String = ExactRidProbe.TargetOnlyMarker()
fun familyMarker(): String = FamilyRidProbe.FamilyOnlyMarker()
KOTLIN

# -v n so the ilemit Exec command line is logged: the two replays below re-run it with one argument changed.
rid_rc=0
dotnet build "$rid/app/app.ktproj" -r "$rid_target" -v n --nologo >"$rid/build.log" 2>&1 || rid_rc=$?
rid_asm="$rid/app/bin/Debug/net10.0/$rid_target/app.dll"
rid_intermediate="$rid/app/obj/Debug/net10.0/$rid_target"
rid_msg=""
if (( rid_rc != 0 )); then
	rid_msg="the -r $rid_target build failed (exit $rid_rc)"
elif [[ ! -f "$rid_intermediate/bir/.stamp" || ! -f "$rid_intermediate/cir/.stamp" \
	|| ! -d "$rid_intermediate/klib" || ! -f "$rid_intermediate/dotkt-reference-klibs.rsp" \
	|| ! -f "$rid_intermediate/dotkt-compile-options.txt" ]]; then
	rid_msg="compiler intermediates are not rooted under the RID-specific IntermediateOutputPath"
elif ! grep -qF "ilemit runtime: 'DotKt.Tests.Rid.Exact' has 2 RID builds; selected runtimes/$rid_target/lib asset for target $rid_target" "$rid/build.log"; then
	# Also guards the fixture itself: a group that degenerated to one candidate would let the build pass vacuously.
	rid_msg="ilemit did not report selecting the runtimes/$rid_target/lib asset of DotKt.Tests.Rid.Exact out of 2 candidates"
elif ! grep -qF "ilemit runtime: 'DotKt.Tests.Rid.Family' has 2 RID builds; selected runtimes/$rid_family/lib asset for target $rid_target" "$rid/build.log"; then
	rid_msg="ilemit did not fall back through the RID graph to the runtimes/$rid_family/lib asset of DotKt.Tests.Rid.Family for target $rid_target"
elif [[ ! -f "$rid_asm" ]]; then
	rid_msg="no emitted assembly at ${rid_asm#$ROOT/}"
elif ! LC_ALL=C grep -qa 'TargetOnlyMarker' "$rid_asm" || ! LC_ALL=C grep -qa 'FamilyOnlyMarker' "$rid_asm"; then
	# Confirm in the OUTPUT, not just in the build's exit status, that the emit linked both RID-asset-only members.
	# The member identities come from the compile contract; runtime selection is asserted independently above.
	rid_msg="the emitted assembly does not reference the RID-asset-only marker members"
fi

# Two replays of the emit step. Both re-run the ilemit invocation the build just logged, with one argument changed;
# the CIR and the resolved reference set stay exactly what MSBuild produced, so neither needs a second build.
rid_cmd=""
if [[ -z "$rid_msg" ]]; then
	rid_cmd="$(grep -oE 'dotnet "[^"]*ilemit\.dll".*' "$rid/build.log" | head -1)" || true
	[[ -n "$rid_cmd" ]] || rid_msg="could not recover the ilemit command line from the build log"
fi
if [[ -z "$rid_msg" ]]; then
	compile_refs_arg="$(grep -oE -- '--compile-refs "[^"]+"' <<<"$rid_cmd" | head -1)" || true
	[[ -n "$compile_refs_arg" ]] \
		|| rid_msg="the ilemit invocation does not carry the non-empty MSBuild compile-reference universe"
fi
rid_replay() { # <log-name> <literal argument to replace (non-empty)> <replacement>; echoes the exit status
	local from="$2" to="$3" pat cmd rc=0
	# Bash replacement patterns are globs: escape metacharacters so Windows paths (with backslashes) match literally.
	pat="$from"
	pat="${pat//\\/\\\\}"
	pat="${pat//\*/\\*}"
	pat="${pat//\?/\\?}"
	pat="${pat//\[/\\[}"
	pat="${pat//\]/\\]}"
	cmd="${rid_cmd//$pat/$to}"
	[[ "$cmd" != "$rid_cmd" ]] || { echo "substitution-failed"; return; }
	( cd "$rid/app" && eval "$cmd" ) >"$rid/$1.log" 2>&1 || rc=$?
	echo "$rc"
}

# REPLAY 1 — the BUILT-IN fallback chain. With no usable $(RuntimeIdentifierGraphPath) the tool degrades to its
# hard-coded family chain instead of the portable graph's #import closure; that last-resort path must still reach the
# family asset, so the same two selections (and a successful emit) must come out.
if [[ -z "$rid_msg" ]]; then
	rid_graph_arg="$(grep -oE -- '--rid-graph-path "[^"]*"' <<<"$rid_cmd" | head -1)" || true
	rid_bi_rc=substitution-failed
	[[ -z "$rid_graph_arg" ]] \
		|| rid_bi_rc="$(rid_replay builtin-chain "$rid_graph_arg" "--rid-graph-path \"$rid/absent-rid-graph.json\"")"
	if [[ "$rid_bi_rc" == "substitution-failed" ]]; then
		rid_msg="the ilemit invocation no longer carries --rid-graph-path — the MSBuild RID-graph plumbing is gone"
	elif [[ "$rid_bi_rc" != "0" ]]; then
		rid_msg="the built-in RID fallback chain (no RID graph on disk) did not emit (exit $rid_bi_rc)"
	elif ! grep -qF "selected runtimes/$rid_target/lib asset for target $rid_target" "$rid/builtin-chain.log" \
		|| ! grep -qF "selected runtimes/$rid_family/lib asset for target $rid_target" "$rid/builtin-chain.log"; then
		rid_msg="the built-in RID fallback chain did not reach the same assets as the portable RID graph"
	fi
fi

# REPLAY 2 — NEGATIVE CONTROL: the same emit at the HOST RID (what a lost target-RID flow degrades to). Both runtime
# groups must then select the RID-neutral placeholder. Emission still SUCCEEDS because #336 makes the compile contract
# the sole source of type/member meaning; a runtime implementation may not remove a member from that universe. The
# changed selection diagnostic is therefore the discriminating fact, while the output remains contract-shaped.
if [[ -z "$rid_msg" ]]; then
	rid_neg_rc="$(rid_replay negative "--target-rid \"$rid_target\"" "--target-rid \"$rid_host\"")"
	if [[ "$rid_neg_rc" == "substitution-failed" ]]; then
		rid_msg="the ilemit invocation no longer carries --target-rid \"$rid_target\" — the MSBuild target-RID plumbing is gone"
	elif [[ "$rid_neg_rc" != "0" ]]; then
		rid_msg="negative control failed to emit against the unchanged compile contract at host RID $rid_host (exit $rid_neg_rc)"
	elif ! grep -qF "selected RID-neutral lib asset for target $rid_host" "$rid/negative.log"; then
		rid_msg="negative control did not select the RID-neutral runtime placeholder for host RID $rid_host"
	fi
fi

if [[ -z "$rid_msg" ]]; then
	echo "PASS  ktproj-crosstarget-rid-assets (host $rid_host -> target $rid_target; exact + $rid_family family fallback)"
else
	echo "FAIL  ktproj-crosstarget-rid-assets (host $rid_host -> target $rid_target): $rid_msg"
	# -v n makes the raw build log enormous; show only the tool diagnostics and errors. Full logs stay in
	# build/tests-msbuild/rid-crosstarget/{build,builtin-chain,negative}.log.
	for rid_log in build builtin-chain negative; do
		[[ -f "$rid/$rid_log.log" ]] || continue
		printf -- '--- %s.log ---\n' "$rid_log"
		grep -E 'ilemit:|error ' "$rid/$rid_log.log" | cut -c1-300 | head -8 || true
	done
	fail=1
fi

# Clean each sample's build output.
echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
