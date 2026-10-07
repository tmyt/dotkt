using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// The documented homogeneous primitive/enum identity deviation belongs to an
// operand's declaration frame, not to the object storage introduced by erasure.
// Preserve that frame before erasure; realize it only after physical types settle.
static class HomogeneousIdentityComparisonLowering
{
    internal const string SourceTypeKey = "_homogeneousIdentityType";
    internal const string ReferenceKey = "_referenceIdentity";

    internal static void Capture(JsonObject comparison, JsonNode left, JsonNode right, BirScope scope)
    {
        var leftType = StaticType.Surface(left, scope);
        var rightType = StaticType.Surface(right, scope);
        if (leftType is TypeNode.Tv variable && variable.Equals(rightType))
            comparison[SourceTypeKey] = TypeJson.Write(variable);
        else if (GenericOperand(leftType) || GenericOperand(rightType))
            // This decision belongs to the donor declaration. After an inline
            // substitution, two distinct slots can both look like Int (or the
            // same struct), which does not turn their comparison into value equality.
            comparison[ReferenceKey] = true;
    }

    static bool GenericOperand(TypeNode type) => type switch {
        TypeNode.Tv => true,
        TypeNode.Nullable nullable => GenericOperand(nullable.Of),
        TypeNode.Oblivious oblivious => GenericOperand(oblivious.Of),
        TypeNode.Mod modifier => GenericOperand(modifier.Of),
        _ => false,
    };

    internal static void Apply(JsonNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(root, names);
        var next = 0;
        Rewrite(root, names, ref next);
    }

