using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Initial value-representation stage for non-reified Kotlin arrays. Exact foreign
// declarations remain owned by their CLR binding. The array object is not copied.
// This stage is under development: store checks, erased-bound cast checks and
// native addressable locations must be completed before this ABI is usable.
static class GenericArrayValueLowering
{
    const string CastOperandKey = "_genericArrayReifiedOperand";

    public static void RecordCastOperands(JsonNode node)
    {
        if (node is JsonArray array) { foreach (var child in array) RecordCastOperands(child); return; }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToArray()) RecordCastOperands(child);
        if (Str(obj["k"]) is "cast" or "isInstRef"
            && TypeJson.Read(obj["type"]) is TypeNode.Array arrayType && ContainsVariable(arrayType.Elem)
            && obj["reifiedTypeOperand"] is JsonValue sourceFact)
            obj[CastOperandKey] = sourceFact.DeepClone();
    }
    sealed class Context
    {
        public string Owner;
        public string Store;
        public bool UsesStore;
        public string Cast;
        public bool UsesCast;
        public readonly HashSet<string> Names = new();
        int next;
        public string Temporary()
        {
            string name;
            do name = "$genericArray$" + next++; while (!Names.Add(name));
            return name;
        }
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        foreach (var ownerFiles in roots.OfType<JsonObject>().GroupBy(file => Str(file["fileClass"])))
            ApplyOwner(ownerFiles.ToArray());
    }

    static void ApplyOwner(JsonObject[] files)
    {
        var file = files[0];
        var methods = file["methods"] as JsonArray;
        var used = files.SelectMany(part => (part["methods"] as JsonArray)?.OfType<JsonObject>()
            ?? Enumerable.Empty<JsonObject>()).Select(method => Str(method["name"])).ToHashSet();
        var name = "$genericArrayStore";
        for (var suffix = 0; used?.Contains(name) == true; suffix++) name = "$genericArrayStore$" + suffix;
        var castName = "$genericArrayCast";
        for (var suffix = 0; used?.Contains(castName) == true; suffix++) castName = "$genericArrayCast$" + suffix;
        var context = new Context { Owner = Str(file["fileClass"]), Store = name, Cast = castName };
        foreach (var part in files) CollectNames(part, context.Names);
        foreach (var part in files) Visit(part, BirScope.Empty, context);
        if (context.UsesStore || context.UsesCast)
        {
            if (methods == null) file["methods"] = methods = new JsonArray();
            if (context.UsesStore) methods.Add(StoreHelper(name));
            if (context.UsesCast) methods.Add(CastHelper(castName));
        }
    }

    static void CollectNames(JsonNode node, HashSet<string> names)
    {
        if (node is JsonArray array) { foreach (var child in array) CollectNames(child, names); return; }
        if (node is not JsonObject obj) return;
        if (Str(obj["name"]) is string name) names.Add(name);
        if (Str(obj["var"]) is string variable) names.Add(variable);
        foreach (var child in obj.Select(pair => pair.Value)) CollectNames(child, names);
    }

    static void Visit(JsonNode node, BirScope scope, Context context, JsonArray typeParameters = null,
        JsonArray methodParameters = null, bool address = false)
    {
        if (node is JsonArray array)
        {
            var child = scope.Child();
            for (var i = 0; i < array.Count; i++)
            {
                var declaration = array[i] as JsonObject;
                var variable = Str(declaration?["k"]) == "var" ? Str(declaration["name"]) : null;
                var variableType = variable == null ? null : TypeJson.Read(declaration["type"]);
                if (TypeJson.Read(array[i]) is TypeNode type)
                    array[i] = TypeJson.Write(Project(type));
                else Visit(array[i], child, context, typeParameters, methodParameters, address);
                if (variableType != null) child.VarTypes[variable] = variableType;
            }
            return;
        }
        if (node is not JsonObject obj) return;
        if (Str(obj["kind"]) is "class" or "interface" or "struct")
        {
            typeParameters = obj["typeParams"] as JsonArray;
            methodParameters = null;
        }
        else if (obj["params"] is JsonArray) methodParameters = obj["typeParams"] as JsonArray;
        var kind = Str(obj["k"]);
        var castElement = kind is "cast" or "isInstRef" && obj[CastOperandKey] is JsonValue reified
            && reified.TryGetValue<bool>(out var checkedOperand) && !checkedOperand
            && TypeJson.Read(obj["type"]) is TypeNode.Array sourceArray && ContainsVariable(sourceArray.Elem)
                ? ErasedBound(sourceArray.Elem, typeParameters, methodParameters, new HashSet<TypeNode.Tv>()) : null;
        obj.Remove(CastOperandKey);
        var element = TypeJson.Read(obj["elem"]);
        var genericArray = ArrayConstructionLowering.ArrayElementOf(StaticType.Surface(obj["array"], scope));
        var childScope = scope.Extend(obj);
        // The selected declaration, not the value's type, owns address-taking.
        // Both pre-link and linked calls carry their current declaration facts.
        var signature = (obj["memberRef"] as JsonObject)?["parameterTypes"] as JsonArray
            ?? obj["resolvedMemberParams"] as JsonArray
            ?? obj["sig"] as JsonArray
            ?? obj["shapeTypes"] as JsonArray
            ?? obj["argTypes"] as JsonArray;
        // Keep the source declaration shape for downstream Kotlin projection.
        if (kind == null && obj["params"] is JsonArray
            && TypeJson.Read(obj["ret"]) is TypeNode ret && !Project(ret).Equals(ret)
            && obj["retKotlinType"] == null)
            obj["retKotlinType"] = TypeJson.Write(ret).ToJsonString();
        if ((kind == null || kind == "var") && TypeJson.Read(obj["type"]) is TypeNode slot
            && !Project(slot).Equals(slot) && obj["kotlinType"] == null)
            obj["kotlinType"] = TypeJson.Write(slot).ToJsonString();

        foreach (var (key, value) in obj.ToList())
        {
            if (key is "memberRef" or "kotlinType" or "retKotlinType") continue;
            if (ClrBoundNode.IsAny(kind) && key is not ("args" or "recv")) continue;
            if (key == "args" && value is JsonArray arguments)
            {
                for (var i = 0; i < arguments.Count; i++)
                    Visit(arguments[i], childScope, context, typeParameters, methodParameters,
                        signature != null && i < signature.Count && IsManagedReference(TypeJson.Read(signature[i])));
                continue;
            }
            if (TypeJson.Read(value) is TypeNode type) obj[key] = TypeJson.Write(Project(type));
            else Visit(value, childScope, context, typeParameters, methodParameters,
                kind == "byrefOf" && key == "inner" || address && kind == "valueBlock" && key == "result");
        }
        if (castElement != null)
        {
            context.UsesCast = true;
            var value = obj["e"]?.DeepClone();
            obj.Clear();
            obj["k"] = "callStatic";
            obj["owner"] = TypeJson.Fqn(context.Owner);
            obj["method"] = context.Cast;
            obj["sig"] = new JsonArray(TypeJson.Fqn("System.Object"), TypeJson.Fqn("System.Type"), TypeJson.Fqn("System.Boolean"));
            obj["ret"] = TypeJson.Fqn("System.Array");
            obj["args"] = new JsonArray(value, new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(castElement) },
                new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Boolean"), ["value"] = kind == "cast" });
        }
        if (kind == "arrayLen" && ContainsVariable(genericArray))
        {
            var length = Call(obj["array"], "get_Length", "System.Int32", new JsonArray(), new JsonArray());
            obj.Clear();
            foreach (var (key, value) in length) obj[key] = value?.DeepClone();
        }
        if (kind == "arrayGet" && ContainsVariable(element) && !address)
        {
            var read = Call(obj["array"], "GetValue", "System.Object",
                new JsonArray(TypeJson.Fqn("System.Int32")), new JsonArray(obj["index"]?.DeepClone()));
            obj.Clear();
            obj["k"] = "cast";
            obj["type"] = TypeJson.Write(Project(element));
            obj["e"] = read;
        }
        if (kind == "arraySet" && ContainsVariable(element))
        {
            context.UsesStore = true;
            var call = new JsonObject
            {
                ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(context.Owner), ["method"] = context.Store,
                ["sig"] = new JsonArray(TypeJson.Fqn("System.Array"), TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.Object")),
                ["ret"] = TypeJson.Fqn("void"),
                ["args"] = new JsonArray(obj["array"]?.DeepClone(), obj["index"]?.DeepClone(),
                    new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Object"), ["e"] = obj["value"]?.DeepClone() }),
            };
            var expressionUse = obj.Parent is JsonObject;
            obj.Clear();
            if (expressionUse)
                foreach (var (key, value) in call) obj[key] = value?.DeepClone();
            else
            {
                obj["k"] = "exprStmt";
                obj["expr"] = call;
            }
        }
        if (kind == "forArray" && ContainsVariable(element))
        {
            var arrayName = context.Temporary();
            var indexName = context.Temporary();
            var body = new JsonArray(new JsonObject
            {
                ["k"] = "var", ["name"] = obj["var"]?.DeepClone(), ["type"] = TypeJson.Write(Project(element)),
                ["init"] = new JsonObject
                {
                    ["k"] = "cast", ["type"] = TypeJson.Write(Project(element)),
                    ["e"] = Call(Local(arrayName), "GetValue", "System.Object", new JsonArray(TypeJson.Fqn("System.Int32")), new JsonArray(Local(indexName))),
                },
            });
            if (obj["body"] is JsonArray statements)
                foreach (var statement in statements) body.Add(statement?.DeepClone());
            var replacement = new JsonArray(
                new JsonObject { ["k"] = "var", ["name"] = arrayName, ["type"] = TypeJson.Fqn("System.Array"), ["init"] = obj["array"]?.DeepClone() },
                new JsonObject
                {
                    ["k"] = "for", ["var"] = indexName, ["label"] = obj["label"]?.DeepClone(),
                    ["from"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 0 },
                    ["to"] = Call(Local(arrayName), "get_Length", "System.Int32", new JsonArray(), new JsonArray()),
                    ["cmp"] = "<", ["step"] = 1, ["body"] = body,
                });
            obj.Clear();
            obj["k"] = "block";
            obj["body"] = replacement;
        }
    }

    static JsonObject Call(JsonNode receiver, string method, string result, JsonArray types, JsonArray args) => new()
    {
        ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Array"),
        ["method"] = method, ["ret"] = TypeJson.Fqn(result),
        ["argTypes"] = types, ["args"] = args, ["recv"] = receiver?.DeepClone(),
    };

    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static TypeNode ErasedBound(TypeNode type, JsonArray types, JsonArray methods, HashSet<TypeNode.Tv> visiting) => type switch
    {
        TypeNode.Nullable nullable => ErasedBound(nullable.Of, types, methods, visiting),
        TypeNode.Oblivious oblivious => ErasedBound(oblivious.Of, types, methods, visiting),
        TypeNode.Tv variable => VariableBound(variable, types, methods, visiting),
        TypeNode.Array array => new TypeNode.Array(ErasedBound(array.Elem, types, methods, visiting), array.Rank, array.SzArray),
        _ => type,
    };
    static TypeNode VariableBound(TypeNode.Tv variable, JsonArray types, JsonArray methods, HashSet<TypeNode.Tv> visiting)
    {
        var parameters = variable.Scope == "type" ? types : methods;
        if (parameters == null || variable.I < 0 || variable.I >= parameters.Count)
            throw new System.InvalidOperationException("generic array cast has no declaration-owned type parameter frame");
        if (!visiting.Add(variable)) throw new System.InvalidOperationException("cyclic generic array erasure bound");
        var constraint = parameters[variable.I] is JsonObject declaration
            ? (declaration["constraints"] as JsonArray)?.FirstOrDefault() : null;
        var bound = constraint == null ? new TypeNode.Fqn("System.Object") : ErasedBound(TypeJson.Read(constraint), types, methods, visiting);
        visiting.Remove(variable);
        return bound;
    }
    static JsonObject CastHelper(string name)
    {
        JsonObject Null() => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Array"), ["value"] = null };
        JsonObject Return(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
        JsonObject If(JsonNode condition, params JsonNode[] body) => new()
        { ["k"] = "if", ["branches"] = new JsonArray(new JsonObject { ["cond"] = condition, ["body"] = new JsonArray(body) }) };
        JsonObject Not(JsonNode value) => new() { ["k"] = "unaryOp", ["op"] = "!", ["e"] = value };
        JsonObject Bad() => If(Local("throwing"), new JsonObject
        {
            ["k"] = "throw", ["value"] = new JsonObject { ["k"] = "new", ["type"] = TypeJson.Fqn("kotlin.ClassCastException"), ["args"] = new JsonArray(), ["argTypes"] = new JsonArray(), ["memberSignature"] = new JsonArray() },
        });
        var element = TypeCall(new JsonObject { ["k"] = "getType", ["e"] = Local("array") }, "GetElementType", "System.Type");
        var matches = TypeCall(Local("bound"), "IsAssignableFrom", "System.Boolean", element);
        matches["argTypes"] = new JsonArray(TypeJson.Fqn("System.Type"));
        return new JsonObject
        {
            ["name"] = name, ["static"] = true, ["vis"] = "private", ["generated"] = true,
            ["abstract"] = false, ["virtual"] = false, ["override"] = false,
            ["params"] = new JsonArray(
                new JsonObject { ["name"] = "value", ["type"] = TypeJson.Fqn("System.Object") },
                new JsonObject { ["name"] = "bound", ["type"] = TypeJson.Fqn("System.Type") },
                new JsonObject { ["name"] = "throwing", ["type"] = TypeJson.Fqn("System.Boolean") }),
            ["ret"] = TypeJson.Fqn("System.Array"),
            ["body"] = new JsonArray(
                If(new JsonObject { ["k"] = "objEq", ["lhs"] = Local("value"), ["rhs"] = Null() }, Return(Null())),
                new JsonObject { ["k"] = "var", ["name"] = "array", ["type"] = TypeJson.Fqn("System.Array"), ["init"] = new JsonObject { ["k"] = "isInstRef", ["type"] = TypeJson.Fqn("System.Array"), ["e"] = Local("value") } },
                If(new JsonObject { ["k"] = "objEq", ["lhs"] = Local("array"), ["rhs"] = Null() }, Bad(), Return(Null())),
                If(Not(TypeCall(new JsonObject { ["k"] = "getType", ["e"] = Local("array") }, "get_IsSZArray", "System.Boolean")), Bad(), Return(Null())),
                If(Not(matches), Bad(), Return(Null())), Return(Local("array"))),
        };
    }
    static JsonObject TypeCall(JsonNode receiver, string method, string result, params JsonNode[] args) => new()
    {
        ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Type"), ["method"] = method,
        ["recv"] = receiver, ["ret"] = TypeJson.Fqn(result), ["args"] = new JsonArray(args),
        ["argTypes"] = new JsonArray(args.Select(_ => (JsonNode)TypeJson.Fqn("System.Object")).ToArray()),
    };
    static JsonObject FailStore() => new()
    {
        ["k"] = "throw", ["value"] = new JsonObject
        {
            ["k"] = "newClr", ["type"] = TypeJson.Fqn("System.ArrayTypeMismatchException"),
            ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
        },
    };
    static JsonObject StoreHelper(string name)
    {
        JsonObject If(JsonNode condition, JsonArray thenBody, JsonArray elseBody = null)
        {
            var branches = new JsonArray(new JsonObject { ["cond"] = condition, ["body"] = thenBody });
            if (elseBody != null) branches.Add(new JsonObject { ["else"] = true, ["body"] = elseBody });
            return new JsonObject { ["k"] = "if", ["branches"] = branches };
        }
        JsonObject Param(string n, string type) => new() { ["name"] = n, ["type"] = TypeJson.Fqn(type) };
        JsonObject IsNull(JsonNode value) => new()
        {
            ["k"] = "objEq", ["lhs"] = value,
            ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Object"), ["value"] = null },
        };
        var nullableUnderlying = new JsonObject
        {
            ["k"] = "clrStatic", ["type"] = TypeJson.Fqn("System.Nullable"), ["method"] = "GetUnderlyingType",
            ["ret"] = TypeJson.Fqn("System.Type"), ["argTypes"] = new JsonArray(TypeJson.Fqn("System.Type")),
            ["args"] = new JsonArray(Local("element")),
        };
        var actualType = new JsonObject
        {
            ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Object"), ["method"] = "GetType",
            ["ret"] = TypeJson.Fqn("System.Type"), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(), ["recv"] = Local("array"),
        };
        return new JsonObject
        {
            ["name"] = name, ["static"] = true, ["vis"] = "private", ["generated"] = true,
            ["abstract"] = false, ["virtual"] = false, ["override"] = false,
            ["params"] = new JsonArray(Param("array", "System.Array"), Param("index", "System.Int32"), Param("value", "System.Object")),
            ["ret"] = TypeJson.Fqn("void"),
            ["body"] = new JsonArray(
                // Establish null/range checks before inspecting the value's store type.
                new JsonObject { ["k"] = "exprStmt", ["expr"] = Call(Local("array"), "GetValue", "System.Object", new JsonArray(TypeJson.Fqn("System.Int32")), new JsonArray(Local("index"))) },
                new JsonObject { ["k"] = "var", ["name"] = "element", ["type"] = TypeJson.Fqn("System.Type"), ["init"] = TypeCall(actualType, "GetElementType", "System.Type") },
                If(IsNull(Local("value")),
                    new JsonArray(If(TypeCall(Local("element"), "get_IsValueType", "System.Boolean"),
                        new JsonArray(If(IsNull(nullableUnderlying), new JsonArray(FailStore()))))),
                    new JsonArray(If(new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = TypeCall(Local("element"), "IsInstanceOfType", "System.Boolean", Local("value")) },
                        new JsonArray(FailStore())))),
                new JsonObject { ["k"] = "exprStmt", ["expr"] = Call(Local("array"), "SetValue", "void", new JsonArray(TypeJson.Fqn("System.Object"), TypeJson.Fqn("System.Int32")), new JsonArray(Local("value"), Local("index"))) },
                new JsonObject { ["k"] = "return" }),
        };
    }

    static TypeNode Project(TypeNode type) => type switch
    {
        TypeNode.Array array when ContainsVariable(array.Elem) => new TypeNode.Fqn("System.Array"),
        // A named construction has already been chosen by its representation or
        // CLR binding pass. Its invariant arguments are not independent array value
        // slots: changing G<T[]> to G<Array> here would disagree with constructors,
        // member descriptors and native storage. Boundary conversions own that seam.
        TypeNode.Nullable nullable => new TypeNode.Nullable(Project(nullable.Of)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(Project(oblivious.Of)),
        _ => type,
    };

    static bool IsManagedReference(TypeNode type) => type switch
    {
        TypeNode.ByRef => true,
        TypeNode.Mod modifier => IsManagedReference(modifier.Of),
        _ => false,
    };

    internal static void SelfTest()
    {
        var array = new TypeNode.Array(new TypeNode.Tv("method", 0));
        var construction = new TypeNode.Fqn("NativeContainer", new TypeNode[] { array });
        if (!Project(array).Equals(new TypeNode.Fqn("System.Array"))
            || !Project(construction).Equals(construction)
            || !Project(new TypeNode.Nullable(construction)).Equals(new TypeNode.Nullable(construction)))
            throw new System.InvalidOperationException("Generic array value projection changed an exact named construction");
        JsonObject Element() => new() { ["k"] = "arrayGet", ["elem"] = TypeJson.Write(array.Elem),
            ["array"] = Local("values"), ["index"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("int"), ["value"] = 0 } };
        foreach (var signatureKey in new[] { "sig", "shapeTypes", "argTypes", "resolvedMemberParams", "memberRef" })
        {
            var addressed = Element();
            var ordinary = Element();
            var pinned = Element();
            var call = new JsonObject { ["k"] = signatureKey == "argTypes" ? "clrGenericStatic" : "callStatic",
                ["args"] = new JsonArray(addressed, ordinary) };
            var signature = new JsonArray(TypeJson.Write(new TypeNode.ByRef(array.Elem)), TypeJson.Write(array.Elem));
            if (signatureKey == "memberRef") call[signatureKey] = new JsonObject { ["parameterTypes"] = signature };
            else call[signatureKey] = signature;
            var document = new JsonObject { ["fileClass"] = "Consumer", ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "Use", ["typeParams"] = new JsonArray("T"),
                    ["params"] = new JsonArray(new JsonObject { ["name"] = "values", ["type"] = TypeJson.Write(array) }),
                    ["ret"] = TypeJson.Fqn("void"), ["body"] = new JsonArray(
                        new JsonObject { ["k"] = "exprStmt", ["expr"] = call },
                        new JsonObject { ["k"] = "var", ["name"] = "pointer", ["type"] = TypeJson.Write(new TypeNode.ByRef(array.Elem)),
                            ["init"] = new JsonObject { ["k"] = "byrefOf", ["inner"] = pinned } }) }) };
            ApplyAll(new[] { document });
            if (Str(addressed["k"]) != "arrayGet" || TypeJson.Read(addressed["elem"]) != array.Elem
                || Str(pinned["k"]) != "arrayGet" || Str(ordinary["k"]) != "cast"
                || Str(ordinary["e"]?["method"]) != "GetValue")
                throw new System.InvalidOperationException("Generic array address was replaced with a value read or an ordinary read retained an address");
        }
        System.Console.WriteLine("[generic array values] self-test OK (array slot versus invariant construction)");
    }

    static bool ContainsVariable(TypeNode type) => type switch
    {
        TypeNode.Tv => true,
        TypeNode.Array array => ContainsVariable(array.Elem),
        TypeNode.Nullable nullable => ContainsVariable(nullable.Of),
        TypeNode.Oblivious oblivious => ContainsVariable(oblivious.Of),
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsVariable),
        _ => false,
    };

    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
