#!/usr/bin/env bash
# The same stateful DLL-ownership regression runs through the dev import and the shipping SDK.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
work="${1:?scratch directory required}"
sdk="${2:-Microsoft.NET.Sdk}"
nuget_config="${3:-}"
trap 'echo "FAIL incremental-emission at line $LINENO; logs: $work" >&2' ERR
rm -f "$work/producer/fail-hook"
mkdir -p "$work/producer" "$work/kotlin-consumer" "$work/csharp-consumer"

write_kotlin_project() {
	local path="$1" reference="$2"
	cat > "$path" <<EOF
<Project Sdk="$sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <TargetsTriggeredByCompilation>AssertExistingCompilationHook</TargetsTriggeredByCompilation>
  </PropertyGroup>
EOF
	if [[ "$sdk" == Microsoft.NET.Sdk ]]; then
		printf '  <Import Project="%s/eng/KotlinClr.targets" />\n' "$ROOT" >> "$path"
	fi
	if [[ -n "$reference" ]]; then
		printf '  <ItemGroup><ProjectReference Include="%s" /></ItemGroup>\n' "$reference" >> "$path"
	fi
	cat >> "$path" <<'EOF'
  <Target Name="AssertExistingCompilationHook">
    <GetAssemblyIdentity AssemblyFiles="@(IntermediateAssembly)" />
    <Error Condition="Exists('$(MSBuildProjectDirectory)/fail-hook')" Text="Intentional existing hook failure" />
  </Target>
</Project>
EOF
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

# Observe the POSIX launcher on builds this suite already needs. The batch
# launcher requires java.exe, so keep the original builds on that host.
build_with_java_probe() (
	local mode="$1"; shift
	if [[ ${OS:-} == Windows_NT ]]; then build "$@"; return; fi
	local base; base="$(cd "$work" && pwd)"
	local home_probe="$base/selected; java"
	if [[ -n "${JAVA_HOME:-}" ]]; then DOTKT_TEST_REAL_JAVA="$JAVA_HOME/bin/java"; else DOTKT_TEST_REAL_JAVA="$(command -v java)"; fi
	export DOTKT_TEST_REAL_JAVA DOTKT_TEST_JAVA_LOG="$base/$mode-java.log"
	: > "$DOTKT_TEST_JAVA_LOG"
	mkdir -p "$home_probe/bin"
	cat > "$home_probe/bin/java" <<'EOF'
#!/bin/sh
printf '%s\n' "$JAVA_HOME" > "$DOTKT_TEST_JAVA_LOG"
exec "$DOTKT_TEST_REAL_JAVA" "$@"
EOF
	chmod +x "$home_probe/bin/java"
	if [[ $mode == inherited ]]; then export JAVA_HOME="$home_probe" DotKtJavaHome=
	else export JAVA_HOME="$base/missing-java" DotKtJavaHome="$home_probe"; fi
	build "$@"
	[[ "$(cat "$DOTKT_TEST_JAVA_LOG")" == "$home_probe" ]]
)
build_with_java_probe inherited "$producer" initial first
build "$kotlin_consumer" initial-kotlin first
build "$csharp_consumer" initial-csharp first
assert_runtime
bir_state="$(file_state "$obj/bir/.stamp")"
cir_state="$(file_state "$obj/cir/.stamp")"

# An assembly-metadata change must refresh emission. A C#-only option no longer has a compiler to invalidate;
# neither change may recompile Kotlin or replace Kotlin declarations/bodies with a different language's output.
for mutation in revision nullable; do
	if [[ "$mutation" == nullable ]]; then
		sed -i 's/<Nullable>disable<\//<Nullable>enable<\//' "$producer"
	fi
	build "$producer" "$mutation" second
	[[ "$(file_state "$obj/bir/.stamp")" == "$bir_state" ]]
	[[ "$(file_state "$obj/cir/.stamp")" == "$cir_state" ]]
	if [[ "$mutation" == revision ]]; then
		grep -q 'DotKt: emitting CIL' "$work/$mutation.log"
	elif grep -q 'DotKt: emitting CIL' "$work/$mutation.log"; then
		echo "C#-only option unnecessarily re-emitted Kotlin"; exit 1
	fi
	if grep -Eq 'DotKt: (compiling|lowering)' "$work/$mutation.log"; then
		echo "C#-only change reran Kotlin compilation/lowering"; exit 1
	fi
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
	if grep -Eq 'DotKt: (projecting|compiling|lowering|emitting)|/csc\.dll' "$work/$mutation-noop.log"; then
		echo "No-op build invoked a compiler stage"; exit 1
	fi
	grep -q 'Skipping target "DotKtIlEmit"' "$work/$mutation-noop.log"
	assert_runtime
	echo "PASS incremental-emission:$mutation (metadata, runtime, C# no-op)"
done

# A failed existing compilation hook must leave emission incomplete, and an unchanged retry must recover.
touch "$work/producer/fail-hook"
if build "$producer" failed-hook third > "$work/failed-hook-expected.log" 2>&1; then
	echo "Expected existing compilation hook to fail"; exit 1
fi
grep -q 'Intentional existing hook failure' "$work/failed-hook.log"
rm "$work/producer/fail-hook"
build "$producer" hook-retry third
grep -q 'DotKt: emitting CIL' "$work/hook-retry.log"
assert_output "$producer_dll" "PASS incremental Kotlin entry point: 42"
[[ "$(file_state "$obj/cir/.stamp")" == "$cir_state" ]]
echo "PASS incremental-emission:failed-hook-retry"

# A missing emitted assembly must invalidate the emitter even though CIR is unchanged.
rm -f "$obj/Probe.dll"
build "$producer" deleted-output third
assert_output "$producer_dll" "PASS incremental Kotlin entry point: 42"
grep -q 'DotKt: emitting CIL' "$work/deleted-output.log"
[[ "$(file_state "$obj/cir/.stamp")" == "$cir_state" ]]
dotnet clean "$producer" -v:q --nologo > "$work/clean.log" 2>&1
[[ ! -e "$obj/Probe.dll" && ! -e "$obj/bir" && ! -e "$obj/cir" && ! -e "$obj/dotkt-ilemit.stamp" ]]
build_with_java_probe configured "$producer" after-clean third
assert_output "$producer_dll" "PASS incremental Kotlin entry point: 42"
echo "PASS incremental-emission:deleted-output-and-clean"
