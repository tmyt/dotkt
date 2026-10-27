using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// An open Kotlin array preserves its allocation's component, not the caller's concrete T.
// Its element is therefore an erased value until an actual consuming slot asks for T.
// Project only body-local value storage here; signatures, allocation witnesses, reified
// operations and native locations retain their independently owned representations.
static class OpenArrayElementValues
{
    sealed record Slot(JsonObject Declaration, string Name, TypeNode Type);
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");

    internal static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex references,
        ValueTypeOracle isValue = null, GenericRepresentationPolicy policy = null)
    {
        var files = roots.OfType<JsonObject>().ToArray();
        var interfaces = new Dictionary<string, bool>(StringComparer.Ordinal);
        var frames = references?.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal) ?? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal);
        void Types(JsonObject container)
        {
            foreach (var type in (container["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (Text(type["name"]) is string name) interfaces[name] = Text(type["kind"]) == "interface";
                if (Text(type["name"]) is string owner && KotlinSupertypesRecord.ReadNullableFrame(type) is { } frame)
                    frames[owner] = frame;
                Types(type);
            }
        }
        foreach (var file in files) Types(file);
        bool IsInterface(TypeNode type) => type is TypeNode.Fqn named
            && (interfaces.TryGetValue(named.Name, out var local) ? local : references?.IsInterfaceType(named) == true);
        void Container(JsonObject container, JsonObject owner)
        {
            foreach (var method in (container["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Project(method, owner, IsInterface, frames, isValue ?? (_ => false), policy);
            foreach (var constructor in (container["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Project(constructor, owner, IsInterface, frames, isValue ?? (_ => false), policy);
            foreach (var type in (container["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Container(type, type);
        }
        foreach (var file in files) Container(file, null);
    }

    static TypeNode.Tv Variable(TypeNode type) => type switch {
        TypeNode.Tv variable => variable,
        TypeNode.Projection projection => Variable(projection.Of),
        _ => null,
    };

    static void Project(JsonObject method, JsonObject owner, Func<TypeNode, bool> isInterface,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy)
    {
        var executable = (method["preStmts"] as JsonArray ?? new JsonArray())
            .Concat(method["thisArgs"] as JsonArray ?? method["baseArgs"] as JsonArray ?? new JsonArray())
            .Concat(method["body"] as JsonArray ?? new JsonArray()).ToArray();
        if (executable.Length == 0) return;
        var reads = new Dictionary<JsonObject, TypeNode.Tv>();
        var uses = new Dictionary<JsonObject, Slot>();
        var slots = new List<Slot>();
        var loopSlots = new Dictionary<JsonObject, Slot>();
        var bindings = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (var parameter in (method["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (Text(parameter["name"]) is string name)
                bindings[name] = new Slot(parameter, name, TypeJson.Read(parameter["type"]));

        TypeNode.Tv Element(JsonObject operation, Dictionary<string, Slot> scope)
        {
            var types = scope.ToDictionary(pair => pair.Key, pair => pair.Value.Type, StringComparer.Ordinal);
            return Variable(ArrayConstructionLowering.ArrayElementOf(
                StaticType.Surface(operation["array"], BirScope.FromVars(types))));
        }

        void Bind(JsonNode node, Dictionary<string, Slot> scope, bool location = false)
        {
            if (node is JsonArray array)
            {
                var block = new Dictionary<string, Slot>(scope, StringComparer.Ordinal);
                foreach (var child in array) Bind(child, block);
                return;
            }
            if (node is not JsonObject obj) return;
            if (obj["k"] == null && obj["params"] is JsonArray && obj["body"] is JsonArray) return;
            var kind = Text(obj["k"]);
            if (kind is "local" or "setLocal" && Text(obj["name"]) is string used
                && scope.TryGetValue(used, out var slot)) uses[obj] = slot;
            if (kind == "arrayGet" && !location && Element(obj, scope) is { } read) reads[obj] = read;
            if (kind == "forArray" && Text(obj["var"]) is string loopName)
            {
                Bind(obj["array"], scope);
                var loop = new Slot(obj, loopName, TypeJson.Read(obj["elem"]));
                if (Element(obj, scope) is { } element)
                {
                    loop = loop with { Type = element };
                    loopSlots[obj] = loop;
                }
                var inner = new Dictionary<string, Slot>(scope, StringComparer.Ordinal) { [loopName] = loop };
                Bind(obj["body"], inner);
                return;
            }
            foreach (var (key, child) in obj)
            {
                if (key is "attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey) continue;
                if (key == "args" && child is JsonArray arguments)
                {
                    var parameters = obj["memberRef"]?["parameterTypes"] as JsonArray
                        ?? obj["sig"] as JsonArray ?? obj["argTypes"] as JsonArray;
                    for (var i = 0; i < arguments.Count; i++)
                        Bind(arguments[i], scope, parameters != null && i < parameters.Count
                            && SignatureValueTypes.Of(TypeJson.Read(parameters[i])) is TypeNode.ByRef);
                }
                else Bind(child, scope, kind == "byrefOf" && key == "inner"
                    || location && kind == "valueBlock" && key == "result");
            }
            if (kind == "var" && Text(obj["name"]) is string declared)
            {
                var local = new Slot(obj, declared, TypeJson.Read(obj["type"]));
                slots.Add(local);
                scope[declared] = local;
            }
        }
        foreach (var statement in executable) Bind(statement, bindings);
        if (reads.Count == 0 && loopSlots.Count == 0) return;

        var origins = loopSlots.Values.ToDictionary(slot => slot, slot => (TypeNode.Tv)slot.Type);
        TypeNode.Tv Origin(JsonNode node)
        {
            if (node is not JsonObject obj) return null;
            if (reads.TryGetValue(obj, out var read)) return read;
            if (Text(obj["k"]) == "local" && uses.TryGetValue(obj, out var slot))
                return origins.GetValueOrDefault(slot);
            if (Text(obj["k"]) == "cast" && NullableBool(obj["reifiedTypeOperand"]) == false
                && TypeJson.Read(obj["type"]) is TypeNode.Tv target && Origin(obj["e"]) is { } source
                && target == source) return target;
            if (Text(obj["k"]) == "valueBlock") return Origin(obj["result"]);
            if (Text(obj["k"]) == "cond") return Origin(obj["then"]) ?? Origin(obj["else"]);
            return null;
        }
        bool Record(Slot slot, JsonNode value)
        {
            if (origins.ContainsKey(slot) || Origin(value) is not { } source) return false;
            if (slot.Type is TypeNode.Tv variable) origins[slot] = variable;
            else if (IsObject(slot.Type)) origins[slot] = source;
            else return false;
            return true;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var slot in slots) changed |= Record(slot, slot.Declaration["init"]);
            foreach (var (use, slot) in uses)
                if (Text(use["k"]) == "setLocal") changed |= Record(slot, use["value"]);
        } while (changed);

        var casts = new Dictionary<JsonObject, TypeNode>();
        var constrained = new HashSet<JsonObject>();
        void Plan(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array) Plan(child); return; }
            if (node is not JsonObject obj) return;
            if (obj["k"] == null && obj["params"] is JsonArray && obj["body"] is JsonArray) return;
            if (Text(obj["k"]) == "cast" && NullableBool(obj["reifiedTypeOperand"]) == false
                && TypeJson.Read(obj["type"]) is TypeNode.Tv target && Origin(obj["e"]) == target)
                casts[obj] = Bound(target, new HashSet<TypeNode.Tv>());
            if (Text(obj["k"]) == "constrainedCall" && Origin(obj["recv"]) != null)
                constrained.Add(obj);
            foreach (var (key, child) in obj)
                if (key is not ("attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey)) Plan(child);
        }

        TypeNode Bound(TypeNode.Tv variable, HashSet<TypeNode.Tv> visited)
        {
            if (!visited.Add(variable)) throw new InvalidOperationException("Cyclic erased array-element bound");
            var declaration = variable.Scope == "method" ? method : owner;
            var frame = declaration == null ? null : variable.Scope == "method"
                ? NullableRepresentationTypes.DeclarationMethodFrame(declaration)
                : KotlinSupertypesRecord.ReadNullableFrame(declaration);
            var index = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
            var position = frame?.SourcePosition(index) ?? variable.I;
            var key = variable.Scope == "method" ? NullableGenericErasure.MethodTypeParameterBoundsPre : KotlinSupertypesRecord.PreKey;
            var source = Text(declaration?[key]) is string encoded
                ? JsonNode.Parse(encoded)?["bounds"]?[index.ToString()] as JsonArray : null;
            var parameter = (declaration?["typeParams"] as JsonArray)?[position] as JsonObject;
            var bounds = (source ?? parameter?["constraints"] as JsonArray ?? new JsonArray())
                .Select(TypeJson.Read).Where(type => type != null).ToArray();
            if (source != null)
            {
                var mapping = new NullableRepresentationTypes(
                    owner == null ? null : KotlinSupertypesRecord.ReadNullableFrame(owner),
                    NullableRepresentationTypes.DeclarationMethodFrame(method), frames, isValue, policy: policy);
                bounds = bounds.Select(mapping.Slot).ToArray();
            }
            var bound = bounds.FirstOrDefault(type => !isInterface(type)) ?? bounds.FirstOrDefault() ?? ObjectType;
            return bound is TypeNode.Tv parent ? Bound(parent, visited) : bound;
        }
        foreach (var statement in executable) Plan(statement);
        foreach (var (read, _) in reads)
        {
            read["elem"] = TypeJson.Write(ObjectType);
            read["sty"] = TypeJson.Write(ObjectType);
        }
        foreach (var (loop, _) in loopSlots) loop["elem"] = TypeJson.Write(ObjectType);
        foreach (var slot in origins.Keys)
            if (slot.Type is TypeNode.Tv && Text(slot.Declaration["k"]) == "var")
                slot.Declaration["type"] = TypeJson.Write(ObjectType);
        foreach (var (use, slot) in uses)
            if (Text(use["k"]) == "local" && origins.ContainsKey(slot))
                use["sty"] = TypeJson.Write(ObjectType);
        foreach (var (cast, bound) in casts)
        {
            // A raw non-reified generic guard admits null. The typed consumer,
            // if any, still owns its actual physical extraction and null check.
            var target = IsObject(bound) ? ObjectType : new TypeNode.Nullable(bound);
            cast["type"] = TypeJson.Write(target);
            if (cast["sty"] != null) cast["sty"] = TypeJson.Write(target);
        }
        foreach (var call in constrained)
        {
            var selected = call["iface"]?.DeepClone()
                ?? throw new InvalidOperationException("Erased array receiver lost its selected constraint owner");
            call["k"] = "callInstance";
            call["ownerType"] = selected;
            call["virtual"] = true;
            call["recv"] = new JsonObject { ["k"] = "cast", ["type"] = selected.DeepClone(), ["e"] = call["recv"].DeepClone() };
            call.Remove("iface");
            call.Remove("recvType");
        }
    }

    static bool IsObject(TypeNode type) => type is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" }
        || type is TypeNode.Nullable nullable && IsObject(nullable.Of);

    internal static void SelfTest()
    {
        var root = JsonNode.Parse("""
        {"fileClass":"ArrayValueControl","methods":[{"name":"consume","typeParams":["T"],
         "params":[{"name":"xs","type":{"t":"array","elem":{"t":"tv","scope":"method","i":0}}}],
         "ret":{"t":"fqn","name":"kotlin.Any"},"body":[
          {"k":"var","name":"value","type":{"t":"tv","scope":"method","i":0},"init":
           {"k":"arrayGet","array":{"k":"local","name":"xs"},"index":{"k":"const","type":{"t":"fqn","name":"kotlin.Int"},"value":0}}},
          {"k":"var","name":"alias","type":{"t":"tv","scope":"method","i":0},"init":{"k":"local","name":"value"}},
          {"k":"return","value":{"k":"cast","reifiedTypeOperand":false,"type":{"t":"tv","scope":"method","i":0},"e":{"k":"local","name":"alias"}}}
         ]}]}
        """).AsObject();
        var pristine = root.DeepClone();
        ApplyAll(new[] { root }, null);
        var method = root["methods"][0];
        if (TypeJson.Read(method["params"][0]["type"]) is not TypeNode.Array { Elem: TypeNode.Tv }
            || TypeJson.Read(method["body"][0]["type"]) != ObjectType
            || TypeJson.Read(method["body"][0]["init"]["elem"]) != ObjectType
            || TypeJson.Read(method["body"][1]["type"]) != ObjectType
            || TypeJson.Read(method["body"][2]["value"]["type"]) != ObjectType)
            throw new InvalidOperationException("Open array values lost erased locals or their separate array signature");

        var expanded = JsonNode.Parse(pristine.ToJsonString().Replace("\"i\":0", "\"i\":2")).AsObject();
        var expandedMethod = expanded["methods"][0].AsObject();
        expandedMethod["typeParams"] = JsonNode.Parse("""
        ["A","B",{"name":"T","constraints":[{"t":"fqn","name":"System.Collections.Generic.List",
         "args":[{"t":"tv","scope":"method","i":3}]}]},"$nullable1"]
        """);
        expandedMethod[NullableRepresentationTypes.MethodFrameKey] =
            "{\"sourceArity\":3,\"nullable\":[1],\"storage\":[],\"nullableStorage\":[],\"order\":[0,1,2,3]}";
        expandedMethod[NullableGenericErasure.MethodTypeParameterBoundsPre] =
            "{\"bounds\":{\"2\":[{\"t\":\"fqn\",\"name\":\"System.Collections.Generic.List\",\"args\":[{\"t\":\"nullable\",\"of\":{\"t\":\"tv\",\"scope\":\"method\",\"i\":1}}]}]}}";
        ApplyAll(new[] { expanded }, null);
        if (TypeJson.Read(expandedMethod["body"][2]["value"]["type"]) is not TypeNode.Nullable {
                Of: TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "method", I: 3 }] } })
            throw new InvalidOperationException("Erased array value bound did not close its source nullable frame");

        var nestedMethod = pristine["methods"][0].DeepClone().AsObject();
        nestedMethod["typeParams"] = JsonNode.Parse("""
        [{"name":"T","constraints":[{"t":"fqn","name":"Carrier","args":[{"t":"tv","scope":"type","i":2}]}]}]
        """);
        nestedMethod[NullableGenericErasure.MethodTypeParameterBoundsPre] =
            "{\"bounds\":{\"0\":[{\"t\":\"fqn\",\"name\":\"Carrier\",\"args\":[{\"t\":\"tv\",\"scope\":\"type\",\"i\":1}]}]}}";
        var nestedOwner = new JsonObject {
            ["name"] = "NestedOwner", ["kind"] = "class",
            ["typeParams"] = new JsonArray("Outer", "$nullableOuter", "Inner"),
            ["methods"] = new JsonArray(nestedMethod),
        };
        nestedOwner[KotlinSupertypesRecord.PreKey] =
            "{\"nullableFrame\":{\"sourceArity\":2,\"nullable\":[0],\"storage\":[],\"nullableStorage\":[],\"order\":[0,2,1]}}";
        var physicalBounds = nestedOwner.DeepClone().AsObject();
        physicalBounds["name"] = "PhysicalBoundsOwner";
        physicalBounds["methods"][0].AsObject().Remove(NullableGenericErasure.MethodTypeParameterBoundsPre);
        ApplyAll(new[] { new JsonObject { ["types"] = new JsonArray(nestedOwner, physicalBounds) } }, null);
        foreach (var declaration in new[] { nestedOwner, physicalBounds })
            if (TypeJson.Read(declaration["methods"][0]["body"][2]["value"]["type"]) is not TypeNode.Nullable {
                    Of: TypeNode.Fqn { Name: "Carrier", Args: [TypeNode.Tv { Scope: "type", I: 2 }] } })
                throw new InvalidOperationException("Erased array bound confused source and nested physical frame indices");

        var nativeLocal = pristine.DeepClone().AsObject();
        var nativeBody = nativeLocal["methods"][0]["body"].AsArray();
        nativeBody.Add(JsonNode.Parse("""
        {"k":"exprStmt","expr":{"k":"callStatic","method":"native","sig":[
         {"t":"byRef","of":{"t":"tv","scope":"method","i":0}},
         {"t":"byRef","of":{"t":"tv","scope":"method","i":0}}],
         "args":[{"k":"local","name":"value"},{"k":"local","name":"value"}]}}
        """));
        ApplyAll(new[] { nativeLocal }, null);
        var nativeArguments = nativeBody[^1]["expr"]["args"].AsArray();
        var nativeElement = new TypeNode.Tv("method", 0);
        if (NullableTvErasureCallRealign.CoerceForFixedSlot(nativeArguments[0], ObjectType,
                new TypeNode.ByRef(nativeElement), _ => false) != null)
            throw new InvalidOperationException("Value coercion wrapped an exact native local location");
        PhysicalValueCoercion.AllocateNativeLocals(nativeBody, Array.Empty<JsonObject>(), nativeBody);
        if (TypeJson.Read(nativeBody[0]["type"]) != nativeElement
            || nativeArguments.Any(argument => Text(argument["k"]) != "local" || Text(argument["name"]) != "value"))
            throw new InvalidOperationException("Native allocation lost exact same-slot array-fed local storage");

        var address = pristine.DeepClone().AsObject();
        address["methods"][0]["body"] = JsonNode.Parse("""
        [{"k":"exprStmt","expr":{"k":"callStatic","method":"native","sig":[{"t":"byRef","of":{"t":"tv","scope":"method","i":0}}],
          "args":[{"k":"arrayGet","elem":{"t":"tv","scope":"method","i":0},"array":{"k":"local","name":"xs"},
                   "index":{"k":"const","type":{"t":"fqn","name":"kotlin.Int"},"value":0}}]}}]
        """);
        ApplyAll(new[] { address }, null);
        if (TypeJson.Read(address["methods"][0]["body"][0]["expr"]["args"][0]["elem"]) is not TypeNode.Tv)
            throw new InvalidOperationException("Array value projection changed a native element location");

        var delegation = pristine.DeepClone().AsObject();
        var constructor = delegation["methods"][0].DeepClone().AsObject();
        constructor["baseArgs"] = new JsonArray(constructor["body"][0]["init"].DeepClone());
        constructor["body"] = new JsonArray();
        delegation["methods"] = new JsonArray();
        delegation["types"] = new JsonArray(new JsonObject {
            ["kind"] = "class", ["name"] = "Derived", ["ctors"] = new JsonArray(constructor),
        });
        ApplyAll(new[] { delegation }, null);
        if (TypeJson.Read(constructor["baseArgs"][0]["elem"]) != ObjectType)
            throw new InvalidOperationException("Array value projection missed executable constructor delegation");
        Console.WriteLine("[open array element values] self-test OK (erased reads/aliases, retained signature)");
    }

    static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static bool? NullableBool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;
}
