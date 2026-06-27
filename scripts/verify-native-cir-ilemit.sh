#!/usr/bin/env bash
# Verify the first executable native-CIR bridge:
# BIR -> bir2cir --native-cir -> ilemit consuming cirDraft.executableCir -> runnable IL.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/native-cir-ilemit-verify"
CIR="$OUT/cir"
IL="$OUT/il"

rm -rf "$OUT"
mkdir -p "$CIR" "$IL"

dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
dotnet build "$ROOT/toolchain/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null
dotnet build "$ROOT/cases/ktproj-il/hello-il.ktproj" -v minimal --nologo >/dev/null

BIR="$ROOT/cases/ktproj-il/obj/dotkt-bir/App.bir.json"
REF="$ROOT/cases/ktproj-il/bin/Debug/net10.0/hello-il.dll"

dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$CIR" --native-cir --ref "$REF" "$BIR" >/dev/null
CIR_FILE="$CIR/App.cir.json"

require() {
    local pattern="$1" label="$2"
    if ! rg -q "$pattern" "$CIR_FILE"; then
        echo "FAIL  native CIR missing $label ($pattern)" >&2
        exit 1
    fi
}

require '"executableCir"' "executable native CIR payload"
require '"k": "clr.newobj"' "native constructor"
require '"k": "clr.call"' "native call"
require '"type": "clr:Greeter"' "reference type normalization"

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$IL" NativeCirApp --ref "$REF" "$CIR_FILE" >/dev/null
cp "$REF" "$IL/hello-il.dll"

RUN_OUT="$(dotnet "$IL/NativeCirApp.dll")"
EXPECTED=$'Hello, ktproj, from IL!\nsum 1..5 = 15'
if [[ "$RUN_OUT" != "$EXPECTED" ]]; then
    echo "FAIL  native CIR ilemit run output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$EXPECTED" "$RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR envelope is consumable by ilemit"

FIELD="$OUT/field"
mkdir -p "$FIELD/src" "$FIELD/cir" "$FIELD/il"
cat > "$FIELD/src/FieldRef.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
cat > "$FIELD/src/FieldBox.cs" <<'EOF'
public sealed class FieldBox
{
    public int Value = 3;
    public static string StaticValue = "S0";
}
EOF
cat > "$FIELD/FieldApp.bir.json" <<'EOF'
{
  "fileClass": "FieldAppKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "main",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [],
      "ret": "void",
      "body": [
        {"k":"var","name":"box","type":"@FieldBox","init":{"k":"new","type":"FieldBox","args":[]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"field","ownerType":"FieldBox","recv":{"k":"local","name":"box"},"name":"Value"}]}},
        {"k":"exprStmt","expr":{"k":"setFieldExpr","ownerType":"FieldBox","recv":{"k":"local","name":"box"},"name":"Value","value":{"k":"const","type":"int","value":7}}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"field","ownerType":"FieldBox","recv":{"k":"local","name":"box"},"name":"Value"}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"staticField","ownerType":"FieldBox","name":"StaticValue"}]}},
        {"k":"exprStmt","expr":{"k":"staticFieldSet","ownerType":"FieldBox","name":"StaticValue","value":{"k":"const","type":"string","value":"S1"}}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"staticField","ownerType":"FieldBox","name":"StaticValue"}]}}
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet build "$FIELD/src/FieldRef.csproj" -v q --nologo >/dev/null
FIELD_REF="$FIELD/src/bin/Debug/net10.0/FieldRef.dll"
dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$FIELD/cir" --native-cir --ref "$FIELD_REF" "$FIELD/FieldApp.bir.json" >/dev/null
FIELD_CIR="$FIELD/cir/FieldApp.cir.json"

for pattern in '"k": "clr.ldfld"' '"k": "clr.stfld"' '"k": "clr.ldsfld"' '"k": "clr.stsfld"'; do
    if ! rg -q "$pattern" "$FIELD_CIR"; then
        echo "FAIL  field native CIR missing $pattern" >&2
        exit 1
    fi
done

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$FIELD/il" FieldNativeApp --ref "$FIELD_REF" "$FIELD_CIR" >/dev/null
cp "$FIELD_REF" "$FIELD/il/FieldRef.dll"
FIELD_RUN_OUT="$(dotnet "$FIELD/il/FieldNativeApp.dll")"
FIELD_EXPECTED=$'3\n7\nS0\nS1'
if [[ "$FIELD_RUN_OUT" != "$FIELD_EXPECTED" ]]; then
    echo "FAIL  native CIR field run output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$FIELD_EXPECTED" "$FIELD_RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR bridge lowers reference fields for ilemit"

