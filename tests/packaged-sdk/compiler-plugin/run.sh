#!/usr/bin/env bash
# Exercise real Kotlin FIR and IR extensions from a NuGet dependency, not a CLR plugin assembly.
set -euo pipefail
src=$(cd "$(dirname "$0")" && pwd)
feed=$(realpath "$1"); ver=$2
mkdir "$3"; ws=$(realpath "$3"); cd "$ws"
mkdir app fixture feed
export NUGET_PACKAGES="$ws/packages"
cat > NuGet.Config <<EOF
<configuration><packageSources><clear/><add key="dotkt" value="$feed"/><add key="fixture" value="$ws/feed"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>
EOF
cat > global.json <<EOF
{"sdk":{"version":"10.0.100","rollForward":"latestFeature"},"msbuild-sdks":{"DotKt.Sdk":"$ver"}}
EOF
cp "$src"/{App.kt,App.ktproj} app/
cp "$src"/Fixture.* fixture/
# Build the plugin with the JVM compiler distributed by the package being tested.
unzip -q "$feed/DotKt.Toolchain.$ver.nupkg" 'tools/kotc/*' 'tools/kotlin-stdlib-clr-frontend.klib'
lib="$ws/tools/kotc/lib"
jars=("$lib"/*.jar); cp_arg=$(IFS=:; echo "${jars[*]}")
java -cp "$lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect \
    -classpath "$cp_arg" "$src/Plugin.kt" -d classes
jar cf fixture/probe.jar -C classes . -C "$src" META-INF
bash tools/kotc/bin/kotc -classpath tools/kotlin-stdlib-clr-frontend.klib \
    -Xplugin=fixture/probe.jar -P 'plugin:dotkt.fixture:answer=from direct kotc' app/App.kt -d direct
grep -Fq 'from direct kotc' direct/App.bir.json
for pkg in Plugin Consumer; do dotnet pack "fixture/Fixture.$pkg.nuspec" -o feed; done
dotnet restore app/App.ktproj --configfile NuGet.Config
compile() { dotnet build app/App.ktproj --no-restore -v:minimal "$@"; }
run() { dotnet app/bin/Debug/net10.0/App.dll; }
compile > initial.log
test "$(run)" = 'from the package'
rsp=app/obj/Debug/net10.0/dotkt-plugins.rsp
test "$(grep -c -- '-Xplugin=' "$rsp")" = 1
compile > unchanged.log
! grep -Fq 'DotKt: compiling' unchanged.log || exit 1
compile '-p:FixtureAnswer=space%20a%22b%5Cc%3Bd' > changed.log
test "$(run)" = 'space a"b\c;d'
# Replace only the JAR bytes, preserving its path and mtime. Do this to a project
# file, never to a restored package. The compiler must rerun even though the
# app's Kotlin sources and the option string are unchanged.
local_jar="$ws/local plugin.jar"
cp fixture/probe.jar "$local_jar"
compile "-p:FixturePluginPath=$local_jar" > local.log
touch -r "$local_jar" jar.timestamp
touch new-resource
jar uf "$local_jar" new-resource
touch -r jar.timestamp "$local_jar"
compile "-p:FixturePluginPath=$local_jar" > replaced.log
grep -Fq 'DotKt: compiling' replaced.log
test "$(run)" = 'from the package'
# Removing inputs must invalidate the previous success. Recover between the two
# negative probes so each starts from its own green incremental build.
if compile -p:WithoutOption=true > removed-option.log 2>&1; then exit 1; fi
grep -iq 'required plugin option not present: dotkt.fixture:answer' removed-option.log
compile > restored.log
if compile -p:WithoutPlugin=true > removed-jar.log 2>&1; then exit 1; fi
grep -Fq 'generatedAnswer' removed-jar.log
echo 'direct, NuGet, generated runtime value and incremental registration: PASS'
