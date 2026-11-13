"""Owning C# lowering graph, derived from the maintained source/physical profiles.

An in-progress lowering graph is neither a kotc output nor a final CIR document:
fields can be absent while their owning pass establishes them. Presence remains
explicit; this model does not fabricate defaults or infer missing semantic facts.
"""
import copy
import json


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


def csharp_type(field, shapes):
    field = base(field)
    if field.get("x-dotkt-csharp-type"):
        return field["x-dotkt-csharp-type"]
    if field.get("x-dotkt-kotlin-json-type"):
        return "TypeNode"
    key = refkey(field)
    if key:
        special = {"type": "TypeNode", "literalValue": "BirLiteral", "birSourceTypeAlias": "SourceTypeAlias",
                   "birExpressionNode": "IBirExpression", "birStatementNode": "IBirStatement",
                   "birTypeDeclaration": "IBirTypeDeclaration", "loweringAttributeValue": "IBirAttributeValue"}
        return special[key] if key in special else shapes[key]["x-dotkt-model"]["name"]
    if field.get("type") == "array":
        return csharp_type(field["items"], shapes) + ("?" if is_nullable(field["items"]) else "") + "[]"
    kind = field.get("type", "string" if "enum" in field else None)
    if "const" in field:
        kind = {str: "string", int: "integer", bool: "boolean"}[type(field["const"])]
    return {"string": "string", "integer": "int", "boolean": "bool"}[kind]


def declaration_cases(definitions):
    result = []
    for entry in definitions["birTypeDeclaration"]["oneOf"]:
        shape = definitions[refkey(entry)]
        info = shape["x-dotkt-model"]
        for kind in [shape["properties"]["kind"]["const"], *info.get("loweringKinds", [])]:
            result.append((kind, info["name"]))
    return result


PHYSICAL_ALIASES = {
        "cirExpressionNode": "birExpressionNode", "cirStatementNode": "birStatementNode",
        "cirExpression": "birExpressionNode",
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
        "cirEnumEntry": "birEnumEntryDeclaration",
        "cirEvent": "birClrEventBacking",
        "cirFile": "birFileDeclaration",
}

PHYSICAL_DECLARATIONS = {
    "cirMethod": ["birFunctionDeclaration"], "cirConstructor": ["birConstructor"],
    "cirField": ["birFieldDeclaration"], "cirProperty": ["birPropertyDeclaration"],
    "cirParameter": ["birParameter"], "mods": ["birModifiers"],
    "cirTypeDeclaration": ["birClassDefinition", "birInterfaceDefinition", "birEnumDefinition"],
    "cirEnumEntry": ["birEnumEntryDeclaration"], "cirEvent": ["birClrEventBacking"],
}


def compose_shapes(definitions):
    aliases = PHYSICAL_ALIASES

    def normalize(value):
        if isinstance(value, list):
            return [normalize(item) for item in value]
        if not isinstance(value, dict):
            return value
        if "$ref" in value:
            key = value["$ref"].removeprefix("#/$defs/")
            if definitions.get(key, {}).get("x-dotkt-csharp-codec"):
                return normalize({**definitions[key], **{k: v for k, v in value.items() if k != "$ref"}})
            if key in {"cirStatementList", "cirExpressionList"}:
                item = "birStatementNode" if key == "cirStatementList" else "birExpressionNode"
                return {"type": "array", "items": {"$ref": "#/$defs/" + item}}
            return {**value, "$ref": "#/$defs/" + aliases.get(key, key)}
        return {key: normalize(item) for key, item in value.items()}

    shapes = {key: normalize(copy.deepcopy(shape)) for key, shape in definitions.items()
              if isinstance(shape, dict) and shape.get("x-dotkt-model", {}).get("domain") in
              {"birCore", "birCoreValues", "birLoweringValues"} and key not in {"birConditionalBranch", "birElseBranch"}}
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
        physical["properties"].update(normalize(info.get("loweringProperties", {})))
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
    declarations = PHYSICAL_DECLARATIONS
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
            shapes[key]["properties"].update(normalize(info.get("loweringProperties", {})))
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
    for owner, field in [("birAttribute", "args"), ("birAttributeNamedArgument", "value")]:
        value = {"$ref": "#/$defs/loweringAttributeValue"}
        shapes[owner]["properties"][field] = {"type": "array", "items": value} if field == "args" else value

    def signature(field):
        field = base(field)
        if field.get("x-dotkt-kotlin-json-type"):
            return "frozen-type"
        if key := refkey(field):
            return "string" if "enum" in definitions.get(key, {}) else ("ref", key)
        if field.get("type") == "array":
            return ("array", signature(field["items"]))
        if "const" in field:
            return {str: "string", int: "integer", bool: "boolean"}[type(field["const"])]
        return field.get("type", "string" if "enum" in field else None)

    # Every physical field must have a declared owning lowering counterpart.
    # Lowering-only facts stay outside the Kotlin producer's source profile.
    omitted_at_input = {}
    for physical, source in aliases.items():
        if "properties" not in definitions.get(physical, {}) or source not in shapes:
            continue
        missing = definitions[physical]["properties"].keys() - shapes[source]["properties"].keys()
        if missing != omitted_at_input.get(physical, set()):
            raise ValueError(f"Unaccounted input-profile omission: {physical}: {sorted(missing)}")
    by_tag = {shape["properties"]["k"]["const"]: shape for shape in shapes.values()
              if shape["x-dotkt-model"].get("domain") == "birCore"}
    for shape in definitions.values():
        info = shape.get("x-dotkt-model", {}) if isinstance(shape, dict) else {}
        if info.get("domain") != "cirExecutable":
            continue
        tag = shape["properties"]["k"]["const"]
        if missing := set(info.get("sourceConsumed", [])) - by_tag[tag]["properties"].keys():
            raise ValueError(f"Untyped consumed phase fields: {tag}: {sorted(missing)}")
    for physical, targets in declarations.items():
        for target in targets:
            info = shapes[target]["x-dotkt-model"]
            explicit = set(info.get("loweringProperties", {})) | set(info.get("loweringSourceFields", []))
            for key, value in normalize(definitions[physical])["properties"].items():
                if signature(shapes[target]["properties"][key]) != signature(value) and key not in explicit:
                    raise ValueError(f"Unaccounted declaration phase type: {target}.{key}")

    return shapes


