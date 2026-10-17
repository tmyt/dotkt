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

build "$kt/App.ktproj" absent '-p:ApplicationManifest=' '-p:NoWin32Manifest=true'
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
build "$kt/App.ktproj" cleared '-p:ApplicationManifest=' '-p:NoWin32Manifest=true'
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
build "$kt/App.ktproj" recovered '-p:ApplicationManifest=' '-p:NoWin32Manifest=true'
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

# Give both controls the same native resource identity, so every resource payload (including VERSIONINFO)
# can be compared byte-for-byte. No defaults are hand-authored by the test or the Kotlin compiler.
defaults=(-p:ApplicationManifest= -p:AssemblyName=App -p:Title=ResourceTest -p:Product=ResourceProduct
  -p:Company=ResourceCompany -p:Copyright=ResourceCopyright -p:FileVersion=3.4.5.6
  -p:InformationalVersion=7.8.9-preview -p:IncludeSourceRevisionInInformationalVersion=false)
pair() {
  local stage="$1"; shift
  build "$kt/App.ktproj" "$stage" "${defaults[@]}" "$@"
  build "$cs/Control.csproj" "control-$stage" --no-incremental "${defaults[@]}" "$@"
  inspect --resources "$cs/$out/App.dll" "$kt/$out/App.dll"
  inspect --managed-match "$cs/$out/App.dll" "$kt/$out/App.dll"
  inspect --kotlin "$kt/$out/App.dll"
}
pair defaults
inspect --default "$kt/$out/App.dll" "$kt/$out/App.exe"
inspect --version 3.4.5.6 7.8.9-preview "$kt/$out/App.dll" "$kt/$out/App.exe"
before="$(state)"; front_before="$(frontend_state)"
[[ "$(dotnet "$kt/$out/App.dll")" == 'Kotlin manifest app' ]] || { echo 'Kotlin entry point did not run' >&2; exit 1; }
build "$kt/App.ktproj" defaults-no-op "${defaults[@]}"
[[ "$before" == "$(state)" ]] || { echo 'default resources no-op changed outputs' >&2; exit 1; }
# Re-emission may run after CoreCompile has skipped, so the resource provider can be the previous Kotlin PE.
before="$(state)"
pdb_before="$(stat -c '%y' "$kt/$obj/App.pdb")"
touch "$kt/$obj/cir/.stamp"
build "$kt/App.ktproj" reemit "${defaults[@]}" -v:n
[[ "$before" != "$(state)" ]] || { echo 'reemit did not update outputs' >&2; exit 1; }
[[ "$pdb_before" == "$(stat -c '%y' "$kt/$obj/App.pdb")" ]] || { echo 'reemit unexpectedly reran Csc' >&2; exit 1; }
grep -Fq 'Skipping target "CoreCompile"' "$ws/reemit.log"
inspect --resources "$cs/$out/App.dll" "$kt/$out/App.dll"
inspect --kotlin "$kt/$out/App.dll"
front_before="$(frontend_state)"
pair disabled -p:NoWin32Manifest=true
inspect --absent "$kt/$out/App.dll" "$kt/$out/App.exe"
inspect --version 3.4.5.6 7.8.9-preview "$kt/$out/App.dll" "$kt/$out/App.exe"
pair enabled -p:NoWin32Manifest=false
inspect --default "$kt/$out/App.dll" "$kt/$out/App.exe"
pair versions -p:FileVersion=8.7.6.5 -p:InformationalVersion=9.8.7-test
inspect --version 8.7.6.5 9.8.7-test "$kt/$out/App.dll" "$kt/$out/App.exe"
for project in "$kt/App.ktproj" "$cs/Control.csproj"; do
  dotnet publish "$project" -c Debug --no-restore -v:q --nologo "${defaults[@]}" \
    -p:FileVersion=8.7.6.5 -p:InformationalVersion=9.8.7-test >> "$ws/default-publish.log" 2>&1
done
inspect --resources "$cs/$out/publish/App.exe" "$kt/$out/publish/App.exe"
inspect --default "$kt/$out/publish/App.exe"
pair default-winexe -p:OutputType=WinExe
inspect --default "$kt/$out/App.dll" "$kt/$out/App.exe"
inspect --gui "$kt/$out/App.exe" "$cs/$out/App.exe"
pair default-library -p:OutputType=Library
inspect --absent "$kt/$out/App.dll"
inspect --library --absent "$kt/$out/App.dll"
inspect --version 3.4.5.6 7.8.9-preview "$kt/$out/App.dll"
[[ "$front_before" == "$(frontend_state)" ]] || { echo 'native resource options recompiled Kotlin' >&2; exit 1; }
# No apphost: the managed PE still carries SDK-selected resources, but cannot change the dotnet host's policy.
pair no-apphost -p:UseAppHost=false
inspect --default "$kt/$out/App.dll"
# A complete .res replaces generated resources rather than being overlaid with defaults. Exercise named
# resources and a non-neutral language as well as switching to an older input and then clearing it.
inspect --write-res "$manifests/first.res" first-payload
inspect --write-res "$manifests/second.res" second-payload
pair custom-resource "-p:Win32Resource=$manifests/first.res"
inspect --absent "$kt/$out/App.dll" "$kt/$out/App.exe"
inspect --resources "$cs/$out/App.exe" "$kt/$out/App.exe"
pair switched-resource "-p:Win32Resource=$manifests/second.res"
inspect --resources "$cs/$out/App.exe" "$kt/$out/App.exe"
inspect --write-res "$manifests/second.res" changed-payload
pair changed-resource "-p:Win32Resource=$manifests/second.res"
inspect --resources "$cs/$out/App.exe" "$kt/$out/App.exe"
pair cleared-resource -p:Win32Resource=
inspect --default "$kt/$out/App.dll" "$kt/$out/App.exe"
inspect --version 3.4.5.6 7.8.9-preview "$kt/$out/App.dll" "$kt/$out/App.exe"
inspect --write-icon "$manifests/first.ico" 4294901760
inspect --write-icon "$manifests/second.ico" 4278190335
pair icon "-p:ApplicationIcon=$manifests/first.ico"
inspect --resources "$cs/$out/App.exe" "$kt/$out/App.exe"
pair switched-icon "-p:ApplicationIcon=$manifests/second.ico"
inspect --resources "$cs/$out/App.exe" "$kt/$out/App.exe"
pair cleared-icon -p:ApplicationIcon=
inspect --resources "$cs/$out/App.exe" "$kt/$out/App.exe"
[[ "$front_before" == "$(frontend_state)" ]] || { echo 'resource or icon edits recompiled Kotlin' >&2; exit 1; }
bash "$REPO/tests/run-ilverify.sh" "$kt/$out/App.dll"
echo 'application-manifest: PASS (explicit/default/disabled, VERSIONINFO, C# parity, final DLL/apphost/build/publish, incremental)'
