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
logged inspect dotnet build "$ws/inspect/Inspect.csproj" --nologo -o "$ws/inspector" \
    -p:DotKtSharedIrDirectory="$fixture/../../../toolchain/bir-common" \
    -p:DotKtDll2KlibDirectory="$fixture/../../../build/dll2klib-bin"
inspect() { dotnet "$ws/inspector/Inspect.dll" "$@"; }
obj="$app/obj/Debug/net10.0"; bin="$app/bin/Debug/net10.0"
impl="$bin/Probe.dll"; pdb="$bin/Probe.pdb"
verify_symbols() {
    cmp "$pdb" "$obj/Probe.pdb"
    inspect "$impl" "$pdb" "$app/Sample.kt" "$app/Inline.kt" "$app/SourceCases.kt" "${1:---deterministic}"
    inspect "$obj/ref/Probe.dll" "${1:---deterministic}"
    [[ "$(dotnet "$impl")" == 42 ]]
}
emit_state() { stat -c '%y' "$obj/Probe.dll" "$obj/Probe.pdb" "$obj/dotkt-ilemit.stamp"; }
frontend_state() { stat -c '%y' "$obj/bir/.stamp" "$obj/cir/.stamp"; }
emitter="$ws/packages/dotkt.toolchain/$version/tools/ilemit/ilemit.dll"
reemit() { (cd "$app" && dotnet "$emitter" "@$obj/dotkt-ilemit.rsp" "$@"); }
repeat_emission() {
    local stage="$1" stable="$2" policy="--deterministic"
    [[ "$stable" == true ]] || policy="--non-deterministic"
    local reference
    reference=$(sed -n '/^--reference-assembly$/{n;s/^"//;s/"$//;p;}' "$obj/dotkt-ilemit.rsp")
    [[ -f "$reference" ]]
    cp "$obj/Probe.dll" "$ws/$stage-before.dll"
    cp "$reference" "$ws/$stage-before-ref.dll"
    [[ ! -f "$obj/Probe.pdb" ]] || cp "$obj/Probe.pdb" "$ws/$stage-before.pdb"
    # Re-run the exact emitter response, not an incremental build that could silently reuse an output.
    sleep 2
    logged "$stage-repeat" reemit
    for image in "$obj/Probe.dll" "$reference"; do
        local before="$ws/$stage-before.dll"
        [[ "$image" != "$reference" ]] || before="$ws/$stage-before-ref.dll"
        if [[ "$stable" == true ]]; then cmp "$before" "$image"
        elif cmp -s "$before" "$image"; then
            echo "Non-deterministic emission reused identical $image" >&2; return 1
        fi
    done
    inspect "$reference" "$policy"
    if [[ -f "$obj/Probe.pdb" ]]; then
        if [[ "$stable" == true ]]; then cmp "$ws/$stage-before.pdb" "$obj/Probe.pdb"
        elif cmp -s "$ws/$stage-before.pdb" "$obj/Probe.pdb"; then
            echo "Non-deterministic emission reused an identical PDB" >&2; return 1
        fi
        inspect "$obj/Probe.dll" "$obj/Probe.pdb" "$app/Sample.kt" "$app/Inline.kt" "$app/SourceCases.kt" "$policy"
    else
        inspect "$obj/Probe.dll" "$policy"
    fi
}

build portable
verify_symbols
frontend="$(frontend_state)"; before="$(emit_state)"
build no-op
[[ "$before" == "$(emit_state)" ]]
repeat_emission deterministic-portable true

# Identical concatenated IL, row counts and source points can still map MethodDefs to different bodies.
# Reuse the same SDK response and change only that physical input; no second frontend build is needed.
inspect --shift-body-map "$fixture/BodyMap.cir.json" "$ws/shifted-body-map.cir.json"
logged body-map-first reemit "$fixture/BodyMap.cir.json"
cp "$obj/Probe.pdb" "$ws/body-map-first.pdb"
logged body-map-second reemit "$ws/shifted-body-map.cir.json"
inspect --different-pdb "$ws/body-map-first.pdb" "$obj/Probe.pdb"
inspect "$obj/Probe.dll" "$obj/Probe.pdb" "$app/Sample.kt" "$app/Inline.kt" "$app/SourceCases.kt" --deterministic

build disabled -p:DebugType=none
[[ ! -e "$pdb" && ! -e "$obj/Probe.pdb" ]]
inspect "$impl" --deterministic
[[ "$frontend" == "$(frontend_state)" ]]
repeat_emission deterministic-disabled true

build optimized -p:Optimize=true
verify_symbols
[[ "$frontend" == "$(frontend_state)" ]]
inspect "$obj/Probe.dll" --different-mvid "$ws/deterministic-portable-before.dll"
mv "$obj/Probe.pdb" "$ws/removed.pdb"
build recovered -p:Optimize=true
verify_symbols
[[ "$frontend" == "$(frontend_state)" ]]

logged publish dotnet publish "$project" --no-build -c Debug -p:Optimize=true -o "$ws/published"
cmp "$impl" "$ws/published/Probe.dll"
cmp "$pdb" "$ws/published/Probe.pdb"
build non-deterministic -p:Optimize=true -p:Deterministic=false
verify_symbols --non-deterministic
[[ "$frontend" == "$(frontend_state)" ]]
repeat_emission non-deterministic-portable false
build non-deterministic-disabled -p:Optimize=true -p:Deterministic=false -p:DebugType=none
[[ ! -e "$pdb" && ! -e "$obj/Probe.pdb" ]]
[[ "$frontend" == "$(frontend_state)" ]]
repeat_emission non-deterministic-disabled false
logged clean dotnet clean "$project" -c Debug -p:Optimize=true --nologo
[[ ! -e "$pdb" && ! -e "$obj/Probe.pdb" ]]
