#!/usr/bin/env bash
# Compare managed version identity/attributes with the same SDK's C# output, including incremental switches.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
feed="$(realpath "$1")"; version="$2"
mkdir "$3" # Fresh caller-owned workspace only.
ws="$(realpath "$3")"; kt="$ws/Kotlin app"; cs="$ws/CSharp control"
mkdir "$kt" "$cs"
cat > "$ws/nuget.config" <<EOF
<configuration><config><add key="globalPackagesFolder" value="$ws/packages" /></config>
<fallbackPackageFolders><clear /></fallbackPackageFolders>
<packageSources><clear /><add key="local" value="$feed" /></packageSources></configuration>
EOF
for spec in "DotKt.Sdk/$version|$kt/App.ktproj" "Microsoft.NET.Sdk|$cs/Control.csproj"; do
  sdk="${spec%%|*}"; project="${spec#*|}"
  cat > "$project" <<'EOF'
<Project Sdk="SDK_PLACEHOLDER"><PropertyGroup>
  <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>Versioned</AssemblyName>
  <InformationalVersion Condition="'$(ProbeInfo)' != ''">$(ProbeInfo)</InformationalVersion>
  <InformationalVersion Condition="'$(SpecialVersion)' == 'true'">build &quot;quoted&quot; ' \ path &amp; Ω;tail</InformationalVersion>
</PropertyGroup>
<ItemGroup Condition="'$(CustomVersionAttributes)' == 'true'">
  <AssemblyAttribute Include="System.Reflection.AssemblyFileVersionAttribute"><_Parameter1>6.7.8.9</_Parameter1></AssemblyAttribute>
  <AssemblyAttribute Include="System.Reflection.AssemblyInformationalVersionAttribute"><_Parameter1>custom</_Parameter1></AssemblyAttribute>
</ItemGroup>
<ItemGroup Condition="'$(EmptyVersionAttributes)' == 'true'">
  <AssemblyAttribute Include="System.Reflection.AssemblyFileVersionAttribute"><_Parameter1></_Parameter1></AssemblyAttribute>
  <AssemblyAttribute Include="System.Reflection.AssemblyInformationalVersionAttribute"><_Parameter1></_Parameter1></AssemblyAttribute>
</ItemGroup>
<Target Name="RecordProviderVersion" BeforeTargets="DotKtIlEmit">
  <GetAssemblyIdentity AssemblyFiles="$(IntermediateOutputPath)$(TargetName)$(TargetExt)">
    <Output TaskParameter="Assemblies" ItemName="ProviderIdentity" />
  </GetAssemblyIdentity>
  <WriteLinesToFile File="$(IntermediateOutputPath)provider-version.txt" Lines="@(ProviderIdentity->'%(Version)')" Overwrite="true" WriteOnlyWhenDifferent="true" />
</Target></Project>
EOF
  sed -i "s|SDK_PLACEHOLDER|$sdk|" "$project"
done
printf '%s\n' 'class Versioned { fun value(): String = "versioned" }' 'fun main() { println(Versioned().value()) }' > "$kt/Program.kt"
printf '%s\n' 'public class Versioned { public string value() => "versioned"; public static void Main() => System.Console.WriteLine("versioned"); }' > "$cs/Program.cs"
dotnet build "$REPO/tests/packaged-sdk/application-manifest/ManifestInspector.csproj" -o "$ws/inspector" -v:q --nologo > "$ws/inspector.log" 2>&1
inspect() { dotnet "$ws/inspector/ManifestInspector.dll" "$@"; }
build() { local project="$1" stage="$2"; shift 2; dotnet build "$project" -v:q --nologo "$@" > "$ws/$stage.log" 2>&1; }
out="bin/Debug/net10.0"; obj="obj/Debug/net10.0"
defaults=(-p:IncludeSourceRevisionInInformationalVersion=false)
pair() {
  local stage="$1"; shift
  build "$kt/App.ktproj" "$stage" "${defaults[@]}" "$@"
  build "$cs/Control.csproj" "control-$stage" --no-incremental "${defaults[@]}" "$@"
  inspect --managed-match "$cs/$out/Versioned.dll" "$kt/$out/Versioned.dll"
  inspect --kotlin "$kt/$out/Versioned.dll"
}
check() { inspect --managed "$@" "$kt/$out/Versioned.dll"; }
state() { stat -c '%y' "$kt/$out/Versioned.dll" "$kt/$obj/dotkt-ilemit.stamp" "$kt/$obj/Versioned.pdb"; }
frontend() { stat -c '%y' "$kt/$obj/bir/.stamp" "$kt/$obj/cir/.stamp"; }