def outputs(root, definitions):
    shapes = compose_shapes(definitions)

    def cs(field):
        return csharp_type(field, shapes)

    def member(key):
        return key[:1].upper() + key[1:]

    def read(field):
        field = base(field)
        if codec := field.get("x-dotkt-csharp-codec"):
            return f"{codec}.Read(ref reader)"
        if cs(field) == "byte[]": return "Convert.FromBase64String(ReadString(ref reader))"
        if field.get("x-dotkt-kotlin-json-type"):
            return "ReadFrozenType(ref reader)"
        key = refkey(field)
        if key:
            if key == "type": return "TypeNode.ReadJson(ref reader)"
            if key == "literalValue": return "BirLiteral.ReadJson(ref reader)"
            if key == "birSourceTypeAlias": return "SourceMetadataJson.Read(ref reader)"
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
        if codec := field.get("x-dotkt-csharp-codec"):
            return [indent + f"{codec}.Write(writer, {value});"]
        if cs(field) == "byte[]": return [indent + f"writer.WriteBase64StringValue({value});"]
        if field.get("x-dotkt-kotlin-json-type"):
            return [indent + f"writer.WriteStringValue(TypeNode.ToJson({value}));"]
        key = refkey(field)
        if key:
            if key == "type": expr = f"TypeNode.WriteJson(writer, {value});"
            elif key == "literalValue": expr = f"BirLiteral.WriteJson(writer, {value});"
            elif key == "birSourceTypeAlias": expr = f"SourceMetadataJson.Write(writer, {value});"
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
        if codec := field.get("x-dotkt-csharp-codec"):
            return f"{codec}.ToDom({value})"
        if cs(field) == "byte[]": return f"JsonValue.Create(Convert.ToBase64String({value}))"
        if field.get("x-dotkt-kotlin-json-type"):
            return f"JsonValue.Create(TypeNode.ToJson({value}))"
        key = refkey(field)
        if key:
            if key == "type": return f"TypeNode.Write({value})"
            if key == "literalValue": return f"LiteralToDom({value})"
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
        return bool(refkey(field)) and refkey(field) not in {"type", "literalValue", "birSourceTypeAlias"}

    def rewrite(field, value, depth=0, callback="rewrite", prefix=""):
        field = base(field)
        if field.get("type") == "array":
            item = f"item{depth}"
            expr = rewrite(field["items"], item, depth + 1, callback, prefix)
            if is_nullable(field["items"]): expr = f"{item} is null ? null : {expr}"
            return f"{value}.Select({item} => {expr}).ToArray()"
        return f"({cs(field)}){callback}({prefix}(BirGraphValue){value})"

    def copy_graph(field, value, depth=0):
        field = base(field)
        if cs(field) == "byte[]": return f"{value}.ToArray()"
        if field.get("type") == "array":
            item = f"item{depth}"
            expr = copy_graph(field["items"], item, depth + 1)
            if is_nullable(field["items"]): expr = f"{item} is null ? null : {expr}"
            return f"{value}.Select({item} => {expr}).ToArray()"
        if cs(field) == "TypeNode": return f"CopyType({value})"
        if is_child(field): return f"({cs(field)})((BirGraphValue){value}).DeepClone()"
        return value

    def is_type_slot(field):
        field = base(field)
        if field.get("x-dotkt-kotlin-json-type") or field.get("x-dotkt-csharp-codec"): return False
        if field.get("type") == "array": return is_type_slot(field["items"])
        return refkey(field) == "type"

    def is_string_slot(field):
        field = base(field)
        if field.get("type") == "array": return is_string_slot(field["items"])
        return cs(field) == "string"

    def rewrite_type(field, value, depth=0, callback="rewrite", prefix=""):
        field = base(field)
        if field.get("type") == "array":
            item = f"item{depth}"
            expr = rewrite_type(field["items"], item, depth + 1, callback, prefix)
            if is_nullable(field["items"]): expr = f"{item} is null ? null : {expr}"
            return f"{value}.Select({item} => {expr}).ToArray()"
        return f"{callback}({prefix}{value})"

    def visit(field, value, depth=0, callback="visit", prefix="", cast="BirGraphValue", suffix=""):
        field = base(field)
        if field.get("type") == "array":
            item = f"item{depth}"
            body = visit(field["items"], item, depth + 1, callback, prefix, cast, suffix)
            if is_nullable(field["items"]): body = f"if ({item} is not null) {{ {body} }}"
            return f"foreach (var {item} in {value}) {{ {body} }}"
        return f"{callback}({prefix}({cast}){value}{suffix});"

    def equal(field, left, right, depth=0):
        field = base(field)
        if field.get("type") == "array":
            a, b = f"left{depth}", f"right{depth}"
            inner = equal(field["items"], a, b, depth + 1)
            if is_nullable(field["items"]):
                inner = f"{a} is null ? {b} is null : {b} is not null && ({inner})"
            return f"EqualItems({left}, {right}, ({a}, {b}) => {inner})"
        kind = cs(field)
        if kind == "byte[]": return f"{left}.AsSpan().SequenceEqual({right})"
        if kind == "TypeNode": return f"TypeNodeDataEquality.Equal({left}, {right})"
        if kind in {"SourceTypeFacts", "SourceBoundsSnapshot", "SourceTypeVector", "OwnerConstraintOverrideBounds"}:
            return f"{left}.FrozenText == {right}.FrozenText"
        if kind == "NullableRepresentationFrame": return f"EqualFrame({left}, {right})"
        if kind == "PhysicalMemberTable": return f"{left}.StructurallyEquals({right})"
        if kind == "SourceTypeAlias": return f"SourceMetadataDataEquality.Equal({left}, {right})"
        if is_child(field): return f"((BirGraphValue){left}).StructurallyEquals((BirGraphValue){right})"
        return f"EqualityComparer<{kind}>.Default.Equals({left}, {right})"

    def slot(field, value, target=None, optional=False):
        replacement = f"({cs(field)})replacement"
        if optional: replacement = f"OptionalField<{cs(field)}>.Of({replacement})"
        read = value if optional else target or value
        return f"new BirGraphSlot(() => (BirGraphValue){read}!, replacement => {target or value} = {replacement})"

    def slots(field, value, depth=0, target=None, optional=False, visitor="visit"):
        field = base(field)
        if field.get("type") == "array":
            index, captured = f"index{depth}", f"at{depth}"
            child = f"{value}[{captured}]"
            local = f"child{depth}"
            body = slots(field["items"], local, depth + 1, target=child, visitor=visitor)
            if is_nullable(field["items"]): body = f"if ({local} is not null) {{ {body} }}"
            return (f"for (var {index} = 0; {index} < {value}.Length; {index}++) {{ "
                    f"var {captured} = {index}; var {local} = {child}; {body} }}")
        return f"{visitor}({slot(field, value, target, optional)});"

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
        if len(fields) + (info["domain"] == "birCore") > 128:
            raise ValueError(f"Expand duplicate-field tracking before extending {name}")
        wide = len(fields) + (info["domain"] == "birCore") > 64
        seen_type, one = ("UInt128", "(UInt128)1") if wide else ("ulong", "1UL")
        node = info["domain"] == "birCore"
        roles = info.get("roles", [info["role"]] if "role" in info else [])
        bases = ["BirGraphNode" if node else "BirGraphValue"]
        bases += ["IBir" + role.capitalize() for role in roles]
        if "expression" in roles: bases += ["IBirAttributeValue"]
        if info.get("attributeValue"): bases += ["IBirAttributeValue"]
        if info.get("executablePayload"): bases += ["IBirExecutablePayload"]
        if key in {"birClassDefinition", "birInterfaceDefinition", "birEnumDefinition"}: bases += ["IBirTypeDeclaration"]
        model += [f"internal sealed record {name} : " + ", ".join(bases), "{"]
        if node:
            tag = shape["properties"]["k"]["const"]
            model += [f'    public override string Tag => {json.dumps(tag)};']
            nodes += [(tag, name, roles)]
        for field, value in fields:
            model += [f"    public OptionalField<{cs(value)}> {member(field)} {{ get; set; }}"]
        if key == "memberRef":
            model += ["    internal static BirMemberReference FromPhysical(MemberRefNode source) => new()", "    {"]
            for index, (field, value) in enumerate(fields):
                prop = member(field)
                expression = f"source.{prop}"
                if field not in shape.get("required", []):
                    item = f"value{index}"
                    converted = item + (".ToArray()" if value.get("type") == "array" else "")
                    expression = f"source.{prop} is {{ }} {item} ? OptionalField<{cs(value)}>.Of({converted}) : default"
                model += [f"        {prop} = {expression},"]
            model += ["    };", ""]
        stamps = [member(field) + ".OrDefault()" for field in ("sty", "ret", "dynRet")
                  if node and field in shape["properties"] and cs(shape["properties"][field]) == "TypeNode"]
        if stamps:
            model += ["    public override TypeNode? ResultTypeStamp => " + " ?? ".join(stamps) + ";"]
        binding_hints = [member(field) + ".OrDefault()" for field in ("sty", "type")
                         if node and field in shape["properties"] and cs(shape["properties"][field]) == "TypeNode"]
        if binding_hints:
            model += ["    public override TypeNode? CallBindingTypeHint => " + " ?? ".join(binding_hints) + ";"]
        explicit_source_types = [member(field) + ".OrDefault()" for field in ("sty", "type", "ret")
                                 if node and field in shape["properties"] and cs(shape["properties"][field]) == "TypeNode"]
        if explicit_source_types:
            model += ["    public override TypeNode? ExplicitSourceType => " + " ?? ".join(explicit_source_types) + ";"]
        if "declarationId" in shape["properties"]:
            model += ["    public override OptionalField<string> DeclarationIdentityFact => DeclarationId;"]
        if "typeArgs" in shape["properties"] and cs(shape["properties"]["typeArgs"]) == "TypeNode[]":
            model += ["    public override OptionalField<TypeNode[]> ApplicationTypeArguments { get => TypeArgs; set => TypeArgs = value; }"]
        for field, api in [("suspendFnTypePre", "SuspendFunctionTypeFact"), ("retSuspendFnTypePre", "SuspendFunctionReturnFact")]:
            if field in shape["properties"]:
                prop = member(field)
                model += [f"    public override OptionalField<TypeNode> {api} {{ get => {prop}; set => {prop} = value; }}"]
        model += ["    public override void VisitOwnTypeFields(Action<string, TypeNode> visit)", "    {"]
        for field, value in fields:
            if is_type_slot(value):
                prop = member(field)
                body = visit(value, prop + ".Value", callback="visit", cast="TypeNode",
                             prefix=json.dumps(field) + ", ")
                model += [f"        if ({prop}.HasValue) {{ {body} }}"]
        model += ["    }"]
        model += ["    public override void VisitStringFields(Action<string> visit)", "    {"]
        for field, value in fields:
            prop = member(field)
            if is_string_slot(value):
                body = visit(value, prop + ".Value", cast="string")
                model += [f"        if ({prop}.HasValue) {{ {body} }}"]
            elif is_type_slot(value):
                body = visit(value, prop + ".Value", callback="BirGraphNameFields.VisitTypeStrings",
                             cast="TypeNode", suffix=", visit")
                model += [f"        if ({prop}.HasValue) {{ {body} }}"]
            elif cs(value) == "BirLiteral":
                model += [f"        if ({prop}.HasValue && {prop}.Value is BirLiteral.TextLiteral text) visit(text.Value);"]
        model += ["    }"]
        synthetic_facts = [member(field) for field, _ in fields
                           if field in {"_syntheticTypeArgs", "sharedCellType", "sharedCellTypeParams"}]
        if synthetic_facts:
            model += ["    public override void ClearSyntheticFrameFacts()", "    {"]
            model += [f"        {prop} = default;" for prop in synthetic_facts]
            model += ["    }"]
        model += ["    public override void VisitNameFields(Action<string> visit)", "    {"]
        for field, value in fields:
            prop = member(field)
            if field in {"name", "var"} and cs(value) == "string":
                model += [f"        if ({prop}.HasValue) visit({prop}.Value);"]
            elif is_type_slot(value):
                body = visit(value, prop + ".Value", callback="BirGraphNameFields.VisitType", cast="TypeNode", suffix=", visit")
                model += [f"        if ({prop}.HasValue) {{ {body} }}"]
        model += ["    }"]
        model += ["    public override void VisitChildren(Action<BirGraphValue> visit)", "    {"]
        for field, value in fields:
            if is_child(value):
                prop = member(field)
                model += [f"        if ({prop}.HasValue) {{ {visit(value, prop + '.Value')} }}"]
        model += ["    }"]
        model += ["    public override void VisitChildSlots(Action<BirGraphSlot> visit)", "    {"]
        for field, value in fields:
            if is_child(value):
                prop = member(field)
                model += [f"        if ({prop}.HasValue) {{ {slots(value, prop + '.Value', target=prop, optional=True)} }}"]
        model += ["    }"]
        parameter_fields = [(field, value) for field, value in fields
                            if field == "params" and base(value).get("type") == "array"
                            and refkey(base(value)["items"]) in {"birParameter", "birLambdaParameter"}]
        if parameter_fields:
            model += ["    public override void VisitSourceParameters(Action<string, TypeNode> visit)", "    {"]
            for field, _ in parameter_fields:
                prop = member(field)
                model += [f"        if ({prop}.HasValue) foreach (var parameter in {prop}.Value)",
                          "            if (parameter.Name.HasValue && parameter.Type.HasValue) visit(parameter.Name.Value, parameter.Type.Value);"]
            model += ["    }"]
        source_regions = {}
        region_results = set()
        for region in info.get("sourceRegions", []):
            if set(region) != {"statements", "result"}:
                raise ValueError(f"Invalid source region metadata: {name}")
            statements, result = region["statements"], region["result"]
            if statements not in shape["properties"] or cs(shape["properties"][statements]) != "IBirStatement[]":
                raise ValueError(f"Source region is not a statement vector: {name}.{statements}")
            if result not in shape["properties"] or cs(shape["properties"][result]) != "IBirExpression":
                raise ValueError(f"Source result is not an expression child: {name}.{result}")
            if statements in source_regions or result in region_results:
                raise ValueError(f"Duplicate source region association: {name}")
            source_regions[statements] = result
            region_results.add(result)
        if any(is_child(value) and cs(value) == "IBirStatement[]" for _, value in fields):
            model += ["    public override void VisitSourceChildren(Action<BirGraphSlot> child, Action<BirSourceRegion> region)", "    {"]
            for field, value in fields:
                if not is_child(value) or field in region_results:
                    continue
                prop = member(field)
                if field in source_regions:
                    result = source_regions[field]
                    result_prop = member(result)
                    result_slot = slot(shape["properties"][result], result_prop + ".Value", target=result_prop, optional=True)
                    model += [f"        if ({prop}.HasValue || {result_prop}.HasValue) region(new BirSourceRegion(",
                              f"            {prop}.OrEmpty(), {result_prop}.HasValue ? {result_slot} : (BirGraphSlot?)null, statements => {prop} = statements));"]
                else:
                    body = f"region(new BirSourceRegion({prop}.Value, ReplaceStatements: statements => {prop} = statements));" if cs(value) == "IBirStatement[]" else slots(
                        value, prop + '.Value', target=prop, optional=True, visitor="child")
                    model += [f"        if ({prop}.HasValue) {{ {body} }}"]
            model += ["    }"]
        if "executionFrame" in info:
            model += ["    public override void VisitCurrentFrameSlots(Action<BirGraphSlot> visit)", "    {"]
            for field in info["executionFrame"]:
                value = shape["properties"][field]
                if not is_child(value):
                    raise ValueError(f"Current-frame operand is not a child: {name}.{field}")
                prop = member(field)
                model += [f"        if ({prop}.HasValue) {{ {slots(value, prop + '.Value', target=prop, optional=True)} }}"]
            model += ["    }"]
        model += ["    public override BirGraphValue RewriteChildren(Func<BirGraphValue, BirGraphValue> rewrite) => this with", "    {"]
        for field, value in fields:
            if is_child(value):
                prop = member(field)
                model += [f"        {prop} = {prop}.HasValue ? OptionalField<{cs(value)}>.Of({rewrite(value, prop + '.Value')}) : {prop},"]
        model += ["    };", "    public override BirGraphValue DeepClone() => this with", "    {"]
        for field, value in fields:
            if is_child(value) or base(value).get("type") == "array" or cs(value) in {"byte[]", "TypeNode"}:
                prop = member(field)
                model += [f"        {prop} = {prop}.HasValue ? OptionalField<{cs(value)}>.Of({copy_graph(value, prop + '.Value')}) : {prop},"]
        model += ["    };", "    public override BirGraphValue RewriteTypeFields(Func<TypeNode, TypeNode> rewrite) => this with", "    {"]
        for field, value in fields:
            if is_type_slot(value):
                prop = member(field)
                model += [f"        {prop} = {prop}.HasValue ? OptionalField<{cs(value)}>.Of({rewrite_type(value, prop + '.Value')}) : {prop},"]
        model += ["    };", "    public override BirGraphValue RewriteGenericFrame(",
                  "        Func<BirGenericFrameRole, TypeNode, TypeNode> type,",
                  "        Func<BirGenericFrameRole, BirGraphValue, BirGraphValue> child) => this with", "    {"]
        frame_roles = {"Lexical", "ReferencedDeclaration", "ResolvedDeclaration", "OverrideDeclaration",
                       "SharedCaptureDeclaration", "LocalDeclaration", "SyntheticDeclaration",
                       "SyntheticApplication", "FrozenSourceFact", "StorageSourceType"}
        for field, value in fields:
            # Frozen type facts are typed in memory, but their encoded field
            # contract remains distinct from an executable lexical type slot.
            type_slot = is_type_slot(value) or cs(value) == "TypeNode"
            if not is_child(value) and not type_slot:
                continue
            role = value.get("x-dotkt-frame-role", "FrozenSourceFact" if not is_type_slot(value) and type_slot else "Lexical")
            if role not in frame_roles:
                raise ValueError(f"Unknown generic-frame role: {name}.{field}: {role}")
            prop = member(field)
            prefix = f"BirGenericFrameRole.{role}, "
            expression = (rewrite(value, prop + ".Value", callback="child", prefix=prefix) if is_child(value)
                          else rewrite_type(value, prop + ".Value", callback="type", prefix=prefix))
            model += [f"        {prop} = {prop}.HasValue ? OptionalField<{cs(value)}>.Of({expression}) : {prop},"]
        model += ["    };", "    public override void RewriteTypeFieldsInPlace(Func<TypeNode, TypeNode> rewrite)", "    {"]
        for field, value in fields:
            if is_type_slot(value):
                prop = member(field)
                model += [f"        if ({prop}.HasValue) {prop} = OptionalField<{cs(value)}>.Of({rewrite_type(value, prop + '.Value')});"]
        model += ["    }", "    public override void VisitGenericFrame(",
                  "        Action<BirGenericFrameRole, TypeNode> type,",
                  "        Action<BirGenericFrameRole, BirGraphValue> child)", "    {"]
        for field, value in fields:
            type_slot = is_type_slot(value) or cs(value) == "TypeNode"
            if not is_child(value) and not type_slot: continue
            role = value.get("x-dotkt-frame-role", "FrozenSourceFact" if not is_type_slot(value) and type_slot else "Lexical")
            prop, prefix = member(field), f"BirGenericFrameRole.{role}, "
            body = visit(value, prop + ".Value", callback="child", prefix=prefix) if is_child(value) else visit(
                value, prop + ".Value", callback="type", prefix=prefix, cast="TypeNode")
            model += [f"        if ({prop}.HasValue) {{ {body} }}"]
        model += ["    }", "    public override void RewriteGenericFrameInPlace(",
                  "        Func<BirGenericFrameRole, TypeNode, TypeNode> type,",
                  "        Action<BirGenericFrameRole, BirGraphValue> child)", "    {"]
        for field, value in fields:
            type_slot = is_type_slot(value) or cs(value) == "TypeNode"
            if not is_child(value) and not type_slot: continue
            role = value.get("x-dotkt-frame-role", "FrozenSourceFact" if not is_type_slot(value) and type_slot else "Lexical")
            prop, prefix = member(field), f"BirGenericFrameRole.{role}, "
            if is_child(value):
                body = visit(value, prop + ".Value", callback="child", prefix=prefix)
            else:
                body = f"{prop} = OptionalField<{cs(value)}>.Of({rewrite_type(value, prop + '.Value', callback='type', prefix=prefix)});"
            model += [f"        if ({prop}.HasValue) {{ {body} }}"]
        model += ["    }", "    public override void RewriteRepresentationInPlace(",
                  "        Func<BirGenericFrameRole, BirRepresentationRole, TypeNode, TypeNode> type,",
                  "        Action<BirGenericFrameRole, BirRepresentationRole, BirGraphValue> child)", "    {"]
        representation_roles = {"Lexical", "ValueType", "TypeArguments", "Element", "Return", "ResultStamp", "DeclaredMemberType",
                                "Declaration", "Constraint", "Foreign", "TypeParameterDeclarations", "Attributes", "InheritedImplementation"}
        for field, value in fields:
            if not is_child(value) and not is_type_slot(value): continue
            role = value.get("x-dotkt-frame-role", "Lexical")
            representation = value.get("x-dotkt-representation-role", "Lexical")
            if representation not in representation_roles:
                raise ValueError(f"Unknown representation role: {name}.{field}: {representation}")
            prop = member(field)
            prefix = f"BirGenericFrameRole.{role}, BirRepresentationRole.{representation}, "
            if is_child(value):
                body = visit(value, prop + ".Value", callback="child", prefix=prefix)
            else:
                body = f"{prop} = OptionalField<{cs(value)}>.Of({rewrite_type(value, prop + '.Value', callback='type', prefix=prefix)});"
            model += [f"        if ({prop}.HasValue) {{ {body} }}"]
        model += ["    }", "    public override void RewriteOwnershipInPlace(",
                  "        Func<BirOwnershipRole, TypeNode, TypeNode> type,",
                  "        Action<BirOwnershipRole, BirGraphValue> child)", "    {"]
        ownership_roles = {"Lexical", "ForeignVector", "MemberDescriptor", "SyntheticDeclaration", "FrozenFact"}
        for field, value in fields:
            type_slot = is_type_slot(value) or cs(value) == "TypeNode"
            if not is_child(value) and not type_slot: continue
            role = value.get("x-dotkt-ownership-role", "FrozenFact" if not is_type_slot(value) and type_slot else "Lexical")
            if role not in ownership_roles:
                raise ValueError(f"Unknown ownership traversal role: {name}.{field}: {role}")
            prop, prefix = member(field), f"BirOwnershipRole.{role}, "
            if is_child(value):
                body = visit(value, prop + ".Value", callback="child", prefix=prefix)
            else:
                body = f"{prop} = OptionalField<{cs(value)}>.Of({rewrite_type(value, prop + '.Value', callback='type', prefix=prefix)});"
            model += [f"        if ({prop}.HasValue) {{ {body} }}"]
        model += ["    }"]
        parameter_vectors = [(field, value) for field, value in fields if "x-dotkt-representation-parameters" in value]
        if parameter_vectors:
            model += ["    public override void RewriteRepresentationParametersInPlace(",
                      "        Func<BirRepresentationParameterRole, BirTypeParameter[], BirTypeParameter[]> rewrite)", "    {"]
            for field, value in parameter_vectors:
                role = value["x-dotkt-representation-parameters"]
                if role not in {"Owner", "Method", "SharedCell"} or cs(value) != "BirTypeParameter[]":
                    raise ValueError(f"Invalid representation parameter vector: {name}.{field}: {role}")
                prop = member(field)
                model += [f"        if ({prop}.HasValue) {prop} = rewrite(BirRepresentationParameterRole.{role}, {prop}.Value);"]
            model += ["    }"]
        model += ["    public override bool StructurallyEquals(BirGraphValue other)",
                  f"        => other is {name} value"]
        for field, value in fields:
            prop = member(field)
            model += [f"            && EqualField({prop}, value.{prop}, (left, right) => {equal(value, 'left', 'right')})"]
        model[-1] += ";"
        model += ["    public override void WriteJson(Utf8JsonWriter writer)", "    {", "        writer.WriteStartObject();"]
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
                  "        Require(reader, JsonTokenType.StartObject);", f"        {seen_type} seen = 0;"]
        codec += [f"        OptionalField<{cs(value)}> field{index} = default;" for index, (_, value) in enumerate(fields)]
        codec += [
                  "        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)", "        {",
                  "            Require(reader, JsonTokenType.PropertyName);", "            var key = reader.GetString();",
                  "            if (!reader.Read()) throw new FormatException();", "            switch (key)", "            {"]
        if node:
            codec += [f'                case "k": Seen(ref seen, {one} << {len(fields)}); if (ReadString(ref reader) != {json.dumps(tag)}) throw new FormatException(); break;']
        for index, (field, value) in enumerate(fields):
            prop, typ = member(field), cs(value)
            expr = read(value)
            decoded = f"OptionalField<{typ}>.Of({expr})"
            if refkey(value) != "literalValue":
                decoded = f"reader.TokenType == JsonTokenType.Null ? OptionalField<{typ}>.Null : {decoded}"
            codec += [f"                case {json.dumps(field)}:", f"                    Seen(ref seen, {one} << {index});",
                      f"                    field{index} = {decoded};", "                    break;"]
        codec += ["                default: throw new FormatException($\"Unknown lowering field {key}\");", "            }", "        }",
                  "        Require(reader, JsonTokenType.EndObject);", f"        return new {name}", "        {"]
        codec += [f"            {member(field)} = field{index}," for index, (field, _) in enumerate(fields)]
        codec += ["        };", "    }", ""]
    codec += ["    internal static BirGraphNode ReadNode(ref Utf8JsonReader reader) => Discriminator(reader, \"k\") switch", "    {"]
    codec += [f'        {json.dumps(tag)} => Read{name}(ref reader),' for tag, name, _ in nodes]
    codec += ['        var kind => throw new FormatException($"malformed current node: unknown k={kind}"),', "    };",
              "    internal static IBirExpression ReadBirExpression(ref Utf8JsonReader reader) => ReadNode(ref reader) as IBirExpression ?? throw new FormatException();",
              "    internal static IBirStatement ReadBirStatement(ref Utf8JsonReader reader) => ReadNode(ref reader) as IBirStatement ?? throw new FormatException();",
              "    internal static IBirAttributeValue ReadBirAttributeValue(ref Utf8JsonReader reader) =>",
              "        HasProperty(reader, \"k\") ? ReadBirExpression(ref reader) :",
              "        HasProperty(reader, \"bytes\") ? ReadBirOpaqueBytes(ref reader) : ReadBirAttributeLiteral(ref reader);",
              "    internal static IBirTypeDeclaration ReadBirTypeDeclaration(ref Utf8JsonReader reader) => Discriminator(reader, \"kind\") switch", "    {",
              *[f'        {json.dumps(kind)} => Read{name}(ref reader),' for kind, name in declaration_cases(definitions)],
              "        _ => throw new FormatException(),", "    };", "}", ""]
    # Type nodes are structural leaves of the graph visitor, but their actual
    # object-valued wire fields still participate in a name reservation scan.
    # Generate this traversal from the same maintained type/classifier shapes;
    # frozen JSON strings never arrive here through is_type_slot above.
    model += ["internal static class BirGraphNameFields", "{"]

    def type_names(field, expression, wire_name=None, depth=0, all_strings=False, nodes=False):
        field = base(field)
        if field.get("x-dotkt-kotlin-json-type") or field.get("x-dotkt-csharp-codec"):
            return []
        if field.get("type") == "array":
            item = f"item{depth}"
            body = type_names(field["items"], item, depth=depth + 1, all_strings=all_strings, nodes=nodes)
            return [f"foreach (var {item} in {expression}) {{ {' '.join(body)} }}"] if body else []
        key = refkey(field)
        suffix = "Nodes" if nodes else "Strings" if all_strings else ""
        if key == "type": return [f"VisitType{suffix}({expression}, visit);"]
        if key:
            if "properties" in definitions.get(key, {}):
                return [f"Visit{definitions[key]['x-dotkt-model']['name']}{suffix}({expression}, visit);"]
            return []
        return [f"visit({expression});"] if not nodes and (all_strings or wire_name in {"name", "var"}) and field.get("type") == "string" else []

    def type_name_fields(shape, variable, all_strings=False, nodes=False):
        result = []
        for field, value in shape["properties"].items():
            prop = value.get("x-dotkt-csharp-name", member(field))
            expression = f"{variable}.{prop}"
            body = type_names(value, expression, field, all_strings=all_strings, nodes=nodes)
            if body:
                if field not in shape.get("required", []) or is_nullable(value):
                    result += [f"if ({expression} is not null) {{ {' '.join(body)} }}"]
                else:
                    result += body
        return result

    type_shapes = [definitions[refkey(entry)] for entry in definitions["type"]["oneOf"]]
    nested_shapes = {}
    for shape in type_shapes:
        for field in shape["properties"].values():
            key = refkey(field)
            if key and key != "type" and "properties" in definitions.get(key, {}):
                nested_shapes[key] = definitions[key]
    for all_strings, nodes in ((False, False), (True, False), (False, True)):
        suffix = "Nodes" if nodes else "Strings" if all_strings else ""
        callback_type = "TypeNode" if nodes else "string"
        model += [f"    internal static void VisitType{suffix}(TypeNode value, Action<{callback_type}> visit)", "    {"]
        if nodes: model += ["        visit(value);"]
        model += ["        switch (value)", "        {"]
        for shape in type_shapes:
            name = shape["x-dotkt-model"]["name"]
            model += [f"            case TypeNode.{name} node:"]
            model += ["                " + line for line in type_name_fields(shape, "node", all_strings, nodes)]
            model += ["                break;"]
        model += ["        }", "    }"]
        for shape in nested_shapes.values():
            name = shape["x-dotkt-model"]["name"]
            model += [f"    static void Visit{name}{suffix}(TypeNode.{name} value, Action<{callback_type}> visit)", "    {"]
            model += ["        " + line for line in type_name_fields(shape, "value", all_strings, nodes)]
            model += ["    }"]
    model += ["}", ""]
    return {root / "toolchain/bir2cir/BirGraph.Model.Generated.cs": "\n".join(model),
            root / "toolchain/bir2cir/BirGraph.Json.Generated.cs": "\n".join(codec)}
