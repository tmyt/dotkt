#!/usr/bin/env bash
# One packaged producer, consumed through real SDK ProjectReferences from Kotlin and C#.
set -euo pipefail
feed="$(realpath "$1")"; version="$2"
mkdir "$3"
ws="$(realpath "$3")"; lib="$ws/Kotlin library"
mkdir "$lib" "$ws/kotlin" "$ws/csharp"
cat > "$ws/nuget.config" <<EOF
<configuration>
  <config><add key="globalPackagesFolder" value="$ws/packages" /></config>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSources><clear /><add key="local" value="$feed" /></packageSources>
</configuration>
EOF
cat > "$lib/Producer.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$version">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ProduceReferenceAssembly>true</ProduceReferenceAssembly>
    <AssemblyVersion>2.3.4.5</AssemblyVersion>
  </PropertyGroup>
</Project>
EOF
cat > "$lib/Api.kt" <<'EOF'
package refprobe

open class Bound(val number: Int)
class Box<T : Bound>(val value: T) {
    fun echo(other: T = value): T = other
}
interface AbstractApi { fun action(): Int }
inline fun supplied(get: () -> Int): Int = get() + 1
EOF
for consumer in kotlin csharp; do
    case "$consumer" in
        kotlin) sdk="DotKt.Sdk/$version"; ext=ktproj ;;
        csharp) sdk=Microsoft.NET.Sdk; ext=csproj ;;
    esac
    cat > "$ws/$consumer/Consumer.$ext" <<EOF
<Project Sdk="$sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../Kotlin library/Producer.ktproj" /></ItemGroup>
</Project>
EOF
done
cat > "$ws/kotlin/Program.kt" <<'EOF'
import refprobe.Bound
import refprobe.Box
import refprobe.supplied

fun result(): Int {
    val box = Box(Bound(41))
    // Without the inline carrier on the reference image, this return is illegal.
    supplied { return box.echo().number + 1 }
    return 0
}

fun main() { check(result() == 42) }
EOF
cat > "$ws/csharp/Program.cs" <<'EOF'
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using refprobe;

var value = new Bound(42);
if (!ReferenceEquals(new Box<Bound, Bound>(value).echo(value), value))
    throw new Exception("The implementation assembly did not execute.");

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
var assembly = md.GetAssemblyDefinition();
if (assembly.Version != new Version(2, 3, 4, 5) ||
    !assembly.GetCustomAttributes().Select(md.GetCustomAttribute).Any(a =>
        a.Constructor.Kind == HandleKind.MemberReference &&
        md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent is var type &&
        type.Kind == HandleKind.TypeReference &&
        md.GetString(md.GetTypeReference((TypeReferenceHandle)type).Name) == "ReferenceAssemblyAttribute"))
    throw new Exception("Incorrect reference assembly identity or marker.");

var box = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t => md.GetString(t.Name) == "Box`2");
if (box.GetGenericParameters().Count != 2 ||
    box.GetGenericParameters().Any(g => md.GetGenericParameter(g).GetConstraints().Count == 0))
    throw new Exception("Reference assembly lost a CLR generic constraint.");
foreach (var handle in md.MethodDefinitions)
{
    var method = md.GetMethodDefinition(handle);
    var name = md.GetString(method.Name);
    var rva = method.RelativeVirtualAddress;
    if (rva != 0 && !pe.GetMethodBody(rva).GetILBytes().SequenceEqual(new byte[] { 0x14, 0x7a }))
        throw new Exception($"Non-stub reference body: {md.GetString(method.Name)}.");
    if ((name == "action" && rva != 0) || (name == "echo" && rva == 0))
        throw new Exception($"Wrong reference body presence for {name}.");
}
EOF
build() {
    local stage="$1" project="$2"; shift 2
    if ! dotnet build "$project" -v:n --nologo "$@" > "$ws/$stage.log" 2>&1; then
        tail -60 "$ws/$stage.log" >&2
        return 1
    fi
}

obj="$lib/obj/Debug/net10.0"
impl="$lib/bin/Debug/net10.0/Producer.dll"
ref="$obj/ref/Producer.dll"; refint="$obj/refint/Producer.dll"
build opt-out "$lib/Producer.ktproj" -p:ProduceReferenceAssembly=false
[[ -f "$impl" && ! -e "$ref" && ! -e "$refint" ]]
build kotlin "$ws/kotlin/Consumer.ktproj" -p:CscToolExe=dotkt-csc-must-not-run -p:UseSharedCompilation=false
cmp "$ref" "$refint"
if cmp -s "$ref" "$impl"; then
    echo 'The reference and implementation images are identical.' >&2
    exit 1
fi
grep -Fxq "$ref" "$ws/kotlin/obj/Debug/net10.0/dotkt-reference-klibs.rsp"
dotnet "$ws/kotlin/bin/Debug/net10.0/Consumer.dll"

# The compiler command must reference the ref image, while both consumers copy the implementation.
build csharp "$ws/csharp/Consumer.csproj" -p:UseSharedCompilation=false
grep -F '/reference:' "$ws/csharp.log" | grep -F "$ref" >/dev/null
dotnet "$ws/csharp/bin/Debug/net10.0/Consumer.dll" "$ref"
for consumer in kotlin csharp; do cmp "$impl" "$ws/$consumer/bin/Debug/net10.0/Producer.dll"; done

state() { stat -c '%y' "$impl" "$ref" "$refint" "$obj/dotkt-ilemit.stamp"; }
before="$(state)"
build no-op "$lib/Producer.ktproj"
[[ "$before" == "$(state)" ]]
mv "$refint" "$ws/deleted-refint.dll"
build recover "$lib/Producer.ktproj"
cmp "$ref" "$refint"
dotnet clean "$lib/Producer.ktproj" --nologo > "$ws/clean.log" 2>&1
[[ ! -e "$ref" && ! -e "$refint" ]]
