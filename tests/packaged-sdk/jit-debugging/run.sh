#!/usr/bin/env bash
set -euo pipefail
task_root=$(cd "$(dirname "$0")/../../.." && pwd)
task_feed=$(realpath "$1"); task_version=$2
mkdir "$3"
task_work=$(realpath "$3")
task_fixture="$task_root/tests/packaged-sdk/jit-debugging"
task_kt="$task_work/Kotlin app"; task_cs="$task_work/CSharp control"
mkdir "$task_kt" "$task_cs"
cp "$task_fixture/fixtures/nuget.config" "$task_work/nuget.config"
dotnet nuget add source "$task_feed" --name local --configfile "$task_work/nuget.config" > "$task_work/nuget-source.log"
sed "s/SDK_VERSION/$task_version/g" "$task_fixture/fixtures/global.json" > "$task_work/global.json"
sed 's/SDK_PLACEHOLDER/DotKt.Sdk/' "$task_fixture/fixtures/App.proj" > "$task_kt/App.ktproj"
sed 's/SDK_PLACEHOLDER/Microsoft.NET.Sdk/' "$task_fixture/fixtures/App.proj" > "$task_cs/Control.csproj"
cp "$task_fixture/fixtures/Program.kt" "$task_kt/Program.kt"
cp "$task_fixture/fixtures/Program.cs" "$task_cs/Program.cs"
dotnet build "$task_fixture/PolicyInspector.csproj" -v:q --nologo -o "$task_work/inspector" \
    -p:BaseIntermediateOutputPath="$task_work/inspector-obj/" \
    -p:MSBuildProjectExtensionsPath="$task_work/inspector-obj/" > "$task_work/inspector.log" 2>&1
task_inspector="$task_work/inspector/PolicyInspector.dll"
build() { local task_project=$1 task_stage=$2; shift 2; dotnet build "$task_project" -m:1 -v:q --nologo "$@" > "$task_work/$task_stage.log" 2>&1; }
state() { stat -c '%y' "$task_kt/bin/$1/net10.0/DebugPolicy.dll" "$task_kt/obj/$1/net10.0/dotkt-ilemit.stamp"; }
frontend() { stat -c '%y' "$task_kt/obj/$1/net10.0/bir/.stamp" "$task_kt/obj/$1/net10.0/cir/.stamp"; }
pair() {
    local task_stage=$1 task_configuration=$2 task_disabled=$3; shift 3
    build "$task_kt/App.ktproj" "$task_stage" -c "$task_configuration" "$@"
    build "$task_cs/Control.csproj" "control-$task_stage" -c "$task_configuration" --no-incremental "$@"
    cat "$task_kt/obj/$task_configuration/net10.0/resolved-options.txt"
    dotnet "$task_inspector" --control "$task_disabled" "$task_cs/bin/$task_configuration/net10.0/DebugPolicy.dll"
    dotnet "$task_inspector" "$task_disabled" "$task_kt/bin/$task_configuration/net10.0/DebugPolicy.dll"
    [[ $(dotnet "$task_kt/bin/$task_configuration/net10.0/DebugPolicy.dll") == 42 ]]
}
pair default-debug Debug true
task_debug_front=$(frontend Debug); task_noop=$(state Debug)
build "$task_kt/App.ktproj" debug-noop -c Debug
[[ "$task_noop" == "$(state Debug)" ]] || { echo 'Unchanged Debug build regenerated output' >&2; exit 1; }
pair default-release Release false
task_release_front=$(frontend Release)
pair debug-optimized Debug false -p:Optimize=true
[[ "$task_debug_front" == "$(frontend Debug)" ]] || { echo 'Optimize changed the Kotlin frontend or lowering' >&2; exit 1; }
task_noop=$(state Debug)
build "$task_kt/App.ktproj" optimized-noop -c Debug -p:Optimize=true
[[ "$task_noop" == "$(state Debug)" ]] || { echo 'Unchanged optimized build regenerated output' >&2; exit 1; }
pair release-unoptimized Release true -p:Optimize=false
[[ "$task_release_front" == "$(frontend Release)" ]] || { echo 'Optimize changed the Kotlin frontend or lowering' >&2; exit 1; }
pair debug-unoptimized-nosymbols Debug true -p:Optimize=false -p:DebugSymbols=false
pair release-optimized-nosymbols Release false -p:Optimize=true -p:DebugType=None
pair debug-optimized-symbols Debug false -p:Optimize=true -p:DebugSymbols=true -p:DebugType=portable
pair release-unoptimized-nosymbols Release true -p:Optimize=false -p:DebugSymbols=false
pair custom-configuration Staging true
task_before_publish=$(state Release)
dotnet publish "$task_kt/App.ktproj" -c Release --no-build -v:q --nologo -p:Optimize=false -p:DebugSymbols=true -p:DebugType=portable > "$task_work/publish-no-build.log" 2>&1
[[ "$task_before_publish" == "$(state Release)" ]] || { echo 'No-build publish regenerated output' >&2; exit 1; }
dotnet "$task_inspector" true "$task_kt/bin/Release/net10.0/publish/DebugPolicy.dll"
[[ $(dotnet "$task_kt/bin/Release/net10.0/publish/DebugPolicy.dll") == 42 ]]
echo 'JIT policy: Optimize defaults/overrides, independent symbols, target metadata, runtime and no-op/incremental checks passed.'
