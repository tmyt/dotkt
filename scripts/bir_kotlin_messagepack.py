"""Source BIR MessagePack writer generated from the Kotlin producer profile."""
import json
from bir_lowering_models import base, is_nullable, refkey


def outputs(root, definitions):
    shapes = {key: shape for key, shape in definitions.items() if isinstance(shape, dict)
              and shape.get("x-dotkt-model", {}).get("domain") in {"birCore", "birCoreValues"}}
    lines = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
             "package kotc.bir", "", "import org.msgpack.core.MessagePacker", "",
             "internal object BirMessagePack {", "    private const val maxDepth = 1024",
             "    private fun checkDepth(depth: Int) { require(depth < maxDepth) }", "",
             "    fun write(value: BirValue, packer: MessagePacker, depth: Int = 0) {",
             "        checkDepth(depth)", "        when (value) {",
             "            is TypeNode -> writeType(value, packer, depth)",
             "            is BirLiteral -> writeLiteral(value, packer, depth)",
             "            is SourceTypeAlias -> SourceMetadataMessagePack.write(value, packer)",
             "            is TypeParameterDeclaration -> writeTypeParameterDeclaration(value, packer, depth)"]
    for shape in shapes.values():
        name = shape["x-dotkt-model"]["name"]
        lines += [f"            is {name} -> write{name}(value, packer, depth)"]
    lines += ["        }", "    }", ""]

    def write(field, value, depth="depth + 1", indent="        ", nesting=0):
        if is_nullable(field):
            return [indent + f"if ({value} == null) packer.packNil()", indent + "else {",
                    *write(base(field), value, depth, indent + "    ", nesting), indent + "}"]
        if field.get("x-dotkt-kotlin-json-type"):
            return [indent + f"MessagePackText.write(packer, {value}.toJson())"]
        key = refkey(field)
        if key:
            if key == "birSourceTypeAlias": target = f"SourceMetadataMessagePack.write({value}, packer)"
            elif key == "type": target = f"writeType({value}, packer, {depth})"
            elif key == "literalValue": target = f"writeLiteral({value}, packer, {depth})"
            elif key == "sourceClassifier": target = f"writeTypeSourceClassifier({value}, packer, {depth})"
            elif key == "typeParam": target = f"writeTypeParameterDeclaration({value}, packer, {depth})"
            elif key in shapes: target = f"write{shapes[key]['x-dotkt-model']['name']}({value}, packer, {depth})"
            else: target = f"write({value}, packer, {depth})"
            return [indent + target]
        if field.get("type") == "array":
            item = f"item{nesting}"
            return [indent + f"checkDepth({depth})", indent + f"packer.packArrayHeader({value}.size)",
                    indent + f"for ({item} in {value}) {{",
                    *write(field["items"], item, f"{depth} + 1", indent + "    ", nesting + 1), indent + "}"]
        kind = field.get("type", "string" if "enum" in field else None)
        if "const" in field: kind = {str: "string", int: "integer", bool: "boolean"}[type(field["const"])]
        if kind == "string": return [indent + f"MessagePackText.write(packer, {value})"]
        method = {"integer": "packInt", "boolean": "packBoolean"}[kind]
        return [indent + f"packer.{method}({value})"]

    def record(shape, name, kotlin_type, tag=None):
        required = set(shape.get("required", []))
        fields = [(key, field) for key, field in shape["properties"].items() if key != tag and field.get("x-dotkt-kotlin", True)]
        wire_required = [(key, field) for key, field in fields if key in required]
        optional = [(key, field) for key, field in fields if key not in required]
        size = str(len(wire_required) + (tag is not None))
        for key, field in optional:
            prop = "value." + field.get("x-dotkt-kotlin-name", key)
            missing = f"{prop} == BirField.Absent" if is_nullable(field) else f"{prop} == null"
            size += f" + (if ({missing}) 0 else 1)"
        lines.extend([f"    private fun write{name}(value: {kotlin_type}, packer: MessagePacker, depth: Int) {{",
                      "        checkDepth(depth)", f"        packer.packMapHeader({size})"])
        if tag:
            lines.extend([f"        packer.packString({json.dumps(tag)})", f"        packer.packString({json.dumps(shape['properties'][tag]['const'])})"])
        for key, field in fields:
            prop = "value." + field.get("x-dotkt-kotlin-name", key)
            key_line = f"packer.packString({json.dumps(key)})"
            if key in required and "const" in field:
                lines.extend(["        " + key_line, *write(field, json.dumps(field["const"]))])
            elif key not in required and is_nullable(field):
                lines.extend([f"        when (val present = {prop}) {{", "            BirField.Absent -> {}",
                              "            BirField.Null -> {", "                " + key_line, "                packer.packNil()", "            }",
                              "            is BirField.Value -> {", "                " + key_line,
                              *write(base(field), "present.value", indent="                "), "            }", "        }"])
            elif key not in required:
                lines.extend([f"        {prop}?.let {{ child ->", "            " + key_line,
                              *write(field, "child", indent="            "), "        }"])
            else:
                lines.extend(["        " + key_line, *write(field, prop)])
        lines.extend(["    }", ""])

    for shape in shapes.values():
        info = shape["x-dotkt-model"]
        record(shape, info["name"], info["name"], "k" if info["domain"] == "birCore" else None)
    record(definitions["typeParam"], "TypeParameterDeclaration", "TypeParameterDeclaration")
    type_shapes = [definitions[item["$ref"].removeprefix("#/$defs/")] for item in definitions["type"]["oneOf"]
                   if definitions[item["$ref"].removeprefix("#/$defs/")]["x-dotkt-model"].get("kotlin", True)]
    lines.extend(["    private fun writeType(value: TypeNode, packer: MessagePacker, depth: Int) {", "        when (value) {"])
    for shape in type_shapes:
        name = shape["x-dotkt-model"]["name"]
        lines.append(f"            is TypeNode.{name} -> writeType{name}(value, packer, depth)")
    lines.extend(["        }", "    }", ""])
    for shape in type_shapes:
        name = shape["x-dotkt-model"]["name"]
        record(shape, "Type" + name, "TypeNode." + name, "t")
    record(definitions["sourceClassifier"], "TypeSourceClassifier", "TypeNode.SourceClassifier")
    lines.extend(["    private fun writeLiteral(value: BirLiteral, packer: MessagePacker, depth: Int) {",
                  "        when (value) {", "            BirLiteral.NullLiteral -> packer.packNil()",
                  "            is BirLiteral.BooleanLiteral -> packer.packBoolean(value.value)",
                  "            is BirLiteral.IntegerLiteral -> packer.packLong(value.value)",
                  "            is BirLiteral.TextLiteral -> MessagePackText.write(packer, value.value)"])
    for shape in definitions.values():
        if not isinstance(shape, dict) or shape.get("x-dotkt-model", {}).get("domain") != "birFloatingLiteral": continue
        name = shape["x-dotkt-model"]["name"]
        tag = json.dumps(shape["properties"]["encoding"]["const"])
        method = "packInt" if shape["properties"]["bits"]["x-dotkt-csharp-type"] == "int" else "packLong"
        lines.extend([f"            is {name} -> {{", "                checkDepth(depth)", "                packer.packMapHeader(2)",
                      f'                packer.packString("encoding").packString({tag})',
                      f'                packer.packString("bits").{method}(value.bits)', "            }"])
    lines.extend(["        }", "    }", "}", ""])
    return {root / "toolchain/kotc/src/main/kotlin/kotc/bir/BirMessagePack.Generated.kt": "\n".join(lines)}
