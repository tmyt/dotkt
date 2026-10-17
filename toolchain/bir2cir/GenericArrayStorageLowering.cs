using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Open Kotlin arrays retain the allocation's runtime component, independently of the caller's T or star projection.
// The physical value slot is System.Array; exact native array signatures remain separate facts.
// All access is materialized here as ordinary CLR calls, not reconstructed by ilemit.
static class GenericArrayStorageLowering
{
    internal static bool NeedsStorage(TypeNode.Array array) =>
        array.SzArray && array.Rank == 1 && ContainsOpenComponent(array.Elem);

    static bool ContainsOpenComponent(TypeNode type) => type switch {
        // A projection's bound is not the allocation's component: Array<out Any?>
        // can hold an Int32[] just as Array<*> can.
        TypeNode.Tv or TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Nullable n => ContainsOpenComponent(n.Of),
        TypeNode.Oblivious o => ContainsOpenComponent(o.Of),
        TypeNode.Array a => ContainsOpenComponent(a.Elem),
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsOpenComponent),
        _ => false,
    };

    static bool ContainsVariable(TypeNode type) => type switch {
        TypeNode.Tv => true,
        TypeNode.Nullable n => ContainsVariable(n.Of),
        TypeNode.Oblivious o => ContainsVariable(o.Of),
        TypeNode.Projection p => ContainsVariable(p.Of),
        TypeNode.Array a => ContainsVariable(a.Elem),
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsVariable),
        _ => false,
    };

    static string Text(JsonNode node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static JsonObject Type(string name) => TypeJson.Fqn(name).AsObject();
    static JsonObject Local(string name, TypeNode type) => new() {
        ["k"] = "local", ["name"] = name, ["sty"] = TypeJson.Write(type),
    };
    static JsonObject Int(int value) => new() {
        ["k"] = "const", ["type"] = Type("System.Int32"), ["value"] = value,
    };

    static JsonObject Call(ReferenceMetadataIndex refs, string name, JsonNode receiver,
        string result, string[] parameters, params JsonNode[] arguments) =>
        RuntimeCall(refs, "System.Array", name, receiver, result, parameters, arguments);

    static JsonObject RuntimeCall(ReferenceMetadataIndex refs, string owner, string name, JsonNode receiver,
        string result, string[] parameters, params JsonNode[] arguments)
    {
        return new JsonObject {
            ["k"] = receiver == null ? "clrStatic" : "clrInstance", ["type"] = Type(owner), ["method"] = name,
            ["recv"] = receiver?.DeepClone(), ["args"] = new JsonArray(arguments.Select(a => a.DeepClone()).ToArray()),
            ["argTypes"] = new JsonArray(parameters.Select(Type).ToArray()),
            ["ret"] = Type(result), ["sty"] = Type(result), ["dispatch"] = receiver == null ? "call" : "callvirt",
            ["memberRef"] = ClrMemberResolution.ArrayStorageMethodRef(refs, name, parameters, owner, receiver == null),
        };
    }

    static JsonObject Read(ReferenceMetadataIndex refs, JsonNode receiver, JsonNode index, TypeNode result) => new() {
        ["k"] = "cast", ["type"] = TypeJson.Write(result), ["sty"] = TypeJson.Write(result),
        ["e"] = Call(refs, "GetValue", receiver, "object", new[] { "System.Int32" }, index),
    };

    internal static bool LowerOperation(JsonObject node, ReferenceMetadataIndex refs, TypeNode receiverType = null)
    {
        if (node["array"] is not JsonObject receiver) return false;
        receiverType ??= TypeJson.Read(receiver["sty"])
            ?? TypeJson.Read(receiver["type"]) ?? TypeJson.Read(receiver["ret"])
            ?? TypeJson.Read(receiver["memberType"]);
        if (receiverType is TypeNode.Nullable n) receiverType = n.Of;
        if (receiverType is not TypeNode.Fqn { Name: "System.Array" }) return false;
        JsonObject replacement;
        switch (Text(node["k"]))
        {
            case "arrayGet":
                replacement = Read(refs, receiver, node["index"],
                    TypeJson.Read(node["sty"]) ?? TypeJson.Read(node["elem"]));
                break;
            case "arraySet":
                replacement = Store(node, receiver, refs);
                break;
            case "arrayLen":
                replacement = Call(refs, "get_Length", receiver, "System.Int32", Array.Empty<string>());
                break;
            case "forArray":
                replacement = Loop(node, receiver, refs);
                break;
            default: return false;
        }
        node.Clear();
        foreach (var pair in replacement) node[pair.Key] = pair.Value?.DeepClone();
        return true;
    }

    static Func<string, string> FreshNames(JsonObject source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode node)
        {
            if (node is JsonObject obj) {
                if (Text(obj["name"]) is string name) names.Add(name);
                if (Text(obj["var"]) is string variable) names.Add(variable);
                foreach (var child in obj) Collect(child.Value);
            } else if (node is JsonArray array) foreach (var child in array) Collect(child);
        }
        // Include surrounding lexical declarations, not just the loop body.
        JsonNode scope = source;
        while (scope.Parent != null) scope = scope.Parent;
        Collect(scope);
        return stem => {
            var index = 0;
            while (!names.Add(stem + index)) index++;
            return stem + index;
        };
    }

    static JsonObject Var(string name, TypeNode type, JsonNode initial) => new() {
        ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(type), ["init"] = initial,
    };

    internal static void LowerTypeOperation(JsonObject operation, TypeNode target,
        JsonArray typeParameters, JsonArray methodParameters, ReferenceMetadataIndex refs,
        Func<TypeNode, TypeNode> projectBound, Func<TypeNode, bool> isInterface)
    {
        var nullable = target is TypeNode.Nullable;
        while (target is TypeNode.Nullable or TypeNode.Oblivious)
            target = target is TypeNode.Nullable n ? n.Of : ((TypeNode.Oblivious)target).Of;
        if (target is not TypeNode.Array array || !NeedsStorage(array)) return;
        var kind = Text(operation["k"]);
        var test = kind == "isInst";
        if (kind is not ("cast" or "isInstRef" or "isInst")
            || KotlinTypeWitness.NeedsWitness(operation) && ContainsVariable(array)) return;
        var safe = test || kind == "isInstRef";
        var fresh = FreshNames(operation);
        var arrayName = fresh("$arrayCast$");
        var typeName = fresh("$arrayCastType$");
        var inputName = test && nullable ? fresh("$arrayInput$") : null;
        var arrayType = new TypeNode.Fqn("System.Array");
        var typeType = new TypeNode.Fqn("System.Type");
        JsonObject Bool(bool value) => new() { ["k"] = "const", ["type"] = Type("System.Boolean"), ["value"] = value };
        JsonObject Null() => new() { ["k"] = "const", ["type"] = Type("System.Array"), ["value"] = null };
        JsonObject Not(JsonNode value) => new() { ["k"] = "unaryOp", ["op"] = "!", ["e"] = value };
        JsonObject If(JsonNode condition, params JsonNode[] body) => new() {
            ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                ["cond"] = condition, ["body"] = new JsonArray(body),
            }),
        };
        JsonObject Declaration(string scope)
        {
            for (JsonNode parent = operation.Parent; parent != null; parent = parent.Parent)
                if (parent is JsonObject declaration && (scope == "method"
                    ? declaration["k"] == null && declaration["params"] is JsonArray && declaration["body"] is JsonArray
                    : Text(declaration["kind"]) is "class" or "interface" or "enum" or "struct" or "delegate"))
                    return declaration;
            return null;
        }
        NullableRepresentationFrame Frame(string scope) => Declaration(scope) is { } declaration
            ? scope == "method" ? NullableRepresentationTypes.DeclarationMethodFrame(declaration)
                : KotlinSupertypesRecord.ReadNullableFrame(declaration) : null;
        TypeNode PhysicalBound(TypeNode source) => source switch {
            TypeNode.Tv variable when Frame(variable.Scope) is { } frame =>
                frame.Variable(variable, NullableRepresentationFrame.Role.Ordinary),
            TypeNode.Nullable n => new TypeNode.Nullable(PhysicalBound(n.Of)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(PhysicalBound(o.Of)),
            TypeNode.Array a => new TypeNode.Array(PhysicalBound(a.Elem), a.Rank, a.SzArray),
            TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(PhysicalBound).ToArray()),
            _ => source,
        };
        TypeNode Bound(TypeNode.Tv variable)
        {
            var parameters = variable.Scope == "method" ? methodParameters : typeParameters;
            var declaration = Declaration(variable.Scope);
            var frame = Frame(variable.Scope);
            var sourceIndex = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
            var index = frame == null ? variable.I : frame.SourcePosition(frame.PhysicalSlot(variable.I).SourceIndex);
            if (parameters == null || index < 0 || index >= parameters.Count)
                throw new InvalidOperationException("Array cast has no declaration-owned generic bound frame");
            var parameter = parameters[index] as JsonObject;
            var sourceKey = variable.Scope == "method" ? NullableGenericErasure.MethodTypeParameterBoundsPre
                : KotlinSupertypesRecord.PreKey;
            var sourceBounds = declaration != null && Text(declaration[sourceKey]) is string encoded
                ? JsonNode.Parse(encoded)?["bounds"]?[sourceIndex.ToString()] as JsonArray : null;
            if (frame?.RequiresMetadata == true && sourceBounds == null)
                throw new InvalidOperationException("Array cast has no recorded Kotlin generic bounds");
            var bounds = sourceBounds ?? (parameter?[FBoundStarProjectionErasure.ErasedInnerConstraintKey]
                ?? parameter?["constraints"]) as JsonArray;
            var types = bounds?.Select(TypeJson.Read).Where(t => t != null).ToArray() ?? Array.Empty<TypeNode>();
            if (sourceBounds != null) types = types.Select(PhysicalBound).ToArray();
            // Kotlin erasure keeps the representative class bound, or the first
            // interface bound. Extra intersection bounds are not cast checks.
            return types.FirstOrDefault(t => !isInterface(t)) ?? types.FirstOrDefault() ?? new TypeNode.Fqn("System.Object");
        }
        JsonNode Check(TypeNode expected, JsonNode actualType, HashSet<TypeNode.Tv> variables)
        {
            while (expected is TypeNode.Nullable or TypeNode.Oblivious or TypeNode.Projection)
                expected = expected switch { TypeNode.Nullable n => n.Of, TypeNode.Oblivious o => o.Of,
                    // An in-projection has Any? as its readable bound, not its write bound.
                    TypeNode.Projection { Variance: "in" } => new TypeNode.Star(),
                    TypeNode.Projection p => p.Of, _ => expected };
            if (expected is TypeNode.Star) return Bool(true);
            if (expected is TypeNode.Tv variable)
            {
                if (!variables.Add(variable)) throw new InvalidOperationException("Cyclic array-cast erasure bound");
                return Check(Bound(variable), actualType, variables);
            }
            if (expected is TypeNode.Array nested)
            {
                var nativeCheck = new JsonObject {
                    ["k"] = "cond", ["type"] = Type("System.Boolean"),
                    ["cond"] = RuntimeCall(refs, "System.Type", "get_IsSZArray", actualType, "System.Boolean", Array.Empty<string>()),
                    ["then"] = Check(nested.Elem, RuntimeCall(refs, "System.Type", "GetElementType", actualType,
                        "System.Type", Array.Empty<string>()), new HashSet<TypeNode.Tv>(variables)), ["else"] = Bool(false),
                };
                // An array whose element is itself open allocates System.Array[];
                // that exact component is already the nested array's physical storage.
                return NeedsStorage(nested)
                    ? ErasedComponentCheck(new TypeNode.Fqn("System.Array"), actualType, nativeCheck)
                    : nativeCheck;
            }
            if (expected is TypeNode.Fqn { Name: "kotlin.Any" or "System.Object" or "object", Args: null }) return Bool(true);
            if (KotlinRuntimeTypeWitness.ValueRepresentations?.ComponentPredicate(expected, actualType) is { } semanticCheck)
                return ErasedComponentCheck(projectBound(expected), actualType, semanticCheck);
            if (expected is TypeNode.Fqn { Args: { Length: > 0 } } generic
                && ForeignStarProjectionBinding.TypeClassifierCheck(generic, actualType, refs) is { } foreignCheck)
                return AliasVarianceRepresentation.RequiresErasure(generic, refs)
                    ? ErasedComponentCheck(projectBound(generic), actualType, foreignCheck)
                    : foreignCheck;
            var classifier = projectBound(expected);
            return RuntimeCall(refs, "System.Type", "IsAssignableFrom", new JsonObject {
                ["k"] = "classRef", ["type"] = TypeJson.Write(classifier),
            }, "System.Boolean", new[] { "System.Type" }, actualType);
        }
        var invalid = Check(array, Local(typeName, typeType), new HashSet<TypeNode.Tv>());
        var exceptionOwner = refs.ResolveRefType("System.InvalidCastException");
        var exceptionConstructor = exceptionOwner.GetConstructor(System.Type.EmptyTypes);
        JsonObject Failure() => safe
            ? new JsonObject { ["k"] = "setLocal", ["name"] = arrayName, ["value"] = Null() }
            : new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                ["k"] = "newClr", ["type"] = Type("System.InvalidCastException"), ["args"] = new JsonArray(),
                ["memberRef"] = ClrMemberResolution.MemberRefJson(exceptionConstructor, "ctor", exceptionOwner, null),
            } };
        var value = Local(arrayName, arrayType);
        var isNull = new JsonObject { ["k"] = "objEq", ["lhs"] = value.DeepClone(), ["rhs"] = Null() };
        var statements = new JsonArray();
        if (inputName != null)
            statements.Add(Var(inputName, new TypeNode.Fqn("System.Object"), operation["e"].DeepClone()));
        statements.Add(Var(arrayName, arrayType, new JsonObject {
            ["k"] = safe ? "isInstRef" : "cast", ["type"] = Type("System.Array"),
            ["e"] = inputName == null ? operation["e"].DeepClone()
                : Local(inputName, new TypeNode.Fqn("System.Object")),
        }));
        if (!safe && !nullable) statements.Add(If(isNull.DeepClone(), Failure()));
        statements.Add(If(Not(isNull),
            Var(typeName, typeType, RuntimeCall(refs, "System.Object", "GetType", value, "System.Type", Array.Empty<string>())),
            If(Not(invalid), Failure())));
        operation.Clear();
        operation["k"] = "valueBlock";
        operation["type"] = Type(test ? "System.Boolean" : "System.Array");
        operation["stmts"] = statements;
        operation["result"] = !test ? value : inputName == null ? Not(isNull.DeepClone()) : new JsonObject {
            ["k"] = "cond", ["type"] = Type("System.Boolean"),
            ["cond"] = new JsonObject { ["k"] = "binOp", ["op"] = "==",
                ["lhs"] = Local(inputName, new TypeNode.Fqn("System.Object")), ["rhs"] = Null() },
            ["then"] = Bool(true), ["else"] = Not(isNull.DeepClone()),
        };
    }

    // An allocation of an opaque alias has its ordinary physical component. Other allocations must still
    // provide the source classifier; assignability to Object would incorrectly accept unrelated String[].
    static JsonObject ErasedComponentCheck(TypeNode physical, JsonNode actualType, JsonNode classifierCheck) => new() {
        ["k"] = "cond", ["type"] = Type("System.Boolean"),
        ["cond"] = new JsonObject {
            ["k"] = "objEq", ["lhs"] = actualType.DeepClone(),
            ["rhs"] = new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(physical) },
        },
        ["then"] = new JsonObject { ["k"] = "const", ["type"] = Type("System.Boolean"), ["value"] = true },
        ["else"] = classifierCheck.DeepClone(),
    };

    internal static void SelfTest()
    {
        var openComponents = new TypeNode[] {
            new TypeNode.Star(), new TypeNode.Tv("method", 0),
            new TypeNode.Projection("out", new TypeNode.Fqn("kotlin.Any")),
            new TypeNode.Projection("in", new TypeNode.Fqn("kotlin.Int")),
            new TypeNode.Array(new TypeNode.Star()),
        };
        if (openComponents.Any(component => !NeedsStorage(new TypeNode.Array(component)))
            || NeedsStorage(new TypeNode.Array(new TypeNode.Fqn("kotlin.Int")))
            || NeedsStorage(new TypeNode.Array(new TypeNode.Star(), Rank: 2, SzArray: false)))
            throw new InvalidOperationException("Open array storage lost its component or native-shape distinction");
        var actual = Local("component", new TypeNode.Fqn("System.Type"));
        var classifier = new JsonObject { ["k"] = "const", ["type"] = Type("System.Boolean"), ["value"] = false };
        var check = ErasedComponentCheck(new TypeNode.Fqn("System.Object"), actual, classifier);
        if (Text(check["cond"]?["k"]) != "objEq"
            || TypeJson.OwnerName(check["cond"]?["rhs"]?["type"]) != "System.Object"
            || !JsonNode.DeepEquals(check["cond"]?["lhs"], actual)
            || !JsonNode.DeepEquals(check["else"], classifier))
            throw new InvalidOperationException("Erased array component lost exact storage or classifier checks");
        Console.WriteLine("[array component classifiers] self-test OK (exact opaque storage, retained native classifier)");
    }

    static JsonObject Store(JsonObject store, JsonNode receiver, ReferenceMetadataIndex refs)
    {
        var fresh = FreshNames(store);
        var arrayName = fresh("$arrayStorage$");
        var indexName = fresh("$arrayIndex$");
        var valueName = fresh("$arrayValue$");
        var componentName = fresh("$arrayComponent$");
        var arrayType = new TypeNode.Fqn("System.Array");
        var intType = new TypeNode.Fqn("System.Int32");
        var objectType = new TypeNode.Fqn("System.Object");
        var typeType = new TypeNode.Fqn("System.Type");
        JsonObject Null() => new() { ["k"] = "const", ["type"] = Type("System.Object"), ["value"] = null };
        JsonObject EqualNull(JsonNode value) => new() {
            ["k"] = "objEq", ["lhs"] = value, ["rhs"] = Null(),
        };
        JsonObject Not(JsonNode value) => new() { ["k"] = "unaryOp", ["op"] = "!", ["e"] = value };
        var exceptionOwner = refs.ResolveRefType("System.ArrayTypeMismatchException");
        var exceptionConstructor = exceptionOwner.GetConstructor(System.Type.EmptyTypes);
        JsonObject Fail() => new() { ["k"] = "throw", ["value"] = new JsonObject {
            ["k"] = "newClr", ["type"] = Type("System.ArrayTypeMismatchException"), ["args"] = new JsonArray(),
            ["memberRef"] = ClrMemberResolution.MemberRefJson(exceptionConstructor, "ctor", exceptionOwner, null),
        } };
        JsonObject If(JsonNode condition, params JsonNode[] body) => new() {
            ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                ["cond"] = condition, ["body"] = new JsonArray(body),
            }),
        };
        var component = Local(componentName, typeType);
        var value = Local(valueName, objectType);
        var typeCheck = If(Not(EqualNull(value.DeepClone())),
            If(Not(RuntimeCall(refs, "System.Type", "IsInstanceOfType", component, "System.Boolean",
                new[] { "System.Object" }, value)), Fail()));
        // SetValue performs numeric widening and maps null to a default value for
        // non-nullable native value arrays. Neither is a Kotlin array store.
        var nullCheck = If(EqualNull(value.DeepClone()),
            If(RuntimeCall(refs, "System.Type", "get_IsValueType", component, "System.Boolean", Array.Empty<string>()),
                If(EqualNull(RuntimeCall(refs, "System.Nullable", "GetUnderlyingType", null, "System.Type",
                    new[] { "System.Type" }, component)), Fail())));
        return new JsonObject {
            ["k"] = "valueBlock", ["type"] = Type("void"),
            // Kotlin evaluates receiver, index, then value. CLR SetValue takes
            // value before index, so pin all three exactly once in source order.
            ["stmts"] = new JsonArray(
                Var(arrayName, arrayType, receiver.DeepClone()),
                Var(indexName, intType, store["index"].DeepClone()),
                Var(valueName, objectType, store["value"].DeepClone()),
                Var(componentName, typeType, RuntimeCall(refs, "System.Type", "GetElementType",
                    RuntimeCall(refs, "System.Object", "GetType", Local(arrayName, arrayType), "System.Type", Array.Empty<string>()),
                    "System.Type", Array.Empty<string>())), typeCheck, nullCheck),
            ["result"] = Call(refs, "SetValue", Local(arrayName, arrayType), "void",
                new[] { "System.Object", "System.Int32" }, value, Local(indexName, intType)),
        };
    }

    static JsonObject Loop(JsonObject loop, JsonNode receiver, ReferenceMetadataIndex refs)
    {
        var Fresh = FreshNames(loop);
        var arrayName = Fresh("$arrayStorage$");
        var indexName = Fresh("$arrayIndex$");
        var lengthName = Fresh("$arrayLength$");
        var arrayType = new TypeNode.Fqn("System.Array");
        var intType = new TypeNode.Fqn("System.Int32");
        var body = new JsonArray(Var(Text(loop["var"]), TypeJson.Read(loop["elem"]),
            Read(refs, Local(arrayName, arrayType), Local(indexName, intType), TypeJson.Read(loop["elem"]))));
        foreach (var statement in loop["body"] as JsonArray ?? new JsonArray()) body.Add(statement?.DeepClone());
        // Counted loops already own continue/increment and suspend flattening.
        // Keep that control-flow contract rather than introducing a structured
        // while whose suspending body could be hoisted out of the loop.
        return new JsonObject {
            ["k"] = "block", ["body"] = new JsonArray(
                Var(arrayName, arrayType, receiver.DeepClone()),
                Var(lengthName, intType, Call(refs, "get_Length", Local(arrayName, arrayType),
                    "System.Int32", Array.Empty<string>())),
                new JsonObject { ["k"] = "for", ["label"] = loop["label"]?.DeepClone(),
                    ["var"] = indexName, ["from"] = Int(0), ["to"] = Local(lengthName, intType),
                    ["cmp"] = "<", ["step"] = 1, ["body"] = body }),
        };
    }
}
