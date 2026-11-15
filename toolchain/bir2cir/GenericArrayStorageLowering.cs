using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Open Kotlin arrays retain the allocation's runtime component, independently of the caller's T or star projection.
// The physical value slot is System.Array; exact native array signatures remain separate facts.
// All access is materialized here as ordinary CLR calls, not reconstructed by ilemit.
static partial class GenericArrayStorageLowering
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
        var plan = ClrMemberResolution.PlanArrayStorageCall(refs, name, parameters, owner, receiver == null);
        return new JsonObject {
            ["k"] = receiver == null ? "clrStatic" : "clrInstance", ["type"] = Type(owner), ["method"] = name,
            ["recv"] = receiver?.DeepClone(), ["args"] = new JsonArray(arguments.Select(a => a.DeepClone()).ToArray()),
            ["argTypes"] = new JsonArray(parameters.Select(Type).ToArray()),
            ["ret"] = Type(result), ["sty"] = Type(result), ["dispatch"] = plan.Dispatch,
            ["memberRef"] = plan.Reference.Write(),
        };
    }

    static JsonObject Read(ReferenceMetadataIndex refs, JsonNode receiver, JsonNode index, TypeNode result) => new() {
        ["k"] = "cast", ["type"] = TypeJson.Write(result), ["sty"] = TypeJson.Write(result),
        ["e"] = Call(refs, "GetValue", receiver, "object", new[] { "System.Int32" }, index),
    };

    internal static bool LowerOperation(JsonObject node, ReferenceMetadataIndex refs, FreshNames names,
        TypeNode receiverType = null)
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
                replacement = Store(node, receiver, refs, names);
                break;
            case "arrayLen":
                replacement = Call(refs, "get_Length", receiver, "System.Int32", Array.Empty<string>());
                break;
            case "forArray":
                replacement = Loop(node, receiver, refs, names);
                break;
            default: return false;
        }
        node.Clear();
        foreach (var pair in replacement) node[pair.Key] = pair.Value?.DeepClone();
        return true;
    }

    // One explicit file/rewrite lifetime, never a cache keyed by mutable IR roots. Capture the file after
    // declaration synthesis; seed on the first allocation, including declarations encountered later in the walk.
    // Array-free files do not pay for an identifier walk. Release the file reference immediately after seeding.
    // Existing user declarations are present at seeding. This allocator is the only writer of the array
    // temporary stems; other lowering-generated locals use disjoint stems. A newly introduced subtree whose
    // names can overlap these stems must be reserved before rewriting/attaching it. Detached rewrites use
    // their destination's allocator. Reservations are monotonic, even when a rewrite removes a name.
    internal sealed class FreshNames
    {
        readonly HashSet<string> names = new(StringComparer.Ordinal);
        JsonNode seed;

        internal FreshNames(JsonNode file) => seed = file;

        internal void Reserve(JsonNode node)
        {
            if (node is JsonObject obj) {
                if (Text(obj["name"]) is string name) names.Add(name);
                if (Text(obj["var"]) is string variable) names.Add(variable);
                foreach (var child in obj) Reserve(child.Value);
            } else if (node is JsonArray array) foreach (var child in array) Reserve(child);
        }

        internal string Next(string stem)
        {
            if (seed != null)
            {
                Reserve(seed);
                seed = null;
            }
            var index = 0;
            while (!names.Add(stem + index)) index++;
            return stem + index;
        }
    }

    static JsonObject Var(string name, TypeNode type, JsonNode initial) => new() {
        ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(type), ["init"] = initial,
    };


    internal static void SelfTest()
    {
        FreshNamesSelfTest();
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
        // Exercise the actual source renderer, not a test-only DOM helper.
        // Exact opaque storage succeeds directly; all other allocations retain
        // the selected source classifier over their actual runtime component.
        var component = new TypeNode.Fqn("OpaqueComponent", new TypeNode[] { new TypeNode.Star() });
        var classifier = new BirConstant { Type = new TypeNode.Fqn("System.Boolean"), Value = new BirLiteral.BooleanLiteral(false) };
        var operation = new BirCast { Type = new TypeNode.Array(component),
            E = new BirLocal { Name = "array", Sty = new TypeNode.Fqn("System.Object") } };
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var check = LowerTypeOperation(operation, operation.Type.Value, null, null, refs,
            _ => new TypeNode.Fqn("System.Object"), _ => false,
            new TypedGenericArrayStorageLowering.FreshNames(operation), (expected, _) => expected == component ? classifier : null);
        var exact = false;
        check.Visit(node => {
            if (node is BirConditional { Cond.Value: BirObjectEquals {
                Lhs.Value: BirNativeInstance { Method.Value: "GetElementType", MemberRef.HasValue: true },
                Rhs.Value: BirClassReference { Type.Value: TypeNode.Fqn { Name: "System.Object" } } },
                Then.Value: BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: true } } } selected
                && ReferenceEquals(selected.Else.Value, classifier)) exact = true;
        });
        if (!exact)
            throw new InvalidOperationException("Erased array component lost exact storage or classifier checks");
        Console.WriteLine("[array component classifiers] self-test OK (exact opaque storage, retained native classifier)");
    }

    static JsonObject Store(JsonObject store, JsonNode receiver, ReferenceMetadataIndex refs, FreshNames names)
    {
        var arrayName = names.Next("$arrayStorage$");
        var indexName = names.Next("$arrayIndex$");
        var valueName = names.Next("$arrayValue$");
        var componentName = names.Next("$arrayComponent$");
        var arrayType = new TypeNode.Fqn("System.Array");
        var intType = new TypeNode.Fqn("System.Int32");
        var objectType = new TypeNode.Fqn("System.Object");
        var typeType = new TypeNode.Fqn("System.Type");
        JsonObject Null() => new() { ["k"] = "const", ["type"] = Type("System.Object"), ["value"] = null };
        JsonObject EqualNull(JsonNode value) => new() {
            ["k"] = "binOp", ["op"] = "===", ["lhs"] = value, ["rhs"] = Null(),
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

    static JsonObject Loop(JsonObject loop, JsonNode receiver, ReferenceMetadataIndex refs, FreshNames names)
    {
        var arrayName = names.Next("$arrayStorage$");
        var indexName = names.Next("$arrayIndex$");
        var lengthName = names.Next("$arrayLength$");
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
