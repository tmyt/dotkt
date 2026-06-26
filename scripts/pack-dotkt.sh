#!/usr/bin/env bash
# Build the three DotKt NuGet packages (Sdk / Toolchain / Runtime) into a local feed.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
FEED="$ROOT/build/nuget-feed"; rm -rf "$FEED"; mkdir -p "$FEED"
VER="0.9.3+kotlin-2.2.0"

echo "== build compiler (installDist) + tools + runtime =="
( cd "$ROOT" && ./gradlew -q :kotc:installDist )
dotnet build "$ROOT/toolchain/ilemit"   -c Release -o "$ROOT/build/ilemit-bin"   -v q --nologo
dotnet build "$ROOT/toolchain/bir2cir"  -c Release -o "$ROOT/build/bir2cir-bin"  -v q --nologo
dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo
dotnet build "$ROOT/toolchain/retarget" -c Release -o "$ROOT/build/retarget-bin" -v q --nologo

echo "== assemble DotKt.Toolchain/tools =="
TC="$ROOT/packaging/DotKt.Toolchain/tools"; rm -rf "$TC"; mkdir -p "$TC"
cp -r "$ROOT/toolchain/kotc/build/install/kotc" "$TC/kotc"
cp "$ROOT/toolchain/kotc/vendor/kotlin-stdlib.jar" "$TC/kotlin-stdlib.jar"
cp -r "$ROOT/build/ilemit-bin"   "$TC/ilemit"
cp -r "$ROOT/build/bir2cir-bin"  "$TC/bir2cir"
cp -r "$ROOT/build/facadegen-bin" "$TC/facadegen"
cp -r "$ROOT/build/retarget-bin" "$TC/retarget"

echo "== pack DotKt.Runtime =="
dotnet pack "$ROOT/runtime/DotKt.Runtime" -c Release -o "$FEED" -v q --nologo

echo "== pack DotKt.Toolchain + DotKt.Sdk =="
dotnet pack "$ROOT/packaging/DotKt.Toolchain/DotKt.Toolchain.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Sdk/DotKt.Sdk.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Templates/DotKt.Templates.csproj" -o "$FEED" -v q --nologo

echo "== feed =="; ls -1 "$FEED"
