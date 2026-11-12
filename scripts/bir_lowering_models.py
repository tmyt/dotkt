"""Owning C# lowering graph, derived from the maintained source/physical profiles.

An in-progress lowering graph is neither a kotc output nor a final CIR document:
fields can be absent while their owning pass establishes them. Presence remains
explicit; this model does not fabricate defaults or infer missing semantic facts.
"""
import copy
import json


def outputs(root, definitions):
    aliases = {
        "cirExpressionNode": "birExpressionNode", "cirStatementNode": "birStatementNode",
        "cirConditionalBranch": "birBranch", "birConditionalBranch": "birBranch",
        "birElseBranch": "birBranch", "cirCatchClause": "birCatch",
        "cirSourceSpan": "sourceSpan", "cirSpreadPart": "birSpreadPart",
        # The enclosing bir2cir passes still use source literals; physical scalar
        # allocation remains the final CIR projection's responsibility.
        "cirScalarValue": "literalValue",
        "tFqn": "type",
        "cirParameter": "birParameter", "cirTypeParameter": "typeParam",
        "cirMethod": "birFunctionDeclaration", "cirConstructor": "birConstructor",
        "cirField": "birFieldDeclaration", "cirProperty": "birPropertyDeclaration",
        "mods": "birModifiers", "pos": "birDeclarationPosition",
        "cirAttributeApplication": "birAttribute",
        "cirFile": "birFileDeclaration",
    }

    def normalize(value):
        if isinstance(value, list):
            return [normalize(item) for item in value]
        if not isinstance(value, dict):
            return value
        if "$ref" in value:
            key = value["$ref"].removeprefix("#/$defs/")
            if key in {"cirStatementList", "cirExpressionList"}:
                item = "birStatementNode" if key == "cirStatementList" else "birExpressionNode"
                return {"type": "array", "items": {"$ref": "#/$defs/" + item}}
            return {**value, "$ref": "#/$defs/" + aliases.get(key, key)}
        return {key: normalize(item) for key, item in value.items()}

    shapes = {key: normalize(copy.deepcopy(shape)) for key, shape in definitions.items()
              if isinstance(shape, dict) and shape.get("x-dotkt-model", {}).get("domain") in
              {"birCore", "birCoreValues"} and key not in {"birConditionalBranch", "birElseBranch"}}
    for shape in shapes.values():
        shape["properties"].update(shape["x-dotkt-model"].get("loweringProperties", {}))
    source_tags = {shape["properties"]["k"]["const"]: key for key, shape in shapes.items()
                   if shape["x-dotkt-model"]["domain"] == "birCore"}
    for key, shape in definitions.items():
        info = shape.get("x-dotkt-model", {}) if isinstance(shape, dict) else {}
        if info.get("domain") != "cirExecutable":
            continue
        physical = normalize(shape)
        physical["properties"].update(normalize(info.get("sourceProperties", {})))
        tag = physical["properties"]["k"]["const"]
        if tag in source_tags:
            target = shapes[source_tags[tag]]
            for field, value in physical["properties"].items():
                if field in target["properties"]:
                    def logical(item):
                        return {k: logical(v) for k, v in item.items() if not k.startswith("x-") and k not in
                                {"description", "$comment", "minimum", "maximum", "minLength", "enum"}} if isinstance(item, dict) else (
                                    [logical(v) for v in item] if isinstance(item, list) else item)
                    if logical(target["properties"][field]) != logical(value):
                        raise ValueError(f"Unaccounted lowering field conflict: {tag}.{field}")
                else:
                    target["properties"][field] = value
            target["x-dotkt-model"]["roles"] = sorted(set(
                [target["x-dotkt-model"]["role"], *info["roles"]]))
        else:
            physical["x-dotkt-model"] = {"name": "Bir" + info["name"].removeprefix("Cir"),
                                         "domain": "birCore", "roles": info["roles"]}
            shapes[key] = physical
    # Declaration and executable phase profiles share their corresponding source
    # record. Add only physical fields; source fields retain source semantics up
    # to the already-typed final CIR projection (notably annotation arguments).
    declarations = {"cirMethod": ["birFunctionDeclaration"], "cirConstructor": ["birConstructor"],
                    "cirField": ["birFieldDeclaration"], "cirProperty": ["birPropertyDeclaration"],
                    "cirParameter": ["birParameter"], "mods": ["birModifiers"],
                    "cirTypeDeclaration": ["birClassDefinition", "birInterfaceDefinition", "birEnumDefinition"]}
    for key, targets in declarations.items():
        for target in targets:
            fields = shapes[target]["properties"]
            for name, value in normalize(definitions[key])["properties"].items():
                if name not in fields:
                    fields[name] = value
    for key, shape in definitions.items():
        info = shape.get("x-dotkt-model", {}) if isinstance(shape, dict) else {}
        if info.get("domain") == "cirDeclarations" and key not in aliases and key not in declarations and "properties" in shape:
            shapes[key] = normalize(copy.deepcopy(shape))
            shapes[key]["x-dotkt-model"] = {"name": "Bir" + info["name"].removeprefix("Cir"), "domain": "birCoreValues"}
    shapes["birBranch"] = normalize(copy.deepcopy(definitions["cirConditionalBranch"]))
    shapes["birBranch"]["x-dotkt-model"] = {"name": "BirBranch", "domain": "birCoreValues"}
    shapes["cirPhysicalEnumValue"] = normalize(copy.deepcopy(definitions["cirPhysicalEnumValue"]))
    shapes["cirPhysicalEnumValue"]["x-dotkt-model"]["name"] = "BirPhysicalEnumValue"
    for key, name in [("typeParam", "BirTypeParameter"), ("memberRef", "BirMemberReference")]:
        shapes[key] = normalize(copy.deepcopy(definitions[key]))
        shapes[key]["x-dotkt-model"] = {"name": name, "domain": "birCoreValues"}

    # A frozen carrier argument is opaque, not an executable child. The phase
    # vocabulary explicitly admits it only in metadata argument positions.
    shapes["birOpaqueBytes"] = {"properties": {"bytes": {"type": "string", "x-dotkt-csharp-type": "byte[]"}},
        "x-dotkt-model": {"name": "BirOpaqueBytes", "domain": "birCoreValues"}}
    for owner, field in [("birAttribute", "args"), ("birAttributeNamedArgument", "value")]:
        value = {"$ref": "#/$defs/loweringAttributeValue"}
        shapes[owner]["properties"][field] = {"type": "array", "items": value} if field == "args" else value

    def base(field):
        if isinstance(field.get("type"), list):
            return {**field, "type": next(kind for kind in field["type"] if kind != "null")}
        choices = field.get("oneOf", [])
        if {"type": "null"} in choices:
            return next(item for item in choices if item != {"type": "null"})
        return field

    def is_nullable(field):
        return (isinstance(field.get("type"), list) and "null" in field["type"]
                or {"type": "null"} in field.get("oneOf", []))

    def refkey(field):
        return base(field).get("$ref", "").removeprefix("#/$defs/")

    def cs(field):
        field = base(field)
        if field.get("x-dotkt-csharp-type"):
            return field["x-dotkt-csharp-type"]
        if field.get("x-dotkt-kotlin-json-type"):
            return "TypeNode"
        key = refkey(field)
        if key:
            special = {"type": "TypeNode", "literalValue": "BirLiteral", "birSourceTypeAlias": "SourceTypeAlias",
                       "cirAttributeArgument": "AttributeArgument",
                       "birExpressionNode": "IBirExpression", "birStatementNode": "IBirStatement",
                       "birTypeDeclaration": "IBirTypeDeclaration", "loweringAttributeValue": "IBirAttributeValue"}
            return special[key] if key in special else shapes[key]["x-dotkt-model"]["name"]
        if field.get("type") == "array":
            return cs(field["items"]) + ("?" if is_nullable(field["items"]) else "") + "[]"
        kind = field.get("type", "string" if "enum" in field else None)
        if "const" in field:
            kind = {str: "string", int: "integer", bool: "boolean"}[type(field["const"])]
        return {"string": "string", "integer": "int", "boolean": "bool"}[kind]

    def member(key):
        return key[:1].upper() + key[1:]

    def read(field):
        field = base(field)
        if cs(field) == "byte[]": return "Convert.FromBase64String(ReadString(ref reader))"
        if field.get("x-dotkt-kotlin-json-type"):
            return "ReadFrozenType(ref reader)"
        key = refkey(field)
        if key:
            if key == "type": return "TypeNode.ReadJson(ref reader)"
            if key == "literalValue": return "BirLiteral.ReadJson(ref reader)"
            if key == "birSourceTypeAlias": return "SourceMetadataJson.Read(ref reader)"
            if key == "cirAttributeArgument": return "JsonSerializer.Deserialize<AttributeArgument>(ref reader, AttributeJson.Options)!"
            return f"Read{cs(field).removeprefix('I')}(ref reader)"
        if field.get("type") == "array":
            item = field["items"]
            expr = read(item)
            if is_nullable(item):
                expr = f"reader.TokenType == JsonTokenType.Null ? null : {expr}"
            return f"ReadArray(ref reader, (ref Utf8JsonReader reader) => {expr})"
        expr = {"string": "ReadString(ref reader)", "int": "reader.GetInt32()",
                "bool": "reader.GetBoolean()"}[cs(field)]
        return expr

    def write(field, value, indent="            ", depth=0):
        field = base(field)
        if cs(field) == "byte[]": return [indent + f"writer.WriteBase64StringValue({value});"]
        if field.get("x-dotkt-kotlin-json-type"):
            return [indent + f"writer.WriteStringValue(TypeNode.ToJson({value}));"]
        key = refkey(field)
        if key:
            if key == "type": expr = f"TypeNode.WriteJson(writer, {value});"
            elif key == "literalValue": expr = f"BirLiteral.WriteJson(writer, {value});"
            elif key == "birSourceTypeAlias": expr = f"SourceMetadataJson.Write(writer, {value});"
            elif key == "cirAttributeArgument": expr = f"JsonSerializer.Serialize(writer, {value}, AttributeJson.Options);"
            else: expr = f"((BirGraphValue){value}).WriteJson(writer);"
            return [indent + expr]
        if field.get("type") == "array":
            item = f"item{depth}"
            lines = [indent + "writer.WriteStartArray();", indent + f"foreach (var {item} in {value})", indent + "{"]
            if is_nullable(field["items"]):
                lines += [indent + f"    if ({item} is null) writer.WriteNullValue();", indent + "    else", indent + "    {"]
                lines += write(field["items"], item, indent + "        ", depth + 1) + [indent + "    }"]
            else: lines += write(field["items"], item, indent + "    ", depth + 1)
            return [*lines, indent + "}", indent + "writer.WriteEndArray();"]
        method = {"string": "WriteStringValue", "int": "WriteNumberValue", "bool": "WriteBooleanValue"}[cs(field)]
        return [indent + f"writer.{method}({value});"]

    def dom(field, value, depth=0):
        field = base(field)
        if cs(field) == "byte[]": return f"JsonValue.Create(Convert.ToBase64String({value}))"
        if field.get("x-dotkt-kotlin-json-type"):
            return f"JsonValue.Create(TypeNode.ToJson({value}))"
        key = refkey(field)
        if key:
            if key == "type": return f"TypeNode.Write({value})"
            if key == "literalValue": return f"LiteralToDom({value})"
            if key == "cirAttributeArgument": return f"JsonValue.Create<AttributeArgument>({value})"
            if key == "birSourceTypeAlias": return "throw new InvalidOperationException(\"Extract aliases before the temporary DOM boundary\")"
            return f"((BirGraphValue){value}).ToDom()"
        if field.get("type") == "array":
            item = f"item{depth}"
            expr = dom(field["items"], item, depth + 1)
            if is_nullable(field["items"]): expr = f"{item} is null ? null : {expr}"
            return f"new JsonArray({value}.Select({item} => (JsonNode?)({expr})).ToArray())"
        return f"JsonValue.Create({value})"

    def is_child(field):
        field = base(field)
        if field.get("type") == "array": return is_child(field["items"])
        return bool(refkey(field)) and refkey(field) not in {"type", "literalValue", "birSourceTypeAlias", "cirAttributeArgument"}

    def rewrite(field, value, depth=0):
        field = base(field)
        if field.get("type") == "array":
            item = f"item{depth}"
            expr = rewrite(field["items"], item, depth + 1)
            if is_nullable(field["items"]): expr = f"{item} is null ? null : {expr}"
            return f"{value}.Select({item} => {expr}).ToArray()"
        return f"({cs(field)})rewrite((BirGraphValue){value})"

    header = ["// <auto-generated />", "// Source: docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
              "#nullable enable", "using System.Text.Json;", "using System.Text.Json.Nodes;", "using DotKt.Cir;",
              "using static DotKt.Bir.Lowering.BirGraphJson;", "namespace DotKt.Bir.Lowering;", ""]
    model, codec = header.copy(), header.copy()
    codec += ["internal static partial class BirGraphJson", "{"]
    nodes = []
    for key, shape in shapes.items():
        info = shape["x-dotkt-model"]
        name = info["name"]
        fields = [(key, value) for key, value in shape["properties"].items() if key != "k" or info["domain"] != "birCore"]
        if len(fields) + (info["domain"] == "birCore") > 64:
            raise ValueError(f"Expand duplicate-field tracking before extending {name}")
        node = info["domain"] == "birCore"
        roles = info.get("roles", [info["role"]] if "role" in info else [])
        bases = ["BirGraphNode" if node else "BirGraphValue"]
        bases += ["IBir" + role.capitalize() for role in roles]
        if "expression" in roles: bases += ["IBirAttributeValue"]
        if key == "birOpaqueBytes": bases += ["IBirAttributeValue"]
        if key in {"birClassDefinition", "birInterfaceDefinition", "birEnumDefinition"}: bases += ["IBirTypeDeclaration"]
        model += [f"internal sealed record {name} : " + ", ".join(bases), "{"]
        if node:
            tag = shape["properties"]["k"]["const"]
            model += [f'    public override string Tag => {json.dumps(tag)};']
            nodes += [(tag, name, roles)]
        for field, value in fields:
            model += [f"    public OptionalField<{cs(value)}> {member(field)} {{ get; init; }}"]
        model += ["    public override BirGraphValue RewriteChildren(Func<BirGraphValue, BirGraphValue> rewrite) => this with", "    {"]
        for field, value in fields:
            if is_child(value):
                prop = member(field)
                model += [f"        {prop} = {prop}.HasValue ? OptionalField<{cs(value)}>.Of({rewrite(value, prop + '.Value')}) : {prop},"]
        model += ["    };", "    public override void WriteJson(Utf8JsonWriter writer)", "    {", "        writer.WriteStartObject();"]
        if node: model += [f'        writer.WriteString("k", {json.dumps(tag)});']
        for field, value in fields:
            prop = member(field)
            body = " ".join(line.strip() for line in write(value, "value"))
            model += [f"        WriteOptional(writer, {json.dumps(field)}, {prop}, (writer, value) => {{ {body} }});"]
        model += ["        writer.WriteEndObject();", "    }", "    public override JsonObject ToDom()", "    {", "        var result = new JsonObject();"]
        if node: model += [f'        result["k"] = {json.dumps(tag)};']
        for field, value in fields:
            prop = member(field)
            if refkey(value) == "birSourceTypeAlias" or refkey(value.get("items", {})) == "birSourceTypeAlias":
                model += [f"        if ({prop}.IsPresent) throw new InvalidOperationException(\"Extract aliases before the temporary DOM boundary\");"]
            else:
                model += [f"        if ({prop}.IsPresent) result[{json.dumps(field)}] = {prop}.IsNull ? null : {dom(value, prop + '.Value')};"]
        model += ["        return result;", "    }", "}", ""]
        codec += [f"    internal static {name} Read{name}(ref Utf8JsonReader reader)", "    {",
                  "        Require(reader, JsonTokenType.StartObject);", "        ulong seen = 0;"]
        codec += [f"        OptionalField<{cs(value)}> field{index} = default;" for index, (_, value) in enumerate(fields)]
        codec += [
                  "        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)", "        {",
                  "            Require(reader, JsonTokenType.PropertyName);", "            var key = reader.GetString();",
                  "            if (!reader.Read()) throw new FormatException();", "            switch (key)", "            {"]
        if node:
            codec += [f'                case "k": Seen(ref seen, 1UL << {len(fields)}); if (ReadString(ref reader) != {json.dumps(tag)}) throw new FormatException(); break;']
        for index, (field, value) in enumerate(fields):
            prop, typ = member(field), cs(value)
            expr = read(value)
            codec += [f"                case {json.dumps(field)}:", f"                    Seen(ref seen, 1UL << {index});",
                      f"                    field{index} = reader.TokenType == JsonTokenType.Null ? OptionalField<{typ}>.Null : OptionalField<{typ}>.Of({expr});", "                    break;"]
        codec += ["                default: throw new FormatException($\"Unknown lowering field {key}\");", "            }", "        }",
                  "        Require(reader, JsonTokenType.EndObject);", f"        return new {name}", "        {"]
        codec += [f"            {member(field)} = field{index}," for index, (field, _) in enumerate(fields)]
        codec += ["        };", "    }", ""]
    codec += ["    internal static BirGraphNode ReadNode(ref Utf8JsonReader reader) => Discriminator(reader, \"k\") switch", "    {"]
    codec += [f'        {json.dumps(tag)} => Read{name}(ref reader),' for tag, name, _ in nodes]
    codec += ['        var kind => throw new FormatException($"malformed current node: unknown k={kind}"),', "    };",
              "    internal static IBirExpression ReadBirExpression(ref Utf8JsonReader reader) => ReadNode(ref reader) as IBirExpression ?? throw new FormatException();",
              "    internal static IBirStatement ReadBirStatement(ref Utf8JsonReader reader) => ReadNode(ref reader) as IBirStatement ?? throw new FormatException();",
              "    internal static IBirAttributeValue ReadBirAttributeValue(ref Utf8JsonReader reader) => HasProperty(reader, \"bytes\") ? ReadBirOpaqueBytes(ref reader) : ReadBirExpression(ref reader);",
              "    internal static IBirTypeDeclaration ReadBirTypeDeclaration(ref Utf8JsonReader reader) => Discriminator(reader, \"kind\") switch", "    {",
              '        "class" => ReadBirClassDefinition(ref reader),', '        "interface" => ReadBirInterfaceDefinition(ref reader),',
              '        "enum" => ReadBirEnumDefinition(ref reader),', "        _ => throw new FormatException(),", "    };", "}", ""]
    return {root / "toolchain/bir2cir/BirGraph.Model.Generated.cs": "\n".join(model),
            root / "toolchain/bir2cir/BirGraph.Json.Generated.cs": "\n".join(codec)}