GEN="$OUT/generic"
mkdir -p "$GEN/src" "$GEN/cir" "$GEN/il"
cat > "$GEN/src/GenericRef.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
cat > "$GEN/src/GenericProbe.cs" <<'EOF'
public sealed class GenericProbe
{
    public static T Echo<T>(T value) => value;
    public T Id<T>(T value) => value;
}
EOF
cat > "$GEN/GenericApp.bir.json" <<'EOF'
{
  "fileClass": "GenericAppKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "main",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [],
      "ret": "void",
      "body": [
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"callStatic","owner":"GenericProbe","method":"Echo","sig":"int","typeArgs":["int"],"args":[{"k":"const","type":"int","value":42}]}]}},
        {"k":"var","name":"probe","type":"@GenericProbe","init":{"k":"new","type":"GenericProbe","args":[]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"callInstance","ownerType":"GenericProbe","recv":{"k":"local","name":"probe"},"method":"Id","sig":"string","typeArgs":["string"],"virtual":false,"args":[{"k":"const","type":"string","value":"ok"}]}]}}
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet build "$GEN/src/GenericRef.csproj" -v q --nologo >/dev/null
GEN_REF="$GEN/src/bin/Debug/net10.0/GenericRef.dll"
dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$GEN/cir" --native-cir --ref "$GEN_REF" "$GEN/GenericApp.bir.json" >/dev/null
GEN_CIR="$GEN/cir/GenericApp.cir.json"

if [[ "$(rg -c '"typeArgs"' "$GEN_CIR")" -lt 2 ]]; then
    echo "FAIL  generic native CIR did not preserve typeArgs" >&2
    exit 1
fi
if [[ "$(rg -c '"k": "clr.call"' "$GEN_CIR")" -lt 2 ]]; then
    echo "FAIL  generic native CIR missing clr.call nodes" >&2
    exit 1
fi

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$GEN/il" GenericNativeApp --ref "$GEN_REF" "$GEN_CIR" >/dev/null
cp "$GEN_REF" "$GEN/il/GenericRef.dll"
GEN_RUN_OUT="$(dotnet "$GEN/il/GenericNativeApp.dll")"
GEN_EXPECTED=$'42\nok'
if [[ "$GEN_RUN_OUT" != "$GEN_EXPECTED" ]]; then
    echo "FAIL  native CIR generic method output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$GEN_EXPECTED" "$GEN_RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR bridge emits generic methods"

GOWNER="$OUT/generic-owner"
mkdir -p "$GOWNER/src" "$GOWNER/cir" "$GOWNER/il"
cat > "$GOWNER/src/GenericOwnerRef.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
cat > "$GOWNER/src/GenericBox.cs" <<'EOF'
public sealed class GenericBox<T>
{
    public T Value;
    public GenericBox(T value) { Value = value; }
    public T Get() => Value;
    public void Set(T value) { Value = value; }
}
EOF
cat > "$GOWNER/GenericOwnerApp.bir.json" <<'EOF'
{
  "fileClass": "GenericOwnerAppKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "main",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [],
      "ret": "void",
      "body": [
        {"k":"var","name":"box","type":"clrg:GenericBox[int]","init":{"k":"new","type":"clrg:GenericBox[int]","args":[{"k":"const","type":"int","value":5}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"callInstance","ownerType":"clrg:GenericBox[int]","recv":{"k":"local","name":"box"},"method":"Get","sig":"","virtual":false,"args":[]}]}},
        {"k":"exprStmt","expr":{"k":"callInstance","ownerType":"clrg:GenericBox[int]","recv":{"k":"local","name":"box"},"method":"Set","sig":"int","virtual":false,"args":[{"k":"const","type":"int","value":9}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"field","ownerType":"clrg:GenericBox[int]","recv":{"k":"local","name":"box"},"name":"Value"}]}}
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet build "$GOWNER/src/GenericOwnerRef.csproj" -v q --nologo >/dev/null
GOWNER_REF="$GOWNER/src/bin/Debug/net10.0/GenericOwnerRef.dll"
dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$GOWNER/cir" --native-cir --ref "$GOWNER_REF" "$GOWNER/GenericOwnerApp.bir.json" >/dev/null
GOWNER_CIR="$GOWNER/cir/GenericOwnerApp.cir.json"

if ! rg -q '"ownerType": "clrg:GenericBox\[int\]"' "$GOWNER_CIR"; then
    echo "FAIL  generic owner native CIR did not preserve constructed ownerType" >&2
    exit 1