pair default
check 1.0.0.0 1.0.0.0 1.0.0
front_before="$(frontend)"
explicit=(-p:AssemblyVersion=2.3.4.5 -p:FileVersion=5.4.3.2 -p:InformationalVersion=7.8.9-preview+test)
pair explicit "${explicit[@]}"
check 2.3.4.5 5.4.3.2 7.8.9-preview+test
before="$(state)"
build "$kt/App.ktproj" no-op "${defaults[@]}" "${explicit[@]}"
[[ "$before" == "$(state)" ]] || { echo 'version no-op changed outputs' >&2; exit 1; }
pair changed -p:AssemblyVersion=3.4.5.6 -p:FileVersion=6.5.4.3 -p:InformationalVersion=8.9.0-test
check 3.4.5.6 6.5.4.3 8.9.0-test
pair version -p:Version=4.5.6-preview
check 4.5.6.0 4.5.6.0 4.5.6-preview
pair prefix-suffix -p:VersionPrefix=5.6.7 -p:VersionSuffix=beta
check 5.6.7.0 5.6.7.0 5.6.7-beta
pair revision -p:ProbeInfo=1.2.3 -p:SourceRevisionId=deadbeef -p:IncludeSourceRevisionInInformationalVersion=true
check 1.0.0.0 1.0.0.0 1.2.3+deadbeef
pair revision-plus -p:ProbeInfo=1.2.3+meta -p:SourceRevisionId=deadbeef -p:IncludeSourceRevisionInInformationalVersion=true
check 1.0.0.0 1.0.0.0 1.2.3+meta.deadbeef
pair revision-disabled -p:ProbeInfo=1.2.3 -p:SourceRevisionId=deadbeef
check 1.0.0.0 1.0.0.0 1.2.3
pair special -p:SpecialVersion=true
pair custom-attributes -p:CustomVersionAttributes=true -p:GenerateAssemblyFileVersionAttribute=false -p:GenerateAssemblyInformationalVersionAttribute=false
check 1.0.0.0 6.7.8.9 custom
pair empty-attributes -p:EmptyVersionAttributes=true -p:GenerateAssemblyFileVersionAttribute=false -p:GenerateAssemblyInformationalVersionAttribute=false
check 1.0.0.0 '' ''
pair no-file-version -p:GenerateAssemblyFileVersionAttribute=false
check 1.0.0.0 '<absent>' 1.0.0
pair no-informational-version -p:GenerateAssemblyInformationalVersionAttribute=false
check 1.0.0.0 1.0.0.0 '<absent>'
pair no-assembly-version -p:GenerateAssemblyVersionAttribute=false
check 0.0.0.0 1.0.0.0 1.0.0
pair no-assembly-info -p:GenerateAssemblyInfo=false -p:CustomVersionAttributes=true "${explicit[@]}"
check 0.0.0.0 '<absent>' '<absent>'
pair reenabled "${explicit[@]}"
check 2.3.4.5 5.4.3.2 7.8.9-preview+test
# C# expands wildcards at compilation time. Compare with this exact provider, not a later C# invocation's clock.
build "$kt/App.ktproj" wildcard "${defaults[@]}" '-p:AssemblyVersion=2.3.*' -p:FileVersion=2.3.0.0 -p:Deterministic=false
provider="$(tr -d '\r\n' < "$kt/$obj/provider-version.txt")"
[[ "$provider" == 2.3.* ]] || { echo 'wildcard provider version was not expanded' >&2; exit 1; }
check "$provider" 2.3.0.0 1.0.0
pair library -p:OutputType=Library "${explicit[@]}"
[[ "$front_before" == "$(frontend)" ]] || { echo 'version options recompiled Kotlin' >&2; exit 1; }
for project in "$kt/App.ktproj" "$cs/Control.csproj"; do
  dotnet publish "$project" -c Debug --no-restore -v:q --nologo "${defaults[@]}" "${explicit[@]}" -p:OutputType=Library >> "$ws/publish.log" 2>&1
done
inspect --managed-match "$cs/$out/publish/Versioned.dll" "$kt/$out/publish/Versioned.dll"

# Separately compiled Kotlin and C# consumers must encode the producer's nonzero AssemblyRef version and run.
for spec in "DotKt.Sdk/$version|Kotlin consumer|Consumer.ktproj" "Microsoft.NET.Sdk|CSharp consumer|Consumer.csproj"; do
  sdk="${spec%%|*}"; rest="${spec#*|}"; consumer="$ws/${rest%%|*}"; project="${rest#*|}"
  mkdir "$consumer"
  cat > "$consumer/$project" <<EOF
<Project Sdk="$sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<ItemGroup><Reference Include="Versioned"><HintPath>$kt/$out/Versioned.dll</HintPath></Reference></ItemGroup></Project>
EOF
  if [[ "$project" == *.ktproj ]]; then printf '%s\n' 'fun main() { println(Versioned().value()) }' > "$consumer/Program.kt"
  else printf '%s\n' 'System.Console.WriteLine(new Versioned().value());' > "$consumer/Program.cs"; fi
  build "$consumer/$project" "${rest%%|*}"
  inspect --reference-version Versioned 2.3.4.5 "$consumer/$out/Consumer.dll"
  [[ "$(dotnet "$consumer/$out/Consumer.dll")" == versioned ]] || { echo 'versioned consumer failed' >&2; exit 1; }
  bash "$REPO/tests/run-ilverify.sh" "$consumer/$out/Consumer.dll"
done
bash "$REPO/tests/run-ilverify.sh" "$kt/$out/Versioned.dll"
echo 'managed-versions: PASS (SDK/C# parity, opt-outs, revision IDs, wildcards, incremental, publish and DLL consumers)'
