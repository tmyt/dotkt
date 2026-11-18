#!/usr/bin/env bash
# Use the packaged gate's isolated NuGet config and select an 11 SDK only for this consumer.
set -euo pipefail
config=$1 version=$2 work=$3
mkdir -p "$work"
cp "$config" "$work/nuget.config"
cd "$work"
cat > global.json <<EOF
{"sdk":{"version":"11.0.100","rollForward":"latestFeature"},"msbuild-sdks":{"DotKt.Sdk":"$version"}}
EOF
cat > App.ktproj <<'EOF'
<Project Sdk="DotKt.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework></PropertyGroup>
</Project>
EOF
cat > App.kt <<'EOF'
fun main() {
    // TargetFrameworkName reads the entry assembly's TargetFrameworkAttribute.
    check(System.AppContext.TargetFrameworkName == ".NETCoreApp,Version=v11.0")
    check(System.Environment.Version.Major == 11)
}
EOF
dotnet build App.ktproj --nologo
# The metadata and runtime checks alone cannot distinguish compiling against the .NET 10 ref pack.
grep -F '/ref/net11.0/System.Runtime.dll' obj/Debug/net11.0/dotkt-bir2cir.rsp >/dev/null
dotnet bin/Debug/net11.0/App.dll