fi
for pattern in '"k": "clr.newobj"' '"k": "clr.call"' '"k": "clr.ldfld"'; do
    if ! rg -q "$pattern" "$GOWNER_CIR"; then
        echo "FAIL  generic owner native CIR missing $pattern" >&2
        exit 1
    fi
done

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$GOWNER/il" GenericOwnerNativeApp --ref "$GOWNER_REF" "$GOWNER_CIR" >/dev/null
cp "$GOWNER_REF" "$GOWNER/il/GenericOwnerRef.dll"
GOWNER_RUN_OUT="$(dotnet "$GOWNER/il/GenericOwnerNativeApp.dll")"
GOWNER_EXPECTED=$'5\n9'
if [[ "$GOWNER_RUN_OUT" != "$GOWNER_EXPECTED" ]]; then
    echo "FAIL  native CIR generic owner output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$GOWNER_EXPECTED" "$GOWNER_RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR bridge emits constructed generic owners"

PROP="$OUT/property"
mkdir -p "$PROP/src" "$PROP/cir" "$PROP/il"
cat > "$PROP/src/PropertyRef.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
cat > "$PROP/src/PropertyProbe.cs" <<'EOF'
public sealed class PropertyProbe
{
    public int Number { get; set; } = 2;
    public static string Label { get; set; } = "L0";
}
EOF
cat > "$PROP/PropertyApp.bir.json" <<'EOF'
{
  "fileClass": "PropertyAppKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "main",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [],
      "ret": "void",
      "body": [
        {"k":"var","name":"probe","type":"clr:PropertyProbe","init":{"k":"new","type":"PropertyProbe","args":[]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"clrPropGet","type":"PropertyProbe","name":"Number","static":false,"recv":{"k":"local","name":"probe"}}]}},
        {"k":"exprStmt","expr":{"k":"clrPropSet","type":"PropertyProbe","name":"Number","static":false,"recv":{"k":"local","name":"probe"},"value":{"k":"const","type":"int","value":6}}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"clrPropGet","type":"PropertyProbe","name":"Number","static":false,"recv":{"k":"local","name":"probe"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"clrPropGet","type":"PropertyProbe","name":"Label","static":true}]}},
        {"k":"exprStmt","expr":{"k":"clrPropSet","type":"PropertyProbe","name":"Label","static":true,"value":{"k":"const","type":"string","value":"L1"}}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"clrPropGet","type":"PropertyProbe","name":"Label","static":true}]}}
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet build "$PROP/src/PropertyRef.csproj" -v q --nologo >/dev/null
PROP_REF="$PROP/src/bin/Debug/net10.0/PropertyRef.dll"
dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$PROP/cir" --native-cir --ref "$PROP_REF" "$PROP/PropertyApp.bir.json" >/dev/null
PROP_CIR="$PROP/cir/PropertyApp.cir.json"

for pattern in '"sourceKind": "clrPropGet"' '"sourceKind": "clrPropSet"' '"name": "get_Number"' '"name": "set_Label"'; do
    if ! rg -q "$pattern" "$PROP_CIR"; then
        echo "FAIL  property native CIR missing $pattern" >&2
        exit 1
    fi
done

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$PROP/il" PropertyNativeApp --ref "$PROP_REF" "$PROP_CIR" >/dev/null
cp "$PROP_REF" "$PROP/il/PropertyRef.dll"
PROP_RUN_OUT="$(dotnet "$PROP/il/PropertyNativeApp.dll")"
PROP_EXPECTED=$'2\n6\nL0\nL1'
if [[ "$PROP_RUN_OUT" != "$PROP_EXPECTED" ]]; then
    echo "FAIL  native CIR property output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$PROP_EXPECTED" "$PROP_RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR bridge lowers physical property nodes"

