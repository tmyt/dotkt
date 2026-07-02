#!/usr/bin/env bash
# Build the FOUR DotKt NuGet packages into the local feed (build/nuget-feed):
#   DotKt.Sdk       — the MSBuild SDK (Sdk.props/targets; implicit refs to Toolchain + Stdlib)
#   DotKt.Toolchain — the compiler: kotc + bir2cir + ilemit + facadegen + retarget + the CLR frontend
#                     stdlib jar + the COMPILE-TIME stdlib reference assembly (tools/stdlib/DotKt.Private.Stdlib.dll)
#   DotKt.Stdlib    — the RUNTIME stdlib assembly (lib/net10.0/DotKt.Stdlib.dll, copy-local)
#   DotKt.Templates — `dotnet new` templates
# (DotKt.Runtime is RETIRED — no such package exists or is referenced.)
# Version is single-sourced in packaging/DotKt.Versions.props (imported by every pack .csproj).
# Orchestrated by `make pack` (which builds the prerequisites first); standalone it builds what's missing.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
FEED="$ROOT/build/nuget-feed"; rm -rf "$FEED"; mkdir -p "$FEED"

echo "== build compiler (installDist) + tools =="
( cd "$ROOT" && ./gradlew -q :kotc:installDist )
dotnet build "$ROOT/toolchain/ilemit"    -c Release -o "$ROOT/build/ilemit-bin"    -v q --nologo
dotnet build "$ROOT/toolchain/bir2cir"   -c Release -o "$ROOT/build/bir2cir-bin"   -v q --nologo
dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo
dotnet build "$ROOT/toolchain/retarget"  -c Release -o "$ROOT/build/retarget-bin"  -v q --nologo

# The CLR FRONTEND stdlib jar (kotc -classpath): built FROM our CLR stdlib sources, REPLACING the JVM kotlin-stdlib.jar
# whose java.util.* typealiases leaked into the frontend. Consumes the kotc install lib/*.jar produced by installDist.
echo "== build CLR frontend stdlib jar =="
FE_JAR="$ROOT/build/clr-stdlib-frontend-jvm/kotlin-stdlib-clr-frontend.jar"
[[ -f "$FE_JAR" ]] || bash "$ROOT/scripts/build-stdlib-jar.sh"

# The CLR stdlib dll pair — REQUIRED package contents (the shipped DotKt.Toolchain.targets needs both: the ref feeds
# bir2cir's @ClrTypeAlias/@ClrIntrinsic substitution, the rt is the app's copy-local runtime). Build if missing.
# NOTE `|| true` on the rt build: its trailing error-grep exits 1 exactly when the build is CLEAN (no errors found);
# the existence checks below are the real gate.
echo "== ensure CLR stdlib (ref + rt) =="
STDLIB_REF="$ROOT/build/clr-stdlib/dll/DotKt.Private.Stdlib.dll"
STDLIB_RT="$ROOT/build/clr-stdlib-rt/dll/DotKt.Stdlib.dll"
[[ -f "$STDLIB_REF" ]] || bash "$ROOT/scripts/build-stdlib-ref.sh" --emit
[[ -f "$STDLIB_RT"  ]] || bash "$ROOT/scripts/build-stdlib-rt.sh" --emit || true
[[ -f "$STDLIB_REF" ]] || { echo "pack-nuget: missing $STDLIB_REF (scripts/build-stdlib-ref.sh --emit failed?)" >&2; exit 1; }
[[ -f "$STDLIB_RT"  ]] || { echo "pack-nuget: missing $STDLIB_RT (scripts/build-stdlib-rt.sh --emit failed?)" >&2; exit 1; }

echo "== assemble DotKt.Toolchain/tools =="
TC="$ROOT/packaging/DotKt.Toolchain/tools"; rm -rf "$TC"; mkdir -p "$TC"
cp -r "$ROOT/toolchain/kotc/build/install/kotc" "$TC/kotc"
cp "$FE_JAR" "$TC/kotlin-stdlib-clr-frontend.jar"
cp -r "$ROOT/build/ilemit-bin"    "$TC/ilemit"
cp -r "$ROOT/build/bir2cir-bin"   "$TC/bir2cir"
cp -r "$ROOT/build/facadegen-bin" "$TC/facadegen"
cp -r "$ROOT/build/retarget-bin"  "$TC/retarget"
# The compile-time stdlib REFERENCE assembly rides with the compiler (DotKt.Toolchain.props points
# $(DotKtStdlibRefAsm) here; Sdk.props turns it into a non-copy <Reference>).
mkdir -p "$TC/stdlib"
cp "$STDLIB_REF" "$TC/stdlib/DotKt.Private.Stdlib.dll"

echo "== assemble DotKt.Stdlib/lib =="
SL="$ROOT/packaging/DotKt.Stdlib/lib/net10.0"; rm -rf "$ROOT/packaging/DotKt.Stdlib/lib"; mkdir -p "$SL"
cp "$STDLIB_RT" "$SL/DotKt.Stdlib.dll"

echo "== pack DotKt.Toolchain + DotKt.Sdk + DotKt.Stdlib + DotKt.Templates =="
dotnet pack "$ROOT/packaging/DotKt.Toolchain/DotKt.Toolchain.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Sdk/DotKt.Sdk.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Stdlib/DotKt.Stdlib.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Templates/DotKt.Templates.csproj" -o "$FEED" -v q --nologo

echo "== feed =="; ls -1 "$FEED"
