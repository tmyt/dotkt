#!/usr/bin/env bash
# The same stateful DLL-ownership regression runs through the dev import and the shipping SDK.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
work="${1:?scratch directory required}"
sdk="${2:-Microsoft.NET.Sdk}"
nuget_config="${3:-}"
trap 'echo "FAIL incremental-emission at line $LINENO; logs: $work" >&2' ERR
mkdir -p "$work/producer" "$work/kotlin-consumer" "$work/csharp-consumer"

write_kotlin_project() {
	local path="$1" reference="$2"
	cat > "$path" <<EOF
<Project Sdk="$sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
EOF
	if [[ "$sdk" == Microsoft.NET.Sdk ]]; then
		printf '  <Import Project="%s/eng/KotlinClr.targets" />\n' "$ROOT" >> "$path"
	fi
	if [[ -n "$reference" ]]; then
		printf '  <ItemGroup><ProjectReference Include="%s" /></ItemGroup>\n' "$reference" >> "$path"
	fi
	printf '</Project>\n' >> "$path"
}
write_kotlin_project "$work/producer/Probe.ktproj" ""
write_kotlin_project "$work/kotlin-consumer/Consumer.ktproj" "../producer/Probe.ktproj"
cat > "$work/producer/Probe.kt" <<'EOF'
package incremental
class Api { fun value(): Int = 42 }
fun answer(): Int = Api().value()
fun main() { println("PASS incremental Kotlin entry point: ${answer()}") }
EOF
cat > "$work/kotlin-consumer/Main.kt" <<'EOF'
import incremental.answer
fun main() { check(answer() == 42); println("PASS incremental Kotlin metadata consumer") }
EOF
cat > "$work/csharp-consumer/Consumer.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include="../producer/Probe.ktproj" /></ItemGroup>
</Project>
EOF
cat > "$work/csharp-consumer/Program.cs" <<'EOF'
if (new incremental.Api().value() != 42) throw new System.Exception("Kotlin body lost");
System.Console.WriteLine("PASS incremental C# consumer");
EOF

if [[ -n "$nuget_config" ]]; then
	cp "$nuget_config" "$work/nuget.config"
fi
producer="$work/producer/Probe.ktproj"
kotlin_consumer="$work/kotlin-consumer/Consumer.ktproj"
csharp_consumer="$work/csharp-consumer/Consumer.csproj"
obj="$work/producer/obj/Debug/net10.0"
producer_dll="$work/producer/bin/Debug/net10.0/Probe.dll"
kotlin_dll="$work/kotlin-consumer/bin/Debug/net10.0/Consumer.dll"
csharp_dll="$work/csharp-consumer/bin/Debug/net10.0/Consumer.dll"

build() {
	local project="$1" log="$2" revision="$3"
	dotnet build "$project" -m:1 -v:n --nologo -p:SourceRevisionId="$revision" > "$work/$log.log" 2>&1 \
		|| { tail -40 "$work/$log.log"; return 1; }
}
assert_output() {
	local dll="$1" expected="$2" actual
	actual="$(dotnet "$dll")"
	[[ "$actual" == "$expected" ]] || { echo "Unexpected output from $dll: '$actual'"; return 1; }
}
assert_runtime() {
	assert_output "$producer_dll" "PASS incremental Kotlin entry point: 42"
	assert_output "$kotlin_dll" "PASS incremental Kotlin metadata consumer"
	assert_output "$csharp_dll" "PASS incremental C# consumer"
}
file_state() { stat -c '%y|%s' "$1"; sha256sum "$1"; }

build "$producer" initial first
build "$kotlin_consumer" initial-kotlin first
build "$csharp_consumer" initial-csharp first
assert_runtime
bir_state="$(file_state "$obj/bir/.stamp")"
cir_state="$(file_state "$obj/cir/.stamp")"

# Both an AssemblyInfo-only change and an arbitrary C#-only option must leave actual Kotlin code in the DLL.
for mutation in revision nullable; do
	if [[ "$mutation" == nullable ]]; then
		sed -i 's/<Nullable>disable<\//<Nullable>enable<\//' "$producer"
	fi
	build "$producer" "$mutation" second
	[[ "$(file_state "$obj/bir/.stamp")" == "$bir_state" ]]
	[[ "$(file_state "$obj/cir/.stamp")" == "$cir_state" ]]
	grep -q 'DotKt: emitting CIL' "$work/$mutation.log"
	! grep -Eq 'DotKt: (compiling|lowering)' "$work/$mutation.log"
	assert_output "$producer_dll" "PASS incremental Kotlin entry point: 42"
	# Resolve a top-level Kotlin declaration through the emitted metadata, not a C# facade spelling.
	build "$kotlin_consumer" "$mutation-kotlin" second
	build "$csharp_consumer" "$mutation-csharp" second
	assert_runtime
	dll_state="$(file_state "$obj/Probe.dll")"
	bin_state="$(file_state "$producer_dll")"
	cs_state="$(file_state "$csharp_dll")"
	build "$csharp_consumer" "$mutation-noop" second
	[[ "$(file_state "$obj/Probe.dll")" == "$dll_state" ]]
	[[ "$(file_state "$producer_dll")" == "$bin_state" ]]
	[[ "$(file_state "$csharp_dll")" == "$cs_state" ]]
	! grep -Eq 'DotKt: (projecting|compiling|lowering|emitting)|/csc\.dll' "$work/$mutation-noop.log"
	grep -q 'Skipping target "CoreCompile"' "$work/$mutation-noop.log"
	assert_runtime
	echo "PASS incremental-emission:$mutation (metadata, runtime, C# no-op)"
done

# A missing emitted assembly must invalidate the emitter even though CIR is unchanged.
rm -f "$obj/Probe.dll"
build "$producer" deleted-output second
assert_output "$producer_dll" "PASS incremental Kotlin entry point: 42"
grep -q 'DotKt: emitting CIL' "$work/deleted-output.log"
[[ "$(file_state "$obj/cir/.stamp")" == "$cir_state" ]]
dotnet clean "$producer" -v:q --nologo > "$work/clean.log" 2>&1
[[ ! -e "$obj/Probe.dll" && ! -e "$obj/bir" && ! -e "$obj/cir" ]]
build "$producer" after-clean second
assert_output "$producer_dll" "PASS incremental Kotlin entry point: 42"
echo "PASS incremental-emission:deleted-output-and-clean"
