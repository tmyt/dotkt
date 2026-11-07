using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    static void InnerFactoryNormalizationSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var logical = new TypeNode.Fqn("Logical", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        var physical = new TypeNode.Fqn("Physical");
        var owners = new Dictionary<string, Owner> {
            ["Logical"] = new Owner { Name = "Logical", ErasedName = "Physical", Needed = true,
                Def = JsonNode.Parse("""{"kind":"class","typeParams":["T"]}""").AsObject() },
        };
        var defs = new Dictionary<string, JsonObject>();
        var carriers = new HashSet<string>(StringComparer.Ordinal) { "Physical" };
        JsonObject Local(string name, TypeNode type) => new() {
            ["k"] = "local", ["name"] = name, ["sty"] = TypeJson.Write(type),
        };
        JsonObject Variable(string name, TypeNode type, JsonNode init) => new() {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(type), ["init"] = init,
        };
        JsonObject Method(TypeNode result, params JsonNode[] statements) => new() {
            ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(result), ["body"] = new JsonArray(statements),
        };
        void Normalize(JsonObject method) => NormalizeInnerFactoryLocals(method, owners, defs, refs, carriers);
        void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Inner factory normalization: " + message);
        }

        var previousSlots = _valueSlots;
        _valueSlots = null;
        try
        {
            // Reverse declaration order requires more than one local-propagation iteration. Cast binding must
            // observe the child's new physical type, and a local function's return must not enter its parent.
            var first = Variable("first", logical, Local("second", logical));
            var second = Variable("second", logical, new JsonObject {
                ["k"] = "callStatic", ["ret"] = TypeJson.Write(physical),
                [ExistentialResultProjectionKey] = TypeJson.Write(logical),
            });
            var cast = new JsonObject {
                ["k"] = "cast", ["type"] = TypeJson.Write(logical), ["sty"] = TypeJson.Write(logical),
                ["e"] = Local("first", logical),
            };
            var method = Method(logical, first, second,
                new JsonObject { ["k"] = "localFun", ["body"] = new JsonArray(new JsonObject {
                    ["k"] = "return", ["value"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int") },
                }) }, new JsonObject { ["k"] = "return", ["value"] = cast });
            Normalize(method);
            Expect(TypeJson.Read(first["type"]) == physical && TypeJson.Read(second["type"]) == physical,
                "fixed-point local propagation stopped early");
            Expect(TypeJson.Read(cast["type"]) == physical && TypeJson.Read(method["ret"]) == physical,
                "post-order cast or parent-only return inference changed");
            Expect(TypeJson.Read(first["init"][ExistentialResultProjectionKey]) == logical,
                "semantic projection was not propagated through the local chain");

            // No body decision survives an invocation: late mutations and newly synthesized declarations both
            // need normalization even when a preceding call found no locals and an unrelated return type.
            var integer = new TypeNode.Fqn("kotlin.Int");
            var late = Method(integer);
            Normalize(late);
            var lateLocal = Variable("late", logical, Local("physicalValue", physical));
            late["body"].AsArray().Add(lateLocal);
            Normalize(late);
            Expect(TypeJson.Read(lateLocal["type"]) == physical && TypeJson.Read(late["ret"]) == integer,
                "late body mutation or unrelated-return filtering lost local normalization");
            var synthesized = Method(logical, new JsonObject { ["k"] = "return", ["value"] = Local("value", physical) });
            Normalize(synthesized);
            Expect(TypeJson.Read(synthesized["ret"]) == physical, "fresh declaration was skipped");

            // Array binding replaces a collected node with a cast containing cloned descendants. Subsequent
            // iterations must collect the live replacement, not retain a snapshot of the original child graph.
            var logicalArray = new TypeNode.Array(logical);
            var physicalArray = new TypeNode.Array(physical);
            var array = Variable("array", logicalArray, new JsonObject {
                ["k"] = "callStatic", ["ret"] = TypeJson.Write(physicalArray),
                [ExistentialArrayElementProjectionKey] = TypeJson.Write(logical),
            });
            var read = new JsonObject {
                ["k"] = "arrayGet", ["array"] = Local("array", logicalArray),
                ["index"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = 0 },
                ["sty"] = TypeJson.Write(logical),
            };
            var element = Variable("element", logical, read);
            var arrays = Method(logicalArray, array, element,
                new JsonObject { ["k"] = "return", ["value"] = Local("array", logicalArray) });
            Normalize(arrays);
            Expect(Str(read["k"]) == "cast" && Bool(read["e"][ProjectedArrayReadKey])
                && TypeJson.Read(element["type"]) == physical && TypeJson.Read(arrays["ret"]) == physicalArray,
                "replacement descendants, array projection or array return was lost");
            var normalized = arrays.ToJsonString();
            Normalize(arrays);
            Expect(arrays.ToJsonString() == normalized, "repeated normalization was not stable");

            // These accesses are deliberately unbound until normalization updates their local receiver. In
            // particular the late outer walk does not itself call BindProjectedFieldStorage: dropping field
            // nodes from the worklist would leave these accesses on the logical generic owner.
            foreach (var role in new[] { "get", "set" })
            {
                var parameters = role == "get" ? new JsonArray() : new JsonArray(new JsonObject {
                    ["name"] = "value", ["type"] = TypeJson.Write(integer),
                });
                var result = role == "get" ? integer : new TypeNode.Fqn("kotlin.Unit");
                var declaration = new JsonObject {
                    ["name"] = "source_" + role, ["params"] = parameters, ["ret"] = TypeJson.Write(result),
                    [KotlinPropertyAccessors.SourceNameKey] = "value", [KotlinPropertyAccessors.KindKey] = role,
                    [KotlinPropertyAccessors.AssociationKey] = "Logical:field:value",
                };
                var slot = new JsonObject {
                    ["name"] = "physical_" + role, ["params"] = parameters.DeepClone(), ["ret"] = TypeJson.Write(result),
                };
                owners["Logical"].Slots.Add(declaration, (slot, role == "get" ? Array.Empty<TypeNode>() : new[] { integer }));
            }
            foreach (var kind in new[] { "field", "lateinitGet", "setFieldExpr", "setField", "callInstance", "newBoundDelegate" })
            {
                var access = new JsonObject {
                    ["k"] = kind, ["name"] = "value", ["ownerType"] = TypeJson.Write(logical),
                    ["recv"] = Local("receiver", logical), ["ret"] = TypeJson.Write(integer),
                    ["value"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(integer), ["value"] = 1 },
                };
                if (kind is "callInstance" or "newBoundDelegate")
                {
                    access["method"] = "source_get";
                    access["args"] = new JsonArray();
                    KotlinPropertyAccessors.PreserveCallIdentity(access, "value", "get");
                }
                var fields = Method(integer,
                    Variable("receiver", logical, Local("physicalReceiver", physical)), access);
                Normalize(fields);
                var selected = kind == "setField" ? access["expr"] : access;
                Expect(TypeJson.Read(selected["ownerType"]) == physical
                    && Str(selected["method"]) == (kind is "setField" or "setFieldExpr" ? "physical_set" : "physical_get"),
                    kind + " did not bind after receiver normalization");
                if (kind == "newBoundDelegate")
                    Expect(TypeJson.Read(selected["calleeOwner"]) == physical, "delegate target owner was not normalized");
            }
        }
        finally { _valueSlots = previousSlots; }
    }
}