EVENT="$OUT/event"
mkdir -p "$EVENT/src" "$EVENT/cir" "$EVENT/il"
cat > "$EVENT/src/EventRef.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
cat > "$EVENT/src/EventProbe.cs" <<'EOF'
public sealed class EventProbe
{
    public event System.Action<int> Changed;
    public void Fire(int value) => Changed?.Invoke(value);
}
EOF
cat > "$EVENT/EventApp.bir.json" <<'EOF'
{
  "fileClass": "EventAppKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "onChanged",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [{"name":"value","type":"int"}],
      "ret": "void",
      "body": [
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"local","name":"value"}]}}
      ],
      "attrs": []
    },
    {
      "name": "main",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [],
      "ret": "void",
      "body": [
        {"k":"var","name":"probe","type":"clr:EventProbe","init":{"k":"new","type":"EventProbe","args":[]}},
        {"k":"exprStmt","expr":{"k":"clrEventAdd","type":"EventProbe","event":"Changed","static":false,"recv":{"k":"local","name":"probe"},"handler":{"k":"delegateNew","method":"onChanged","funcType":"func:void:int"}}},
        {"k":"exprStmt","expr":{"k":"callInstance","ownerType":"EventProbe","recv":{"k":"local","name":"probe"},"method":"Fire","sig":"int","virtual":false,"args":[{"k":"const","type":"int","value":11}]}},
        {"k":"exprStmt","expr":{"k":"clrEventRemove","type":"EventProbe","event":"Changed","static":false,"recv":{"k":"local","name":"probe"},"handler":{"k":"delegateNew","method":"onChanged","funcType":"func:void:int"}}},
        {"k":"exprStmt","expr":{"k":"callInstance","ownerType":"EventProbe","recv":{"k":"local","name":"probe"},"method":"Fire","sig":"int","virtual":false,"args":[{"k":"const","type":"int","value":12}]}}
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet build "$EVENT/src/EventRef.csproj" -v q --nologo >/dev/null
EVENT_REF="$EVENT/src/bin/Debug/net10.0/EventRef.dll"
dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$EVENT/cir" --native-cir --ref "$EVENT_REF" "$EVENT/EventApp.bir.json" >/dev/null
EVENT_CIR="$EVENT/cir/EventApp.cir.json"

for pattern in '"sourceKind": "clrEventAdd"' '"sourceKind": "clrEventRemove"' '"name": "add_Changed"' '"name": "remove_Changed"'; do
    if ! rg -q "$pattern" "$EVENT_CIR"; then
        echo "FAIL  event native CIR missing $pattern" >&2
        exit 1
    fi
done

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$EVENT/il" EventNativeApp --ref "$EVENT_REF" "$EVENT_CIR" >/dev/null
cp "$EVENT_REF" "$EVENT/il/EventRef.dll"
EVENT_RUN_OUT="$(dotnet "$EVENT/il/EventNativeApp.dll")"
EVENT_EXPECTED="11"
if [[ "$EVENT_RUN_OUT" != "$EVENT_EXPECTED" ]]; then
    echo "FAIL  native CIR event output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$EVENT_EXPECTED" "$EVENT_RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR bridge lowers physical event nodes"

TYPEOPS="$OUT/type-ops"
mkdir -p "$TYPEOPS/cir" "$TYPEOPS/il"
cat > "$TYPEOPS/TypeOpsApp.bir.json" <<'EOF'
{
  "fileClass": "TypeOpsAppKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "main",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [],
      "ret": "void",
      "body": [
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"conv","to":"long","e":{"k":"const","type":"int","value":7}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"isinst","type":"string","e":{"k":"const","type":"string","value":"hi"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"cast","type":"string","e":{"k":"const","type":"string","value":"cast-ok"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"isinstRef","type":"string","e":{"k":"const","type":"string","value":"ref-ok"}}]}},
        {"k":"var","name":"boxedInt","type":"object","init":{"k":"const","type":"int","value":8}},
        {"k":"var","name":"maybeInt","type":"nullable:int","init":{"k":"safeCastValue","elem":"int","e":{"k":"local","name":"boxedInt"}}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"nullableHasValue","elem":"int","e":{"k":"local","name":"maybeInt"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"nullableValue","elem":"int","e":{"k":"local","name":"maybeInt"}}]}},
        {"k":"var","name":"boxedString","type":"object","init":{"k":"const","type":"string","value":"nope"}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"nullableHasValue","elem":"int","e":{"k":"safeCastValue","elem":"int","e":{"k":"local","name":"boxedString"}}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"nullableHasValue","elem":"int","e":{"k":"nullableNull","elem":"int"}}]}},
        {"k":"var","name":"wrappedInt","type":"nullable:int","init":{"k":"nullableWrap","elem":"int","e":{"k":"const","type":"int","value":13}}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"nullableValue","elem":"int","e":{"k":"local","name":"wrappedInt"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"classRef","type":"int"}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"getType","e":{"k":"const","type":"string","value":"runtime"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"enumValue","type":"clr:System.DayOfWeek","ordinal":1}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"enumOrdinal","e":{"k":"enumValue","type":"clr:System.DayOfWeek","ordinal":1}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"arrayLen","array":{"k":"enumValues","type":"clr:System.DayOfWeek"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"enumParse","type":"clr:System.DayOfWeek","arg":{"k":"const","type":"string","value":"Friday"}}]}}
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$TYPEOPS/cir" --native-cir "$TYPEOPS/TypeOpsApp.bir.json" >/dev/null
TYPEOPS_CIR="$TYPEOPS/cir/TypeOpsApp.cir.json"

