"""Generate the tagged, width-preserving scalar cases of the semantic BIR literal."""
import json


def outputs(root, definitions):
    shapes = [shape for shape in definitions.values() if isinstance(shape, dict)
              and shape.get("x-dotkt-model", {}).get("domain") == "birFloatingLiteral"]
    cs = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
          "#nullable enable", "using System;", "using System.Text.Json.Nodes;", "namespace DotKt.Bir;", ""]
    kt = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.", "package kotc.bir", ""]
    for shape in shapes:
        if set(shape["properties"]) != {"encoding", "bits"} or set(shape["required"]) != {"encoding", "bits"}:
            raise ValueError("Floating literal models must account for every schema field")
        name = shape["x-dotkt-model"]["name"]
        tag = json.dumps(shape["properties"]["encoding"]["const"])
        width = shape["properties"]["bits"]["x-dotkt-csharp-type"]
        if width not in {"int", "long"}:
            raise ValueError("Unsupported logical floating literal width")
        cs.extend([f"public sealed record {name}({width} Bits) : BirLiteral", "{",
                   f"    public override JsonNode ToDom() => new JsonObject {{ [\"encoding\"] = {tag}, [\"bits\"] = Bits }};", "}", ""])
        prefix = json.dumps('{"encoding":' + tag + ',"bits":')
        kt.extend([f"data class {name}(val bits: {'Int' if width == 'int' else 'Long'}) : BirLiteral() {{",
                   "    override fun writeJson(output: StringBuilder) {",
                   f"        output.append({prefix}).append(bits).append('}}')", "    }", "}", ""])
    cs.extend(["public abstract partial record BirLiteral", "{",
               "    private static BirLiteral ReadFloating(JsonObject value)", "    {",
               "        if (value.Count != 2 || value[\"bits\"] is not JsonValue bits) throw new FormatException();",
               "        return value[\"encoding\"]?.GetValue<string>() switch", "        {"])
    for shape in shapes:
        name = shape["x-dotkt-model"]["name"]
        tag = json.dumps(shape["properties"]["encoding"]["const"])
        width = shape["properties"]["bits"]["x-dotkt-csharp-type"]
        cs.append(f"            {tag} => new {name}(bits.GetValue<{width}>()),")
    cs.extend(["            _ => throw new FormatException(),", "        };", "    }", "}", ""])
    return {root / "toolchain/bir-common/BirFloatingLiteral.Generated.cs": "\n".join(cs),
            root / "toolchain/kotc/src/main/kotlin/kotc/bir/FloatingLiterals.Generated.kt": "\n".join(kt)}
