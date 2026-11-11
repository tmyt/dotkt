"""Generate source-owned BIR records, without serialized children or a field bag."""
import json


def outputs(root, definitions):
    selected = [shape for shape in definitions.values() if isinstance(shape, dict)
                and shape.get("x-dotkt-model", {}).get("domain") in {"birCore", "birCoreValues"}]
    lines = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
             "package kotc.bir", ""]

    def shape_type(field):
        if "$ref" in field:
            ref = field["$ref"].removeprefix("#/$defs/")
            return "TypeNode" if ref == "type" else definitions[ref]["x-dotkt-model"]["name"]
        kind = field["type"]
        if isinstance(kind, list):
            if len(kind) != 2 or "null" not in kind:
                raise ValueError("Source field needs one logical type and optional null")
            kind = next(item for item in kind if item != "null")
        return {"string": "String", "integer": "Int", "boolean": "Boolean"}[kind]

    def nullable(field):
        return isinstance(field.get("type"), list) and "null" in field["type"]

    def write(field, value):
        if "$ref" in field:
            return (f"TypeNodeJson.write({value}, output)" if field["$ref"] == "#/$defs/type"
                    else f"{value}.writeJson(output)")
        if shape_type(field) == "String":
            return f"TypeNodeJson.writeString({value}, output)"
        return f"output.append({value})"

    tags = set()
    for shape in selected:
        info = shape["x-dotkt-model"]
        is_node = info["domain"] == "birCore"
        fields = [(key, field) for key, field in shape["properties"].items() if key != "k"]
        required = set(shape["required"])
        if shape.get("additionalProperties") is not False:
            raise ValueError("BIR source records must explicitly account for every field")
        if is_node:
            tag = shape["properties"]["k"]["const"]
            if tag in tags or info["role"] not in {"expression", "statement"}:
                raise ValueError("BIR source tags and roles must be explicit and unique")
            tags.add(tag)
        parameters = []
        for key, field in fields:
            optional = key not in required
            if optional and nullable(field):
                raise ValueError("Nullable optional fields require explicit presence, not a null default")
            value_type = shape_type(field) + ("?" if optional or nullable(field) else "")
            parameters.append(f"    val {key}: {value_type}" + (" = null" if optional else ""))
        lines += [f'data class {info["name"]}(', ",\n".join(parameters), ")" +
                  (" : Bir" + info["role"].capitalize() if is_node else "") + " {",
                  ("    override " if is_node else "    ") + "fun writeJson(output: StringBuilder) {"]
        opening = '{"k":' + json.dumps(tag) if is_node else "{"
        lines += ["        output.append(" + json.dumps(opening) + ")"]
        for index, (key, field) in enumerate(fields):
            optional = key not in required
            indent = "            " if optional else "        "
            if optional:
                lines += [f"        {key}?.let {{ value ->"]
            prefix = ("," if is_node or index else "") + json.dumps(key) + ":"
            lines += [indent + "output.append(" + json.dumps(prefix) + ")"]
            value = "value" if optional else key
            if nullable(field):
                lines += [indent + f'if ({value} == null) output.append("null")',
                          indent + "else " + write(field, value)]
            else:
                lines += [indent + write(field, value)]
            if optional:
                lines += ["        }"]
        lines += ["        output.append('}')", "    }", "}", ""]
    return {root / "toolchain/kotc/src/main/kotlin/kotc/bir/BirCore.Generated.kt": "\n".join(lines)}
