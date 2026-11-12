using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A singleton-class enum has the same Kotlin comparison contract as a native CLR enum. Give it a real
// compiler-owned comparison face so dispatch works without an impossible System.Enum bound or taking over a
// user-authored CLR IComparable implementation.
// The ordinal field comes only from the producer's explicit enum map, never from a physical-name convention.
static class RichEnumComparisonSynthesis
{
    internal const string Face = "DotKt.Runtime.CompilerServices.KotlinEnumComparison";
    internal static void Apply(IEnumerable<JsonNode> roots)
    {
        foreach (var type in roots.OfType<JsonObject>()
            .SelectMany(root => (root["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
        {
            if (type["richEnum"] is not JsonObject metadata) continue;
            var owner = type["name"].GetValue<string>();
            var ordinal = metadata["ordinal"].GetValue<string>();
            type["base"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.KotlinEnumComparable");
            foreach (var constructor in (type["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (constructor["thisArgs"] is not JsonArray)
                {
                    constructor["baseArgs"] = new JsonArray();
                    constructor["delegationSig"] = new JsonArray();
                    constructor.Remove("delegationDeclarationId");
                }
            var interfaces = type["interfaces"] as JsonArray ?? new JsonArray();
            if (type["interfaces"] == null) type["interfaces"] = interfaces;
            interfaces.Add(TypeJson.Fqn(Face));
            var methods = type["methods"] as JsonArray ?? new JsonArray();
            if (type["methods"] == null) type["methods"] = methods;
            var names = methods.OfType<JsonObject>().Select(method => method["name"]?.GetValue<string>()).ToHashSet();
            var suffix = 0;
            string name;
            do { name = "$enumComparison$" + suffix++; } while (names.Contains(name));
            BirFieldGet Ordinal(IBirExpression receiver) => new() {
                OwnerType = new TypeNode.Fqn(owner), Name = ordinal, Recv = OptionalField<IBirExpression>.Of(receiver),
            };
            methods.Add(new BirFunctionDeclaration {
                Name = name, Static = false, Virtual = true, Override = false,
                Abstract = false, Vis = "private", Generated = true, PhysicalSlotBridge = true,
                Params = new[] { new BirParameter { Name = "other", Type = new TypeNode.Fqn("kotlin.Any") } },
                Ret = new TypeNode.Fqn("kotlin.Int"),
                Body = new IBirStatement[] { new BirReturn { Value = new BirBinaryOperation {
                    Op = "-", Lhs = Ordinal(new BirThis()),
                    Rhs = Ordinal(new BirCast { Type = new TypeNode.Fqn(owner), E = new BirLocal { Name = "other" } }),
                } } },
                ClrInterfaceImpls = new[] { new BirMethodImpl {
                    Owner = new TypeNode.Fqn(Face), Member = "compareEnumTo", Arity = 0,
                    Params = new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, Ret = new TypeNode.Fqn("kotlin.Int"),
                } },
            }.ToDom());
        }
    }
}
