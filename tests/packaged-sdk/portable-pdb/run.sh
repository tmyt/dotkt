#!/usr/bin/env bash
# Real packaged compiler + SDK; a metadata reader checks physical PE/PDB identity and source maps.
set -euo pipefail
fixture="$(cd "$(dirname "$0")" && pwd)"
feed="$(realpath "$1")"; version="$2"
mkdir "$3"
ws="$(realpath "$3")"; app="$ws/app"
mkdir "$app" "$ws/inspect"
cp "$fixture/"*.kt "$app/"
cp "$fixture/"*.cs "$fixture/"*.csproj "$ws/inspect/"
cat > "$ws/nuget.config" <<EOF
<configuration>
  <config><add key="globalPackagesFolder" value="$ws/packages" /></config>
  <packageSources><clear /><add key="local" value="$feed" /></packageSources>
</configuration>
EOF
cat > "$app/Probe.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$version">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
    <ProduceReferenceAssembly>true</ProduceReferenceAssembly>
  </PropertyGroup>
</Project>
EOF
logged() {
    local stage="$1"; shift
    if ! "$@" > "$ws/$stage.log" 2>&1; then
        tail -60 "$ws/$stage.log" >&2
        return 1
    fi
}
project="$app/Probe.ktproj"
build() {
    local stage="$1"; shift
    logged "$stage" dotnet build "$project" -c Debug --nologo \
        -p:CscToolExe=dotkt-csc-must-not-run -p:UseSharedCompilation=false "$@"
}
logged inspect dotnet build "$ws/inspect/Inspect.csproj" --nologo -o "$ws/inspector"
inspect() { dotnet "$ws/inspector/Inspect.dll" "$@"; }
obj="$app/obj/Debug/net10.0"; bin="$app/bin/Debug/net10.0"
impl="$bin/Probe.dll"; pdb="$bin/Probe.pdb"
verify_symbols() {
    cmp "$pdb" "$obj/Probe.pdb"
    inspect "$impl" "$pdb" "$app/Sample.kt" "$app/Inline.kt"
    inspect "$obj/ref/Probe.dll"
    [[ "$(dotnet "$impl")" == 42 ]]
}
emit_state() { stat -c '%y' "$obj/Probe.dll" "$obj/Probe.pdb" "$obj/dotkt-ilemit.stamp"; }
frontend_state() { stat -c '%y' "$obj/bir/.stamp" "$obj/cir/.stamp"; }

build portable
verify_symbols
frontend="$(frontend_state)"; before="$(emit_state)"
build no-op
[[ "$before" == "$(emit_state)" ]]

build disabled -p:DebugType=none
[[ ! -e "$pdb" && ! -e "$obj/Probe.pdb" ]]
inspect "$impl"
[[ "$frontend" == "$(frontend_state)" ]]

build optimized -p:Optimize=true
verify_symbols
[[ "$frontend" == "$(frontend_state)" ]]
mv "$obj/Probe.pdb" "$ws/removed.pdb"
build recovered -p:Optimize=true
verify_symbols
[[ "$frontend" == "$(frontend_state)" ]]

logged publish dotnet publish "$project" --no-build -c Debug -p:Optimize=true -o "$ws/published"
cmp "$impl" "$ws/published/Probe.dll"
cmp "$pdb" "$ws/published/Probe.pdb"
logged clean dotnet clean "$project" -c Debug -p:Optimize=true --nologo
[[ ! -e "$pdb" && ! -e "$obj/Probe.pdb" ]]