for pattern in '"k": "clr.conv"' '"k": "clr.isinst"' '"k": "clr.castclass"' '"k": "clr.isinst.ref"' '"k": "clr.safeCast.value"' '"k": "clr.nullable.null"' '"k": "clr.nullable.wrap"' '"k": "clr.nullable.hasValue"' '"k": "clr.nullable.value"' '"k": "clr.typeof"' '"k": "clr.getType"' '"k": "clr.enum.value"' '"k": "clr.enum.ordinal"' '"k": "clr.enum.values"' '"k": "clr.enum.parse"'; do
    if ! rg -q "$pattern" "$TYPEOPS_CIR"; then
        echo "FAIL  type-op native CIR missing $pattern" >&2
        exit 1
    fi
done

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$TYPEOPS/il" TypeOpsNativeApp "$TYPEOPS_CIR" >/dev/null
TYPEOPS_RUN_OUT="$(dotnet "$TYPEOPS/il/TypeOpsNativeApp.dll")"
TYPEOPS_EXPECTED=$'7\nTrue\ncast-ok\nref-ok\nTrue\n8\nFalse\nFalse\n13\nSystem.Int32\nSystem.String\nMonday\n1\n7\nFriday'
if [[ "$TYPEOPS_RUN_OUT" != "$TYPEOPS_EXPECTED" ]]; then
    echo "FAIL  native CIR type-op output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$TYPEOPS_EXPECTED" "$TYPEOPS_RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR bridge lowers physical type operations"

OBJOPS="$OUT/obj-ops"
mkdir -p "$OBJOPS/cir" "$OBJOPS/il"
cat > "$OBJOPS/ObjOpsApp.bir.json" <<'EOF'
{
  "fileClass": "ObjOpsAppKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "main",
      "static": true,
      "override": false,
      "virtual": false,
      "abstract": false,
      "objectOverride": false,
      "vis": "public",
      "params": [],
      "ret": "void",
      "body": [
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"objEq","l":{"k":"const","type":"string","value":"hi"},"r":{"k":"const","type":"string","value":"hi"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"objEq","l":{"k":"const","type":"string","value":"hi"},"r":{"k":"const","type":"string","value":"bye"}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"objMethod","method":"ToString","recv":{"k":"const","type":"int","value":42}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"objMethod","method":"Equals","recv":{"k":"const","type":"int","value":7},"arg":{"k":"const","type":"int","value":7}}]}},
        {"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"objMethod","method":"Equals","recv":{"k":"const","type":"int","value":7},"arg":{"k":"const","type":"int","value":8}}]}}
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$OBJOPS/cir" --native-cir "$OBJOPS/ObjOpsApp.bir.json" >/dev/null
OBJOPS_CIR="$OBJOPS/cir/ObjOpsApp.cir.json"

for pattern in '"k": "clr.obj.eq"' '"k": "clr.obj.method"' '"sourceKind": "objEq"' '"sourceKind": "objMethod"'; do
    if ! rg -q "$pattern" "$OBJOPS_CIR"; then
        echo "FAIL  obj-op native CIR missing $pattern" >&2
        exit 1
    fi
done

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$OBJOPS/il" ObjOpsNativeApp "$OBJOPS_CIR" >/dev/null
OBJOPS_RUN_OUT="$(dotnet "$OBJOPS/il/ObjOpsNativeApp.dll")"
OBJOPS_EXPECTED=$'True\nFalse\n42\nTrue\nFalse'
if [[ "$OBJOPS_RUN_OUT" != "$OBJOPS_EXPECTED" ]]; then
    echo "FAIL  native CIR obj-op output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$OBJOPS_EXPECTED" "$OBJOPS_RUN_OUT" >&2
    exit 1
fi

echo "PASS  native CIR bridge lowers physical object operations"

WRAP="$OUT/wrapper"
WRAP_OUT="$("$ROOT/scripts/dotkt.sh" --native-cir --no-stdlib --run -d "$WRAP" "$ROOT/cases/m0/M0.kt")"
WRAP_EXPECTED="$(printf 'emitted M0Kt.dll\ndotkt: built %s/M0Kt.dll\n----\nsum = 5\nzero\nn=1\nn=2' "$WRAP")"
if [[ "$WRAP_OUT" != "$WRAP_EXPECTED" ]]; then
    echo "FAIL  dotkt --native-cir wrapper output mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$WRAP_EXPECTED" "$WRAP_OUT" >&2
    exit 1
fi

echo "PASS  dotkt --native-cir developer path"
