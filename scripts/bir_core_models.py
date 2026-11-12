"""Generate source-owned BIR records, without serialized children or a field bag."""
import json


def outputs(root, definitions):
    selected = [shape for shape in definitions.values() if isinstance(shape, dict)
                and shape.get("x-dotkt-model", {}).get("domain") in {"birCore", "birCoreValues"}]
    lines = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
             "package kotc.bir", ""]

    def nullable(field):
        return (isinstance(field.get("type"), list) and "null" in field["type"]
                or {"type": "null"} in field.get("oneOf", []))

    def non_null(field):
        if isinstance(field.get("type"), list):
            kinds = [kind for kind in field["type"] if kind != "null"]
            if len(kinds) != 1:
                raise ValueError("Source field needs exactly one non-null logical type")
            return {**field, "type": kinds[0]}
        if nullable(field):
            choices = [choice for choice in field["oneOf"] if choice != {"type": "null"}]
            if len(choices) != 1:
                raise ValueError("Source field needs exactly one non-null logical shape")
            return choices[0]
        return field

    def shape_type(field):
        field = non_null(field)
        if field.get("x-dotkt-kotlin-json-type"):
            return "TypeNode"
        if "$ref" in field:
            ref = field["$ref"].removeprefix("#/$defs/")
            return "TypeNode" if ref == "type" else definitions[ref]["x-dotkt-model"]["name"]
        if field.get("type") == "array":
            item = field["items"]
            return "List<" + shape_type(item) + ("?" if nullable(item) else "") + ">"
        kind = field.get("type", "string" if "enum" in field else None)
        if kind is None and "const" in field:
            kind = {bool: "boolean", int: "integer", str: "string"}[type(field["const"])]
        return {"string": "String", "integer": "Int", "boolean": "Boolean"}[kind]

    def write(field, value, indent, depth=0):
        if nullable(field):
            return [indent + f'if ({value} == null) output.append("null")', indent + "else {",
                    *write(non_null(field), value, indent + "    ", depth), indent + "}"]
        if field.get("x-dotkt-kotlin-json-type"):
            return [indent + f"TypeNodeJson.writeString({value}.toJson(), output)"]
        if "$ref" in field:
            return [indent + f"{value}.writeJson(output)"]
        if field.get("type") == "array":
            index, item = f"index{depth}", f"item{depth}"
            return [indent + "output.append('[')",
                    indent + f"{value}.forEachIndexed {{ {index}, {item} ->",
                    indent + f"    if ({index} > 0) output.append(',')",
                    *write(field["items"], item, indent + "    ", depth + 1),
                    indent + "}", indent + "output.append(']')"]
        if shape_type(field) == "String":
            return [indent + f"TypeNodeJson.writeString({value}, output)"]
        return [indent + f"output.append({value})"]

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
        ordered = [pair for pair in fields if pair[0] in required] + [
            pair for pair in fields if pair[0] not in required]
        for key, field in ordered:
            if key in required and "const" in field:
                continue
            optional = key not in required
            name = field.get("x-dotkt-kotlin-name", key)
            value_type = shape_type(field)
            if optional and nullable(field):
                value_type = f"BirField<{value_type}>"
                default = " = BirField.Absent"
            else:
                value_type += "?" if optional or nullable(field) else ""
                default = " = null" if optional else ""
            parameters.append(f"    val {name}: {value_type}{default}")
        bases = ["Bir" + info["role"].capitalize() + "()"] if is_node else info.get("interfaces", ["BirValue"])
        if parameters:
            lines += [f'data class {info["name"]}(', ",\n".join(parameters),
                      ") : " + ", ".join(bases) + " {"]
        else:
            lines += [f'data object {info["name"]} : ' + ", ".join(bases) + " {"]
        lines += ["    override fun writeJson(output: StringBuilder) {"]
        opening = '{"k":' + json.dumps(tag) if is_node else "{"
        lines += ["        output.append(" + json.dumps(opening) + ")"]
        if not is_node:
            lines += ["        var hasField = false"]
        for index, (key, field) in enumerate(fields):
            optional = key not in required
            name = field.get("x-dotkt-kotlin-name", key)
            prefix = ("," if is_node or index else "") + json.dumps(key) + ":"
            append_key = ("output.append(" + json.dumps(prefix) + ")" if is_node else
                          "if (hasField) output.append(','); hasField = true; output.append(" +
                          json.dumps(json.dumps(key) + ":") + ")")
            if key in required and "const" in field:
                lines += ["        " + append_key,
                          "        output.append(" + json.dumps(json.dumps(field["const"])) + ")"]
            elif optional and nullable(field):
                lines += [f"        when (val present = {name}) {{",
                          "            BirField.Absent -> {}",
                          "            BirField.Null -> {",
                          "                " + append_key,
                          '                output.append("null")', "            }",
                          "            is BirField.Value -> {", "                " + append_key,
                          *write(non_null(field), "present.value", "                "), "            }", "        }"]
            elif optional:
                lines += [f"        {name}?.let {{ value ->", "            " + append_key,
                          *write(field, "value", "            "), "        }"]
            else:
                lines += ["        " + append_key, *write(field, name, "        ")]
        lines += ["        output.append('}')", "    }", "}", ""]
    return {root / "toolchain/kotc/src/main/kotlin/kotc/bir/BirCore.Generated.kt": "\n".join(lines)}
