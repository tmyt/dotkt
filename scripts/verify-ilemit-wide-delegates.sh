#!/usr/bin/env bash
# Regression check for function types wider than System.Func/Action supports.
#
# System.Func tops out at 16 value parameters plus TResult (Func`17). Kotlin
# function values can be wider, so ilemit synthesizes module-local delegate
# types DotKt.Runtime.CompilerServices.KFunc`N / KAction`N when needed.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/ilemit-wide-delegates"
rm -rf "$OUT"
mkdir -p "$OUT/bir" "$OUT/il"

dotnet build "$ROOT/toolchain/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null
dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo >/dev/null
dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null

cat > "$OUT/bir/Wide.bir.json" <<'EOF'
{
  "fileClass": "WideKt",
  "hasMain": true,
  "fields": [],
  "methods": [
    {
      "name": "__lambda0",
      "static": true,
      "override": false,
      "virtual": false,
      "params": [
        {"name":"p1","type":"int"},{"name":"p2","type":"int"},{"name":"p3","type":"int"},{"name":"p4","type":"int"},
        {"name":"p5","type":"int"},{"name":"p6","type":"int"},{"name":"p7","type":"int"},{"name":"p8","type":"int"},
        {"name":"p9","type":"int"},{"name":"p10","type":"int"},{"name":"p11","type":"int"},{"name":"p12","type":"int"},
        {"name":"p13","type":"int"},{"name":"p14","type":"int"},{"name":"p15","type":"int"},{"name":"p16","type":"int"},
        {"name":"p17","type":"int"}
      ],
      "ret": "int",
      "body": [{"k":"return","value":{"k":"local","name":"p17"}}]
    },
    {
      "name": "__lambda1",
      "static": true,
      "override": false,
      "virtual": false,
      "params": [
        {"name":"p1","type":"int"},{"name":"p2","type":"int"},{"name":"p3","type":"int"},{"name":"p4","type":"int"},
        {"name":"p5","type":"int"},{"name":"p6","type":"int"},{"name":"p7","type":"int"},{"name":"p8","type":"int"},
        {"name":"p9","type":"int"},{"name":"p10","type":"int"},{"name":"p11","type":"int"},{"name":"p12","type":"int"},
        {"name":"p13","type":"int"},{"name":"p14","type":"int"},{"name":"p15","type":"int"},{"name":"p16","type":"int"},
        {"name":"p17","type":"int"}
      ],
      "ret": "void",
      "body": [{"k":"exprStmt","expr":{"k":"console","method":"WriteLine","args":[{"k":"local","name":"p17"}]}}]
    },
    {
      "name": "accept",
      "static": true,
      "override": false,
      "virtual": false,
      "params": [
        {
          "name": "cb",
          "type": "func:int:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int"
        }
      ],
      "ret": "int",
      "body": [
        {
          "k": "return",
          "value": {
            "k": "delegateInvoke",
            "funcType": "func:int:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int",
            "recv": {"k":"local","name":"cb"},
            "args": [
              {"k":"const","type":"int","value":1},{"k":"const","type":"int","value":2},{"k":"const","type":"int","value":3},{"k":"const","type":"int","value":4},
              {"k":"const","type":"int","value":5},{"k":"const","type":"int","value":6},{"k":"const","type":"int","value":7},{"k":"const","type":"int","value":8},
              {"k":"const","type":"int","value":9},{"k":"const","type":"int","value":10},{"k":"const","type":"int","value":11},{"k":"const","type":"int","value":12},
              {"k":"const","type":"int","value":13},{"k":"const","type":"int","value":14},{"k":"const","type":"int","value":15},{"k":"const","type":"int","value":16},
              {"k":"const","type":"int","value":17}
            ]
          }
        }
      ]
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
        {
          "k": "var",
          "name": "f",
          "type": "func:int:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int",
          "init": {
            "k": "delegateNew",
            "method": "__lambda0",
            "funcType": "func:int:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int"
          }
        },
        {
          "k": "var",
          "name": "a",
          "type": "func:void:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int",
          "init": {
            "k": "delegateNew",
            "method": "__lambda1",
            "funcType": "func:void:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int"
          }
        },
        {
          "k": "exprStmt",
          "expr": {
            "k": "console",
            "method": "WriteLine",
            "args": [
              {
                "k": "delegateInvoke",
                "funcType": "func:int:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int",
                "recv": {"k":"local","name":"f"},
                "args": [
                  {"k":"const","type":"int","value":1},{"k":"const","type":"int","value":2},{"k":"const","type":"int","value":3},{"k":"const","type":"int","value":4},
                  {"k":"const","type":"int","value":5},{"k":"const","type":"int","value":6},{"k":"const","type":"int","value":7},{"k":"const","type":"int","value":8},
                  {"k":"const","type":"int","value":9},{"k":"const","type":"int","value":10},{"k":"const","type":"int","value":11},{"k":"const","type":"int","value":12},
                  {"k":"const","type":"int","value":13},{"k":"const","type":"int","value":14},{"k":"const","type":"int","value":15},{"k":"const","type":"int","value":16},
                  {"k":"const","type":"int","value":17}
                ]
              }
            ]
          }
        },
        {
          "k": "exprStmt",
          "expr": {
            "k": "delegateInvoke",
            "funcType": "func:void:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int",
            "recv": {"k":"local","name":"a"},
            "args": [
              {"k":"const","type":"int","value":1},{"k":"const","type":"int","value":2},{"k":"const","type":"int","value":3},{"k":"const","type":"int","value":4},
              {"k":"const","type":"int","value":5},{"k":"const","type":"int","value":6},{"k":"const","type":"int","value":7},{"k":"const","type":"int","value":8},
              {"k":"const","type":"int","value":9},{"k":"const","type":"int","value":10},{"k":"const","type":"int","value":11},{"k":"const","type":"int","value":12},
              {"k":"const","type":"int","value":13},{"k":"const","type":"int","value":14},{"k":"const","type":"int","value":15},{"k":"const","type":"int","value":16},
              {"k":"const","type":"int","value":17}
            ]
          }
        }
      ],
      "attrs": []
    }
  ],
  "types": []
}
EOF

dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$OUT/il" Wide "$OUT/bir/Wide.bir.json" >/dev/null
actual="$(dotnet "$OUT/il/Wide.dll")"
expected="$(printf '17\n17')"
if [[ "$actual" != "$expected" ]]; then
    echo "FAIL  wide delegate invocation" >&2
    printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual" >&2
    exit 1
fi

if ! strings "$OUT/il/Wide.dll" | rg -q 'KFunc`18'; then
    echo "FAIL  emitted assembly is missing KFunc\`18" >&2
    exit 1
fi
if ! strings "$OUT/il/Wide.dll" | rg -q 'KAction`17'; then
    echo "FAIL  emitted assembly is missing KAction\`17" >&2
    exit 1
fi

REFPACK="$(ls -d /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 2>/dev/null | sort -V | tail -1)"
RUNTIMEPACK="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/* 2>/dev/null | sort -V | tail -1)"
REFS="$(ls "$REFPACK"/*.dll "$RUNTIMEPACK"/*.dll | tr '\n' ';')$OUT/il/Wide.dll"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$OUT/wide.meta" --refs "$REFS" WideKt >/dev/null
if ! rg -q 'tlfun accept Int final cb:func:\[Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int,Int\]' "$OUT/wide.meta"; then
    echo "FAIL  facadegen did not restore KFunc\`18 as a Kotlin function type" >&2
    cat "$OUT/wide.meta" >&2
    exit 1
fi

dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$OUT/native" --native-cir --ref "$OUT/il/Wide.dll" "$OUT/bir/Wide.bir.json" >/dev/null
if ! rg -q '"parameterTypes"' "$OUT/native/Wide.cir.json" || ! rg -q '"func:int:int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int,int"' "$OUT/native/Wide.cir.json"; then
    echo "FAIL  bir2cir did not index KFunc\`18 as func:..." >&2
    exit 1
fi

echo "PASS  ilemit wide synthetic delegates"
