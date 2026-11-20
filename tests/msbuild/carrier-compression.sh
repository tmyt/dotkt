#!/usr/bin/env bash
# The identical Release producer -> Debug consumer contract runs through both
# the in-repo import and the packaged SDK, inspecting actual emitted attributes.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
work="${1:?scratch directory required}"
sdk="${2:-Microsoft.NET.Sdk}"
nuget_config="${3:-}"
mkdir -p "$work/producer" "$work/consumer"
trap 'echo "FAIL carrier-compression at line $LINENO; logs: $work" >&2' ERR
if [[ -n "$nuget_config" ]]; then cp "$nuget_config" "$work/nuget.config"; fi
for project in producer consumer; do
	cat > "$work/$project/$project.ktproj" <<EOF
<Project Sdk="$sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType Condition="'$project' == 'consumer'">Exe</OutputType>
    <DotKtCirFormat>json</DotKtCirFormat>
    <ProduceReferenceAssembly Condition="'$project' == 'producer'">true</ProduceReferenceAssembly>
  </PropertyGroup>
EOF
	if [[ "$sdk" == Microsoft.NET.Sdk ]]; then
		printf '  <Import Project="%s/eng/KotlinClr.targets" />\n' "$ROOT" >> "$work/$project/$project.ktproj"
	fi
	if [[ "$project" == consumer ]]; then
		cat >> "$work/$project/$project.ktproj" <<'EOF'
  <ItemGroup>
    <Reference Include="producer"><HintPath>../producer/bin/Release/net10.0/producer.dll</HintPath></Reference>
  </ItemGroup>
EOF
	fi
	printf '</Project>\n' >> "$work/$project/$project.ktproj"
done
python3 - "$work/producer/Library.kt" <<'PY'
import pathlib,sys
text = 'x' * 512 + r'\uD800\u0000\uDFFF'
pathlib.Path(sys.argv[1]).write_text('''package compression
typealias LargeAlias = (String, String, String, String, String, String, String, String) -> String
fun relay(value: LargeAlias): LargeAlias = value
enum class Choice(val code: Int) { First(10), Second(20) }
inline fun <reified E : Enum<E>> lookup(name: String): E = enumValueOf<E>(name)
var counter = 0
fun next(): Int { counter += 1; return counter }
inline fun <T> ordered(value: T, action: (T) -> T, first: Int = next(), second: Int = next(), marker: String = "''' + text + '''"): T {
    check(first == 1 && second == 2 && marker.length == 515)
    return action(value)
}
fun defaultText(value: String = "''' + text + '''"): String = value
fun tiny(value: Int = 7): Int = value
''')
PY
cat > "$work/consumer/Main.kt" <<'EOF'
import compression.*
fun main() {
    counter = 0
    check(ordered("ok", { it + "!" }) == "ok!" && counter == 2)
    val text = defaultText()
    check(text.length == 515 && text[512] == '\uD800' && text[513] == '\u0000' && text[514] == '\uDFFF')
    check(tiny() == 7)
    check(lookup<Choice>("Second") === Choice.Second && Choice.Second.name == "Second" && Choice.Second.ordinal == 1)
    val combine: LargeAlias = { a, b, c, d, e, f, g, h -> a + h }
    check(relay(combine)("a", "b", "c", "d", "e", "f", "g", "h") == "ah")
    println("PASS compressed Release dependency consumed by Debug")
}
EOF

producer="$work/producer/producer.ktproj"
dotnet build "$ROOT/tests/msbuild/carrier-inspector/Inspector.csproj" -o "$work/inspector" -p:NuGetAudit=false \
	-v q --nologo > "$work/inspector-build.log" 2>&1
for configuration in Debug Release; do
	dotnet build "$producer" -c "$configuration" -v q --nologo > "$work/producer-$configuration.log" 2>&1
	dotnet "$work/inspector/Inspector.dll" "$work/producer/bin/$configuration/net10.0/producer.dll" > "$work/carriers-$configuration.json"
	dotnet "$work/inspector/Inspector.dll" "$work/producer/obj/$configuration/net10.0/ref/producer.dll" > "$work/carriers-$configuration-ref.json"
done
python3 - "$work" <<'PY'
import base64,collections,json,pathlib,sys,zlib
p=pathlib.Path(sys.argv[1])
def read(configuration):
    records=json.loads((p/('carriers-'+configuration+'.json')).read_text())
    assert records, 'no real DLL carriers inspected'
    for record in records:
        raw=base64.b64decode(record['content'])
        if record['codec']=='bir-msgpack+deflate/1':
            decoder=zlib.decompressobj(wbits=-15)
            raw=decoder.decompress(raw)+decoder.flush()
            assert decoder.eof and not decoder.unused_data, 'not one complete raw DEFLATE stream'
        else: assert record['codec']=='bir-msgpack/1', record
        record['raw']=raw
    return records
debug,release=read('Debug'),read('Release')
assert all(r['codec']=='bir-msgpack/1' for r in debug), 'Debug performed carrier compression'
for kind in ('KotlinInlineAttribute','KotlinDefaultAttribute','KotlinTypeAliasAttribute','KotlinTypeAttribute','KotlinDeclarationIdentityAttribute'):
    assert any(r['name']==kind and r['codec']=='bir-msgpack+deflate/1' for r in release), kind+' missing compression'
assert any(r['name']=='KotlinDefaultAttribute' and len(r['raw'])<128 and r['codec']=='bir-msgpack/1' for r in release), 'small default lost fallback'
identity=lambda rows: collections.Counter((r['name'],r['index'],r['raw']) for r in rows)
assert identity(debug)==identity(release), 'Release transport changed logical payload bytes'
for configuration,implementation in (('Debug',debug),('Release',release)):
    reference=read(configuration+'-ref')
    assert identity(reference)==identity(implementation), 'reference body stripping changed carrier payloads'
    assert [r['codec'] for r in reference]==[r['codec'] for r in implementation], 'reference body stripping changed codecs'
for configuration in ('Debug','Release'):
    response=(p/'producer/obj'/configuration/'net10.0/dotkt-bir2cir.rsp').read_text().splitlines()
    assert response[response.index('--configuration')+1]==configuration, 'target configuration was not forwarded'
print('PASS actual DLL codecs, raw DEFLATE, small fallback, identical logical BIR and target configuration')
PY
# The CIR directory is an overridable shared intermediate. Reusing it across
# configurations must not reuse a transport selected for the previous producer.
for configuration in Release Debug Release; do
	dotnet build "$producer" -c "$configuration" -p:DotKtIntermediateCirPath="$work/shared-cir" \
		-v q --nologo > "$work/shared-$configuration.log" 2>&1
	dotnet "$work/inspector/Inspector.dll" "$work/producer/bin/$configuration/net10.0/producer.dll" > "$work/shared.json"
	python3 - "$work/shared.json" "$configuration" <<'PY'
import json,sys
records=json.load(open(sys.argv[1]))
if sys.argv[2]=='Release':
    assert any(r['codec']=='bir-msgpack+deflate/1' for r in records), 'shared CIR reused Debug transport'
else:
    assert records and all(r['codec']=='bir-msgpack/1' for r in records), 'shared CIR reused Release transport'
PY
done
dotnet run --project "$work/consumer/consumer.ktproj" -c Debug -v q --nologo > "$work/consumer.log" 2>&1
grep -Fx 'PASS compressed Release dependency consumed by Debug' "$work/consumer.log"
