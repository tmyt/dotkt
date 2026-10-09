using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin capture is not its upper bound's CLR generic construction. Preserve that source fact before
// representation lowering, then forward the already-selected declaration in the captured runtime frame.
// Neither the declaration ABI nor managed-reference storage is changed by this operation.
static class CapturedGenericInvocation
{
    internal const string ArgumentKey = "_capturedGenericArgument";
    const string ResultKey = "_capturedGenericResult";
    const string TargetKey = "_capturedGenericTarget";
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    static int _next;
    static Dictionary<string, List<(string Owner, JsonObject Method)>> _localDeclarations;
    static Dictionary<string, string> _localTypeNames;

    public static void PreserveSourceFacts(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        _next = 0;
        var locals = SupertypeGraph.Collect(roots);
        foreach (var root in roots) Visit(root);

        void Visit(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (node is not JsonObject obj) return;
            if (Str(obj["k"]) is "callStatic" or "callInstance"
                && obj["typeArgs"] is JsonArray { Count: > 0 }
                && obj["suspendCall"]?.GetValue<bool>() != true
                && obj["args"] is JsonArray args
                && (obj["sig"] ?? obj["shapeTypes"]) is JsonArray signature)
            {
                for (var i = 0; i < args.Count && i < signature.Count; i++)
                {
                    if (TypeJson.Read(signature[i]) is not TypeNode.Fqn { Args: { } formalArgs } formal
                        || NodeType.Of(args[i]) is not TypeNode.Fqn { Args: { } actualArgs } actual
                        || formal.Name != actual.Name || formalArgs.Length != actualArgs.Length) continue;
                    var parameters = locals.TryGetValue(formal.Name, out var local)
                        ? local.Node["typeParams"] as JsonArray : refs.OwnerTypeParamDeclarations(formal.Name);
                    if (parameters == null || parameters.Count != formalArgs.Length) continue;
                    if (!formalArgs.Select((type, slot) => type is TypeNode.Tv { Scope: "method" }
                            && actualArgs[slot] is TypeNode.Star
                            && Str(parameters[slot]?["variance"]) is not ("in" or "out")).Any(match => match)) continue;
                    obj[ArgumentKey] = i;
                    if (obj[DeclarationIdentityBinding.Key] is JsonNode identity) obj[TargetKey] = identity.DeepClone();
                    if (NodeType.Stamp(obj) is TypeNode result) obj[ResultKey] = TypeJson.Write(result);
                    break;
                }
            }
            foreach (var child in obj.ToList()) Visit(child.Value);
        }
    }

