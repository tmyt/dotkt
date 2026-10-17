#!/usr/bin/env bash
# Fresh packaged-SDK consumer + same-target C# oracle; inspect the final Windows apphost, not a placeholder.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
FIXTURE="$REPO/tests/packaged-sdk/application-manifest"
feed="$(realpath "$1")"
version="$2"
mkdir "$3" # Require a fresh workspace; never delete caller-provided paths.
ws="$(realpath "$3")"
kt="$ws/Kotlin app"
cs="$ws/CSharp control"
manifests="$ws/manifest files"
mkdir "$kt" "$cs" "$manifests"
{ printf '\357\273\277'; cat "$FIXTURE/first.manifest"; } > "$manifests/first.manifest"
cp "$FIXTURE/second.manifest" "$manifests/second.manifest"
cat > "$ws/nuget.config" <<EOF
<configuration>
  <config><add key="globalPackagesFolder" value="$ws/packages" /></config>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSources><clear /><add key="local" value="$feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><packageSource key="local"><package pattern="DotKt.*" /></packageSource><packageSource key="nuget.org"><package pattern="Microsoft.NETCore.App.Host.*" /></packageSource></packageSourceMapping>
</configuration>
EOF
for spec in "DotKt.Sdk/$version|$kt/App.ktproj" "Microsoft.NET.Sdk|$cs/Control.csproj"; do
  sdk="${spec%%|*}"; project="${spec#*|}"
  cat > "$project" <<EOF
<Project Sdk="$sdk"><PropertyGroup>
  <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier><SelfContained>false</SelfContained>
  <ApplicationManifest>../manifest files/first.manifest</ApplicationManifest>
</PropertyGroup></Project>
EOF
done
printf '%s\n' 'fun main() { println("Kotlin manifest app") }' > "$kt/Program.kt"
printf '%s\n' 'public static class Program { public static void Main() => System.Console.WriteLine("CSharp manifest control"); }' > "$cs/Program.cs"
dotnet build "$FIXTURE/ManifestInspector.csproj" -o "$ws/inspector" -v:q --nologo > "$ws/inspector.log" 2>&1
inspect() { dotnet "$ws/inspector/ManifestInspector.dll" "$@"; }
build() { local project="$1" stage="$2"; shift 2; dotnet build "$project" -v:q --nologo "$@" > "$ws/$stage.log" 2>&1; }
out="bin/Debug/net10.0/win-x64"
obj="obj/Debug/net10.0/win-x64"
state() { stat -c '%y' "$kt/$out/App.dll" "$kt/$out/App.exe" "$kt/$obj/dotkt-ilemit.stamp"; }
frontend_state() { stat -c '%y' "$kt/$obj/bir/.stamp" "$kt/$obj/cir/.stamp"; }
check_pair() {
  inspect "$1" "$kt/$out/App.dll" "$kt/$out/App.exe" "$cs/$out/Control.dll" "$cs/$out/Control.exe"
  inspect --bytes "$1" "$kt/$out/App.dll" "$kt/$out/App.exe"
  inspect --kotlin "$kt/$out/App.dll"
}

build "$kt/App.ktproj" absent '-p:ApplicationManifest='
inspect --absent "$kt/$out/App.dll" "$kt/$out/App.exe"
front_before="$(frontend_state)"
build "$kt/App.ktproj" selected
build "$cs/Control.csproj" control --no-incremental
check_pair "$manifests/first.manifest"
before="$(state)"
build "$kt/App.ktproj" no-op
[[ "$before" == "$(state)" ]] || { echo "manifest no-op changed outputs" >&2; exit 1; }
if grep -Fq 'DotKt: emitting CIL' "$ws/no-op.log"; then echo 'manifest no-op re-emitted' >&2; exit 1; fi

sed 's/DotKt.Manifest.First/DotKt.Manifest.Changed/' "$FIXTURE/first.manifest" > "$manifests/first.manifest"
build "$kt/App.ktproj" content-change
build "$cs/Control.csproj" control-content-change --no-incremental
check_pair "$manifests/first.manifest"
# The second file predates the current output: selection, not its timestamp, must invalidate emission.
build "$kt/App.ktproj" selected-second '-p:ApplicationManifest=../manifest files/second.manifest'
# Force the oracle fresh: the native SDK can retain an older-timestamp manifest when only its path changes.
build "$cs/Control.csproj" control-second --no-incremental '-p:ApplicationManifest=../manifest files/second.manifest'
check_pair "$manifests/second.manifest"
for project in "$kt/App.ktproj" "$cs/Control.csproj"; do
  dotnet publish "$project" -c Debug --no-restore -v:q --nologo '-p:ApplicationManifest=../manifest files/second.manifest' >> "$ws/publish.log" 2>&1
done
inspect "$manifests/second.manifest" "$kt/$out/publish/App.exe" "$cs/$out/publish/Control.exe"
inspect --bytes "$manifests/second.manifest" "$kt/$out/publish/App.exe"
build "$kt/App.ktproj" cleared '-p:ApplicationManifest='
inspect --absent "$kt/$out/App.dll" "$kt/$out/App.exe"
[[ "$front_before" == "$(frontend_state)" ]] || { echo 'native manifest edits recompiled Kotlin' >&2; exit 1; }

if build "$kt/App.ktproj" missing '-p:ApplicationManifest=../manifest files/does-not-exist.manifest'; then
  echo 'missing manifest unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'does-not-exist.manifest' "$ws/missing.log"
if build "$kt/App.ktproj" unreadable '-p:ApplicationManifest=../manifest files'; then
  echo 'directory manifest unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'manifest files' "$ws/unreadable.log"
build "$kt/App.ktproj" recovered '-p:ApplicationManifest='
inspect --absent "$kt/$out/App.dll" "$kt/$out/App.exe"
build "$kt/App.ktproj" winexe '-p:OutputType=WinExe' "-p:ApplicationManifest=$manifests/second.manifest"
build "$cs/Control.csproj" control-winexe --no-incremental '-p:OutputType=WinExe' "-p:ApplicationManifest=$manifests/second.manifest"
check_pair "$manifests/second.manifest"
inspect --gui "$kt/$out/App.exe" "$cs/$out/Control.exe"
build "$kt/App.ktproj" library '-p:OutputType=Library' "-p:ApplicationManifest=$manifests/second.manifest"
build "$cs/Control.csproj" control-library --no-incremental '-p:OutputType=Library' "-p:ApplicationManifest=$manifests/second.manifest"
inspect --library "$manifests/second.manifest" "$kt/$out/App.dll" "$cs/$out/Control.dll"
inspect --library --bytes "$manifests/second.manifest" "$kt/$out/App.dll"
inspect --kotlin "$kt/$out/App.dll"
bash "$REPO/tests/run-ilverify.sh" "$kt/$out/App.dll"
echo 'application-manifest: PASS (C# oracle, final exe/build/publish, changes/clear/no-op, missing/unusable input)'
