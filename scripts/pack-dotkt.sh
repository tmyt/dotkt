#!/usr/bin/env bash
# Build the three DotKt NuGet packages (Sdk / Toolchain / Runtime) into a local feed.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
FEED="$ROOT/build/nuget-feed"; rm -rf "$FEED"; mkdir -p "$FEED"
VER="0.9.2+kotlin-2.2.0"

echo "== build compiler (installDist) + tools + runtime =="
( cd "$ROOT" && ./gradlew -q :compiler:installDist )
dotnet build "$ROOT/tools/ilemit"   -c Release -o "$ROOT/build/ilemit-bin"   -v q --nologo
dotnet build "$ROOT/tools/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo
dotnet build "$ROOT/tools/retarget" -c Release -o "$ROOT/build/retarget-bin" -v q --nologo

echo "== assemble DotKt.Toolchain/tools =="
TC="$ROOT/packaging/DotKt.Toolchain/tools"; rm -rf "$TC"; mkdir -p "$TC"
cp -r "$ROOT/compiler/build/install/compiler" "$TC/compiler"
cp "$ROOT/runtime/kotlin/kotlin-stdlib.jar" "$TC/kotlin-stdlib.jar"
cp -r "$ROOT/build/ilemit-bin"   "$TC/ilemit"
cp -r "$ROOT/build/facadegen-bin" "$TC/facadegen"
cp -r "$ROOT/build/retarget-bin" "$TC/retarget"

echo "== pack DotKt.Runtime =="
dotnet pack "$ROOT/runtime/DotKt.Runtime" -c Release -o "$FEED" -v q --nologo

echo "== pack DotKt.Toolchain + DotKt.Sdk =="
dotnet pack "$ROOT/packaging/DotKt.Toolchain/DotKt.Toolchain.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Sdk/DotKt.Sdk.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/templates/DotKt.Templates.csproj" -o "$FEED" -v q --nologo

echo "== feed =="; ls -1 "$FEED"