    public static void PrepareLocalDeclarations(IEnumerable<JsonNode> roots)
    {
        _localDeclarations = new(StringComparer.Ordinal);
        var rootList = roots.OfType<JsonObject>().ToList();
        _localTypeNames = new(StringComparer.Ordinal);
        var typeDefs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var root in rootList) Collect(root, Str(root["fileClass"]));
        foreach (var name in typeDefs.Keys) MetadataName(name);
        void Collect(JsonObject owner, string ownerName)
        {
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (Str(method[DeclarationIdentityBinding.Key]) is string id)
                    {
                        if (!_localDeclarations.TryGetValue(id, out var matches)) _localDeclarations[id] = matches = new();
                        matches.Add((ownerName, method));
                    }
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                {
                    var name = Str(type["name"]);
                    typeDefs[name] = type;
                    Collect(type, name);
                }
        }
        string MetadataName(string name)
        {
            if (_localTypeNames.TryGetValue(name, out var known)) return known;
            var definition = typeDefs[name];
            var arity = (definition["typeParams"] as JsonArray)?.Count ?? 0;
            var parent = Str(definition["nestedIn"]);
            var simple = parent == null ? name : name[(name.LastIndexOf('.') + 1)..];
            var physical = Str(definition["kind"]) == "delegate" || arity == 0 ? simple : simple + "`" + arity;
            return _localTypeNames[name] = parent == null ? physical : MetadataName(parent) + "+" + physical;
        }
    }

    // Called after exact external selection, while the selected MethodDef's CLR constraints are still present.
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, HashSet<string> emittedLocalTypes)
    {
        var generated = new List<JsonObject>();
        Walk(root, new JsonArray(), new JsonArray());
        if (root is JsonObject file && generated.Count > 0)
        {
            var types = file["types"] as JsonArray;
            if (types == null) file["types"] = types = new JsonArray();
            foreach (var type in generated) { types.Add(type); emittedLocalTypes.Add(Str(type["name"])); }
        }

        void Walk(JsonNode node, JsonArray ownerFrame, JsonArray methodFrame)
        {
            if (node is JsonArray array)
            { foreach (var child in array.ToList()) Walk(child, ownerFrame, methodFrame); return; }
            if (node is not JsonObject obj) return;
            if (Str(obj["kind"]) is "class" or "interface" or "struct")
                ownerFrame = TypeParameterFrame.CloneDeclarations(obj);
            if (obj["params"] is JsonArray && obj["body"] is JsonArray)
                methodFrame = obj["typeParams"] as JsonArray ?? new JsonArray();
            foreach (var child in obj.ToList()) Walk(child.Value, ownerFrame, methodFrame);
            if (obj[ArgumentKey] is not JsonValue capture) { obj.Remove(ResultKey); return; }
            var index = capture.GetValue<int>();
            obj.Remove(ArgumentKey);
            var target = Str(obj[TargetKey]);
            obj.Remove(TargetKey);
            var result = TypeJson.Read(obj[ResultKey]) ?? NodeType.Of(obj);
            obj.Remove(ResultKey);
            if (obj["typeArgs"] is not JsonArray typeArgs || obj["args"] is not JsonArray arguments
                || Str(obj["k"]) is not ("clrGenericInstance" or "clrGenericStatic" or "callInstance" or "callStatic")) return;
            var member = obj["memberRef"] as JsonObject;
            var selectedFrame = obj[ClrMemberResolution.ResolvedMethodTypeParamsKey] as JsonArray;
            var isLocal = member == null;
            if (isLocal)
            {
                var callOwner = TypeJson.Read(obj["ownerType"] ?? obj["owner"] ?? obj["calleeOwner"]) as TypeNode.Fqn;
                if (target == null || callOwner == null || !_localDeclarations.TryGetValue(target, out var matches)) return;
                var exact = matches.Where(binding => binding.Owner == callOwner.Name
                    && Str(binding.Method["name"]) == Str(obj["method"])).ToList();
                if (exact.Count != 1) throw new InvalidOperationException("Captured local invocation has no unique declaration identity");
                var declaration = exact[0].Method;
                selectedFrame = declaration["typeParams"] as JsonArray ?? new JsonArray();
                member = new JsonObject
                {
                    ["declaringType"] = TypeJson.Write(callOwner), ["returnType"] = declaration["ret"].DeepClone(),
                    ["parameterTypes"] = new JsonArray(((JsonArray)declaration["params"]).OfType<JsonObject>()
                        .Select(parameter => parameter["type"].DeepClone()).ToArray()),
                };
            }
            if (selectedFrame == null) throw new InvalidOperationException("Captured invocation has no selected generic frame");
            var owner = TypeJson.Read(member["declaringType"]) as TypeNode.Fqn
                ?? throw new InvalidOperationException("Captured invocation has no selected declaring type");
            var declarationParams = (member["parameterTypes"] as JsonArray)?.Select(TypeJson.Read).ToArray()
                ?? throw new InvalidOperationException("Captured invocation has no selected parameter vector");
            var declaredReturn = TypeJson.Read(member["returnType"]);
            if (index < 0 || index >= arguments.Count || declarationParams.Length != arguments.Count)
                throw new InvalidOperationException("Captured invocation argument does not match selected declaration");
            // Reflection never carries a managed address. Ordinary exact ref/out calls do not have capture facts.
            if (declarationParams.Any(HasAddress) || HasAddress(declaredReturn)
                || declarationParams.Any(type => ByRefLike(type, refs)))
                throw new NotSupportedException("Captured invocation cannot transport managed-reference arguments or results");

            var arity = selectedFrame.Count;
            TypeNode LiftCaller(TypeNode type) => Map(type, tv => new TypeNode.Tv("method",
                arity + (tv.Scope == "type" ? tv.I : ownerFrame.Count + tv.I)));
            var liftedOwner = (TypeNode.Fqn)LiftCaller(owner);
            TypeNode CloseDeclaration(TypeNode type) => Map(type, tv => tv.Scope == "type"
                ? liftedOwner.Args?[tv.I] ?? throw new InvalidOperationException("Captured invocation has no owner frame")
                : tv);
            var frame = new JsonArray();
            foreach (var parameter in selectedFrame) frame.Add(CloneParameter(parameter, CloseDeclaration));
            foreach (var parameter in ownerFrame.Concat(methodFrame))
                frame.Add(CloneParameter(parameter, LiftCaller));
            for (var slot = 0; slot < frame.Count; slot++)
            {
                frame[slot]["name"] = "__captured" + slot;
                ((JsonObject)frame[slot]).Remove("variance");
            }
            var isInstance = Str(obj["k"]) is "clrGenericInstance" or "callInstance";
            var parameterTypes = declarationParams.Select(CloseDeclaration).ToList();
            if (isInstance) parameterTypes.Insert(0, liftedOwner);
            var forward = (JsonObject)obj.DeepClone();
            forward.Remove(ClrMemberResolution.ResolvedMethodTypeParamsKey);
            forward.Remove("sty");
            forward["typeArgs"] = new JsonArray(Enumerable.Range(0, arity)
                .Select(i => TypeJson.Write(new TypeNode.Tv("method", i))).ToArray());
            forward[Str(obj["k"]) is "callInstance" ? "ownerType" : Str(obj["k"]) is "callStatic" ? "owner" : "type"] = TypeJson.Write(liftedOwner);
            if (!isLocal) forward["memberRef"]["declaringType"] = TypeJson.Write(liftedOwner);
            forward[isLocal ? "sig" : "resolvedMemberParams"] = new JsonArray(declarationParams.Select(CloseDeclaration).Select(TypeJson.Write).ToArray());
            forward["ret"] = TypeJson.Write(CloseDeclaration(declaredReturn));
            if (!isLocal) forward["resolvedMemberReturn"] = TypeJson.Write(CloseDeclaration(declaredReturn));
            forward.Remove("dynRet");
            forward["args"] = new JsonArray(declarationParams.Select((_, i) => (JsonNode)Local(i + (isInstance ? 1 : 0))).ToArray());
            if (isInstance) forward["recv"] = Local(0);
            var name = "dotkt$captured$" + _next++;
            var returnsVoid = declaredReturn is TypeNode.Fqn { Name: "void" or "System.Void" };
            var body = returnsVoid
                ? new JsonArray(new JsonObject { ["k"] = "expr", ["e"] = forward })
                : new JsonArray(new JsonObject { ["k"] = "return", ["value"] = Cast(Object, forward) });
            var thunk = new JsonObject
            {
                ["name"] = "Invoke", ["static"] = true, ["vis"] = "public", ["typeParams"] = frame,
                ["override"] = false, ["virtual"] = false, ["abstract"] = false, ["objectOverride"] = false,
                ["params"] = new JsonArray(parameterTypes.Select((type, i) => (JsonNode)new JsonObject
                    { ["name"] = "p" + i, ["type"] = TypeJson.Write(type) }).ToArray()),
                ["ret"] = TypeJson.Write(returnsVoid ? new TypeNode.Fqn("void") : Object),
                ["body"] = body, ["attrs"] = new JsonArray(),
            };
            generated.Add(new JsonObject
            {
                ["name"] = name, ["kind"] = "class", ["generated"] = true, ["vis"] = "internal",
                ["typeParams"] = new JsonArray(), ["interfaces"] = new JsonArray(), ["fields"] = new JsonArray(),
                ["ctors"] = new JsonArray(), ["methods"] = new JsonArray(thunk), ["attrs"] = new JsonArray(),
            });
            var values = arguments.Select(argument => (JsonNode)Cast(Object, argument.DeepClone())).ToList();
            if (isInstance) values.Insert(0, Cast(Object, obj["recv"].DeepClone()));
            var fallback = typeArgs.Select(type => TypeJson.Read(type)).ToList();
            fallback.AddRange(Enumerable.Range(0, ownerFrame.Count).Select(i => (TypeNode)new TypeNode.Tv("type", i)));
            fallback.AddRange(Enumerable.Range(0, methodFrame.Count).Select(i => (TypeNode)new TypeNode.Tv("method", i)));
            var runtimeCall = new JsonObject
            {
                ["k"] = "clrStatic", ["type"] = TypeJson.Write(new TypeNode.Fqn(RuntimeOwner)),
                ["method"] = returnsVoid ? "starProjectionInvokeStaticHelperUnit" : "starProjectionInvokeStaticHelper",
                ["argTypes"] = new JsonArray(RuntimeSignature.Select(TypeJson.Write).ToArray()),
                ["ret"] = TypeJson.Write(returnsVoid ? new TypeNode.Fqn("void") : Object),
                ["args"] = new JsonArray(ClassRef(new TypeNode.Fqn(name)), Constant(Int, 0), Constant(String, "Invoke"),
                    Array(String, parameterTypes.Select(type => (JsonNode)Constant(String, RuntimeKey(type, refs)))),
                    Array(Type, fallback.Select(type => (JsonNode)ClassRef(type))), Constant(Int, index + (isInstance ? 1 : 0)),
                    Array(Object, values)),
            };
            // The reference twin describes this runtime helper using trusted stdlib aliases. Select that exact
            // source declaration, then let MemberRefJson take its shipped twin's physical signature as usual.
            var runtimeType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
            var sourceSignature = new TypeNode[] { runtimeType, new TypeNode.Fqn("kotlin.Int"),
                new TypeNode.Fqn("kotlin.String"), new TypeNode.Array(new TypeNode.Fqn("kotlin.String")),
                new TypeNode.Array(runtimeType), new TypeNode.Fqn("kotlin.Int"),
                new TypeNode.Array(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))) };
            if (!refs.TryResolveStaticMemberSignature(RuntimeOwner, Str(runtimeCall["method"]), 0, true,
                    sourceSignature, null, out _, out var runtimeDeclaration, out var runtimeDeclaringType))
                throw new InvalidOperationException("Captured invocation runtime helper has no exact declaration");
            runtimeCall["memberRef"] = ClrMemberResolution.MemberRefJson(runtimeDeclaration, "method", runtimeDeclaringType, null);
            runtimeCall["resolvedMemberReturn"] = runtimeCall["memberRef"]["returnType"].DeepClone();
            runtimeCall.Remove("argTypes");
            ForeignStarProjectionBinding.RequireRuntimeFallback();
            var replacement = returnsVoid || result == null || IsObject(result) ? runtimeCall : Cast(result, runtimeCall);
            obj.Clear();
            foreach (var item in replacement.ToList()) { replacement.Remove(item.Key); obj[item.Key] = item.Value; }
        }
    }

    static readonly TypeNode Object = new TypeNode.Fqn("object");
    static readonly TypeNode Int = new TypeNode.Fqn("int");
    static readonly TypeNode String = new TypeNode.Fqn("string");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");
    static readonly TypeNode[] RuntimeSignature = { Type, Int, String, new TypeNode.Array(String), new TypeNode.Array(Type), Int, new TypeNode.Array(Object) };
    static JsonObject Local(int index) => new() { ["k"] = "local", ["name"] = "p" + index };
    static JsonObject Cast(TypeNode type, JsonNode value) => new() { ["k"] = "cast", ["type"] = TypeJson.Write(type), ["e"] = value };
    static JsonObject ClassRef(TypeNode type) => new() { ["k"] = "classRef", ["type"] = TypeJson.Write(type) };
    static JsonObject Constant(TypeNode type, object value) => new() { ["k"] = "const", ["type"] = TypeJson.Write(type), ["value"] = JsonSerializerNode(value) };
    static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value);
    static JsonObject Array(TypeNode elem, IEnumerable<JsonNode> elements) => new() { ["k"] = "newArray", ["elem"] = TypeJson.Write(elem), ["elems"] = new JsonArray(elements.ToArray()) };
    static bool IsObject(TypeNode type) => type is TypeNode.Fqn { Name: "object" or "System.Object" } || type is TypeNode.Nullable n && IsObject(n.Of);
    static bool HasAddress(TypeNode type) => type is TypeNode.ByRef or TypeNode.Ptr || type is TypeNode.Mod m && HasAddress(m.Of);
    static bool ByRefLike(TypeNode type, ReferenceMetadataIndex refs) => type is TypeNode.Fqn f && refs.IsByRefLikeFqn(f);
    static JsonObject CloneParameter(JsonNode parameter, Func<TypeNode, TypeNode> map)
    {
        var clone = parameter is JsonObject definition ? (JsonObject)definition.DeepClone()
            : new JsonObject { ["name"] = Str(parameter) ?? throw new InvalidOperationException("Captured invocation has a malformed generic parameter") };
        if (clone["constraints"] is JsonArray constraints)
            clone["constraints"] = new JsonArray(constraints.Select(type => TypeJson.Write(map(TypeJson.Read(type)))).ToArray());
        return clone;
    }
    static TypeNode Map(TypeNode type, Func<TypeNode.Tv, TypeNode> tv) => type switch
    {
        TypeNode.Tv variable => tv(variable),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(arg => Map(arg, tv)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Map(n.Of, tv)),
        TypeNode.Array a => new TypeNode.Array(Map(a.Elem, tv), a.Rank, a.SzArray),
        TypeNode.ByRef r => new TypeNode.ByRef(Map(r.Of, tv)),
        TypeNode.Ptr p => new TypeNode.Ptr(Map(p.Of, tv)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, Map(m.M, tv), Map(m.Of, tv)),
        _ => type,
    };
    static string RuntimeKey(TypeNode type, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Tv tv => (tv.Scope == "method" ? "m" : "t") + tv.I,
        TypeNode.Nullable n => RuntimeKey(n.Of, refs),
        TypeNode.Array a => "a" + (a.SzArray ? "s" : "m") + a.Rank + "[" + RuntimeKey(a.Elem, refs) + "]",
        TypeNode.Fqn f => (f.Args is { Length: > 0 } ? "g{" : "n{") + PhysicalName(f, refs) + "}"
            + (f.Args is { Length: > 0 } args ? "<" + string.Join(",", args.Select(arg => RuntimeKey(arg, refs))) + ">" : ""),
        _ => throw new InvalidOperationException("Captured invocation has an unsupported structural parameter type"),
    };
    static string PhysicalName(TypeNode.Fqn type, ReferenceMetadataIndex refs) => type.Name switch
    {
        "object" => "System.Object", "int" => "System.Int32", "string" => "System.String",
        _ => _localTypeNames.TryGetValue(type.Name, out var local) ? local
            : refs.TryExactPhysicalTypeName(type.Name, type.Args?.Length ?? 0, out var name) ? name : type.Name,
    };
    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