    static void Rewrite(JsonNode node, HashSet<string> names, ref int next)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) Rewrite(child, names, ref next);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToArray()) Rewrite(child, names, ref next);
        if (obj.ContainsKey(ReferenceKey))
        {
            if (Text(obj["k"]) != "binOp" || Text(obj["op"]) != "==="
                || obj[ReferenceKey]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Reference identity fact no longer belongs to its source comparison");
            obj.Remove(ReferenceKey);
            foreach (var operand in new[] { "lhs", "rhs" })
                obj[operand] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Object"),
                    ["e"] = obj[operand]!.DeepClone() };
            return;
        }
        if (!obj.ContainsKey(SourceTypeKey)) return;
        var type = TypeJson.Read(obj[SourceTypeKey])
            ?? throw new InvalidOperationException("Homogeneous identity lost its declaration frame");
        obj.Remove(SourceTypeKey);
        if (Text(obj["k"]) != "binOp" || Text(obj["op"]) != "===")
            throw new InvalidOperationException("Homogeneous identity fact no longer belongs to an identity operation");
        static string Temp(HashSet<string> allocated, ref int counter)
        {
            string name;
            do name = "dotkt$identity$value$" + counter++; while (!allocated.Add(name));
            return name;
        }
        var left = Temp(names, ref next);
        var right = Temp(names, ref next);
        var comparisonType = Temp(names, ref next);
        JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
        JsonObject Boolean(bool value) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Boolean"), ["value"] = value };
        JsonObject Conditional(JsonNode condition, JsonNode yes, JsonNode no) => new() {
            ["k"] = "cond", ["cond"] = condition, ["then"] = yes, ["else"] = no,
        };
        JsonObject TypeProperty(string name) => new() {
            ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn("System.Type"), ["name"] = name,
            ["static"] = false,
            ["recv"] = new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(type) },
            ["ret"] = TypeJson.Fqn("System.Boolean"),
        };
        JsonObject IsInstance(string name) => new() {
            ["k"] = "isInst", ["type"] = TypeJson.Write(type), ["e"] = Local(name),
        };
        JsonObject Unbox(string name, string scalar) => new() {
            ["k"] = "cast", ["type"] = TypeJson.Fqn(scalar), ["e"] = Local(name),
        };
        var sourceLeft = obj["lhs"]!.DeepClone();
        var sourceRight = obj["rhs"]!.DeepClone();
        var referenceIdentity = new JsonObject {
            ["k"] = "binOp", ["op"] = "==", ["lhs"] = Local(left), ["rhs"] = Local(right),
        };
        // Never emit ceq over an unconstrained TV: an unbox.any TV followed by
        // ceq can be rejected by the JIT for a struct even in an untaken branch.
        // Every value branch instead states one exact CLI scalar stack type.
        // Boxed enum values can be unboxed as their exact underlying CLI scalar.
        var compatible = Conditional(IsInstance(left), IsInstance(right), Boolean(false));
        var underlying = new JsonObject {
            ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Type"), ["method"] = "GetEnumUnderlyingType",
            ["recv"] = new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(type) },
            ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(), ["ret"] = TypeJson.Fqn("System.Type"),
        };
        var exactType = Conditional(TypeProperty("IsEnum"), underlying,
            new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(type) });
        JsonNode scalarIdentity = referenceIdentity.DeepClone();
        foreach (var scalar in new[] { "System.Boolean", "System.Char", "System.SByte", "System.Byte",
            "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
            "System.Single", "System.Double", "System.IntPtr", "System.UIntPtr" }.Reverse())
            scalarIdentity = Conditional(new JsonObject {
                ["k"] = "binOp", ["op"] = "==", ["lhs"] = Local(comparisonType),
                ["rhs"] = new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Fqn(scalar) },
            }, new JsonObject {
                ["k"] = "binOp", ["op"] = "==", ["lhs"] = Unbox(left, scalar), ["rhs"] = Unbox(right, scalar),
            }, scalarIdentity);
        obj.Clear();
        obj["k"] = "valueBlock";
        obj["stmts"] = new JsonArray(
            new JsonObject { ["k"] = "var", ["name"] = left, ["type"] = TypeJson.Fqn("System.Object"), ["init"] = sourceLeft },
            new JsonObject { ["k"] = "var", ["name"] = right, ["type"] = TypeJson.Fqn("System.Object"), ["init"] = sourceRight },
            new JsonObject { ["k"] = "var", ["name"] = comparisonType, ["type"] = TypeJson.Fqn("System.Type"), ["init"] = exactType });
        obj["result"] = Conditional(compatible, scalarIdentity, referenceIdentity);
    }

    static void CollectNames(JsonNode node, HashSet<string> names)
    {
        if (node is JsonObject obj)
        {
            if (Text(obj["name"]) is string name) names.Add(name);
            foreach (var child in obj.Select(pair => pair.Value)) CollectNames(child, names);
        }
        else if (node is JsonArray array)
            foreach (var child in array) CollectNames(child, names);
    }

    internal static void SelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        foreach (var right in new TypeNode[] { variable, new TypeNode.Tv("method", 1),
            new TypeNode.Nullable(variable), new TypeNode.Fqn("kotlin.Any") })
        {
            var document = JsonNode.Parse("""
                {"fileClass":"Identity","methods":[{"name":"Use","typeParams":["T","U"],
                 "params":[],"ret":{"t":"fqn","name":"kotlin.Boolean"},"body":[]} ]}
                """)!.AsObject();
            JsonObject Operand(string name, TypeNode type) => new() {
                ["k"] = "local", ["name"] = name, ["sty"] = TypeJson.Write(type),
            };
            var operation = new JsonObject { ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.internal.ir"),
                ["method"] = "EQEQEQ", ["args"] = new JsonArray(Operand("left", variable), Operand("right", right)) };
            var statement = new JsonObject { ["k"] = "return", ["value"] = operation };
            document["methods"][0]["body"].AsArray().Add(new JsonObject {
                ["k"] = "var", ["name"] = "dotkt$identity$value$0", ["type"] = TypeJson.Fqn("kotlin.Int") });
            document["methods"][0]["body"].AsArray().Add(statement);
            PrimitiveOperatorLowering.Apply(document);
            var comparison = statement["value"].AsObject();
            if (comparison.ContainsKey(SourceTypeKey) != variable.Equals(right))
                throw new InvalidOperationException("Identity capture confused homogeneous, nullable or distinct source slots");
            if (comparison.ContainsKey(ReferenceKey) == variable.Equals(right))
                throw new InvalidOperationException("Identity capture lost the donor's reference comparison rule");
            for (var iteration = 0; iteration < 2; iteration++)
                NullableGenericErasure.Apply(document, _ => false);
            if (variable.Equals(right) && TypeJson.Read(comparison[SourceTypeKey]) != variable)
                throw new InvalidOperationException("Identity declaration frame was erased with its value storage");
            Apply(document);
            var once = document.DeepClone();
            Apply(document);
            if (!JsonNode.DeepEquals(document, once) || document.ToJsonString().Contains(SourceTypeKey, StringComparison.Ordinal))
                throw new InvalidOperationException("Identity frame survived materialization or materialization was not idempotent");
            if (document.ToJsonString().Contains(ReferenceKey, StringComparison.Ordinal)
                || !variable.Equals(right) && (Text(comparison["lhs"]["k"]) != "cast"
                    || TypeJson.Read(comparison["lhs"]["type"]) != new TypeNode.Fqn("System.Object")
                    || Text(comparison["rhs"]["k"]) != "cast"))
                throw new InvalidOperationException("Reference identity was not realized as explicit boxing");
            if (variable.Equals(right) && (Text(comparison["k"]) != "valueBlock"
                || comparison["stmts"].AsArray().Count != 3
                || Text(comparison["stmts"][0]["name"]) == "dotkt$identity$value$0"))
                throw new InvalidOperationException("Identity operands were not hoisted once with collision-free names");
        }
        foreach (var referenceBuild in new[] { false, true })
        {
            var concrete = new JsonObject { [SourceTypeKey] = TypeJson.Fqn("kotlin.Int") };
            var lowered = BirTypeLowering.Lower(concrete, referenceBuild,
                new Dictionary<string, string> { ["kotlin.Int"] = "System.Int32" });
            if (BirTypeLowering.CanonicalPhysicalSlotType(TypeJson.Read(lowered[SourceTypeKey])) != new TypeNode.Fqn("System.Int32"))
                throw new InvalidOperationException("A concrete inline identity frame did not become its exact CLR type");
        }
        Console.WriteLine("[homogeneous identity] self-test OK (source frame, nullable/distinct exclusion, erasure, single evaluation, cleanup)");
    }

    static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
