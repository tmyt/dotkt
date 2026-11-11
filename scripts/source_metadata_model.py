"""Generate source declaration models from the existing alias schema, not CLR types."""
import json


def outputs(root, schema):
    definitions = schema["$defs"]
    named = [schema, *(value for value in definitions.values() if "x-dotkt-model" in value)]

    def type_name(field):
        if "$ref" in field:
            target = definitions[field["$ref"].removeprefix("#/$defs/")]
            return target["x-dotkt-model"]["name"] if "x-dotkt-model" in target else type_name(target)
        if "x-dotkt-kotlin-type" in field:
            return field["x-dotkt-kotlin-type"]
        if field.get("type") == "array":
            return f'List<{type_name(field["items"])}>'
        return {"string": "String", "boolean": "Boolean", "integer": "Int"}[
            field.get("type", "string" if "enum" in field else None)]

    def variants(definition):
        info = definition["x-dotkt-model"]
        discriminator = info["discriminator"]
        for branch in definition.get("oneOf", [{}]):
            properties = {**definition["properties"], **branch.get("properties", {})}
            shape = properties[discriminator]
            for tag in shape.get("enum", [shape.get("const")]):
                if not isinstance(tag, str):
                    raise ValueError("A source union needs a closed string discriminator")
                fields = dict(properties)
                fields[discriminator] = {"const": tag}
                required = set(definition.get("required", [])) | set(branch.get("required", []))
                for condition in definition.get("allOf", []):
                    selected = condition.get("if", {}).get("properties", {}).get(discriminator)
                    if selected is None:
                        if "properties" in condition.get("if", {}):
                            raise ValueError("Unsupported source model discriminator condition")
                        continue  # Non-discriminator semantic relations remain the owning producer's facts.
                    if condition["if"] != {"properties": {discriminator: selected}} or set(selected) != {"const"}:
                        raise ValueError("Source model discriminator conditions require a single constant")
                    rule = condition.get("then" if selected.get("const") == tag else "else", {})
                    if set(rule) - {"required", "not"} or set(rule.get("not", {})) - {"required"}:
                        raise ValueError("Unsupported source model discriminator rule")
                    required.update(rule.get("required", []))
                    for forbidden in rule.get("not", {}).get("required", []):
                        fields.pop(forbidden)
                yield tag[0].upper() + tag[1:] + info["variantSuffix"], {
                    "properties": fields, "required": required,
                }

    def parameters(definition):
        properties = definition["properties"]
        return [(key, shape, key not in definition.get("required", []))
                for key, shape in properties.items() if "const" not in shape]

    header = "// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py."
    model = [header, "package kotc.bir", ""]
    writer = [header, "package kotc.bir", "", "internal object SourceMetadataJson {",
              "    fun encode(value: SourceTypeAlias): String = buildString { writeSourceTypeAlias(value, this) }", ""]

    def declaration(name, definition, parent=None, indent=""):
        fields = [f'val {key}: {type_name(shape)}' + ("? = null" if optional else "")
                  for key, shape, optional in parameters(definition)]
        suffix = f" : {parent}" if parent else ""
        if fields:
            model.append(f'{indent}data class {name}(\n' + ",\n".join(f"{indent}    {field}" for field in fields)
                         + f"\n{indent}){suffix}")
        else:
            model.append(f"{indent}data object {name}{suffix}")

    def write_value(shape, expression, indent):
        if "$ref" in shape:
            target = definitions[shape["$ref"].removeprefix("#/$defs/")]
            if "x-dotkt-model" in target:
                return [f'{indent}write{target["x-dotkt-model"]["name"]}({expression}, sb)']
            return write_value(target, expression, indent)
        if shape.get("type") == "array":
            return [f"{indent}sb.append('[')", f"{indent}{expression}.forEachIndexed {{ index, item ->",
                    f"{indent}    if (index > 0) sb.append(',')",
                    *write_value(shape["items"], "item", indent + "    "), f"{indent}}}", f"{indent}sb.append(']')"]
        if "const" in shape:
            return [f"{indent}sb.append({json.dumps(json.dumps(shape['const']))})"]
        if type_name(shape) == "String":
            return [f"{indent}TypeNodeJson.writeString({expression}, sb)"]
        return [f"{indent}sb.append({expression})"]

    def write_object(definition, indent):
        lines = [f"{indent}sb.append('{{')"]
        # Both source record families start with at least one required field.
        ordered = sorted(definition["properties"].items(), key=lambda entry: entry[0] not in definition["required"])
        if not ordered or ordered[0][0] not in definition["required"]:
            raise ValueError("Source records need a required leading field")
        for index, (key, shape) in enumerate(ordered):
            optional = key not in definition["required"]
            nest = indent + "    " if optional else indent
            if optional:
                lines.append(f"{indent}value.{key}?.let {{ child ->")
            prefix = ("," if index else "") + json.dumps(key) + ":"
            lines.append(f"{nest}sb.append({json.dumps(prefix)})")
            lines.extend(write_value(shape, "child" if optional else f"value.{key}", nest))
            if optional:
                lines.append(f"{indent}}}")
        lines.append(f"{indent}sb.append('}}')")
        return lines

    for definition in named:
        info = definition["x-dotkt-model"]
        name = info["name"]
        if "discriminator" in info:
            model.append(f"sealed interface {name} {{")
            writer += [f"    private fun write{name}(value: {name}, sb: StringBuilder) {{", "        when (value) {"]
            for variant, shape in variants(definition):
                declaration(variant, shape, name, "    ")
                pattern = "is " if parameters(shape) else ""
                writer += [f"            {pattern}{name}.{variant} -> {{", *write_object(shape, "                "), "            }"]
            model += ["}", ""]
            writer += ["        }", "    }", ""]
        else:
            declaration(name, definition)
            model.append("")
            writer += [f"    private fun write{name}(value: {name}, sb: StringBuilder) {{",
                       *write_object(definition, "        "), "    }", ""]
    writer += ["}", ""]
    return {
        root / "toolchain/kotc/src/main/kotlin/kotc/bir/SourceMetadata.kt": "\n".join(model),
        root / "toolchain/kotc/src/main/kotlin/kotc/bir/SourceMetadataJson.kt": "\n".join(writer),
    }
