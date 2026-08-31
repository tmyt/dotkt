using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Non-generic `System.IComparable` bridge. A Kotlin `class C : Comparable<C>` lowers (via the stdlib's
// `@ClrTypeAlias("System.IComparable")` on `kotlin.Comparable`) to `C : System.IComparable<C>` — the GENERIC
// interface only. But the CLR-side natural-ordering dispatch spine is the NON-generic `System.IComparable`:
// the stdlib's `compareValues` casts `a as IComparable` and ilemit's constrained-compareTo emits the value-type-safe
// `IComparable.CompareTo(object)` fallback (a boxed primitive implements IComparable but NOT a reified
// `IComparable<object>`). Every comparable BCL type (Int32/String/DateTime/...) therefore implements BOTH faces;
// a user Kotlin type that implements only the generic face hits `EntryPointNotFoundException` (SAM-shim
// `a.compareTo(b)` inside the rt's `sortWith`) or `InvalidCastException` (`compareValues`) the moment a compiled
// stdlib body sorts it. Mirror the BCL convention: for every emitted CLASS whose lowered interfaces include
// `kotlin.Comparable<X>` (or an explicitly projected `System.IComparable<X>`), add `System.IComparable` + a
// `CompareTo(Any)` bridge that casts the arg to X and forwards to the generic CompareTo. This runs at the final
// semantic boundary, before BirTypeLowering: a legal covariant override returning `Nothing` must retain that stamp
// on the synthesized call so NothingValueTermination can terminate the physical Int32 slot instead of returning
// Nothing's CLR object erasure into it (#321). Non-ref builds only (the ref surface stays pure Kotlin).
static class ComparableBridgeSynthesis
{
    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types)
        {
            if (t is not JsonObject to) continue;
            if ((to["kind"] as JsonValue)?.GetValue<string>() != "class") continue;   // interfaces carry no bodies
            if (to["interfaces"] is not JsonArray ifaces) continue;
            TypeNode selfArg = null; var hasNonGeneric = false;
            foreach (var i in ifaces)
            {
                if (TypeJson.Read(i) is not TypeNode.Fqn f) continue;
                if (f.Name == "System.IComparable" && f.Args == null) hasNonGeneric = true;
                else if (f.Name is not ("kotlin.Comparable" or "System.IComparable")) continue;
                else if (f.Args is { Length: 1 })
                {
                    if (BirTypeLowering.ComparableApplicationCollapses(f.Args[0])) hasNonGeneric = true;
                    else selfArg = f.Args[0];
                }
            }
            if (selfArg == null || hasNonGeneric) continue;   // 1-arg IComparable<X> only
            if (to["methods"] is not JsonArray methods) { methods = new JsonArray(); to["methods"] = methods; }
            // Idempotence: skip when a 1-arg CompareTo(object) is already declared (user-written or a prior pass).
            var exists = methods.OfType<JsonObject>().Any(m =>
                (m["name"] as JsonValue)?.GetValue<string>() == "CompareTo"
                && m["params"] is JsonArray ps && ps.Count == 1
                && TypeJson.Read((ps[0] as JsonObject)?["type"]) is TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" });
            if (exists) continue;
            var owner = (to["name"] as JsonValue)?.GetValue<string>();
            if (string.IsNullOrEmpty(owner)) continue;
            // Forward only to the declaration the frontend identified as the Comparable slot implementation. Name
            // and arity alone also match unrelated Kotlin overloads after DeclarationRename; copying such an overload's
            // Nothing return stamp would turn a valid Int-returning bridge into invalid terminating IL.
            var targets = methods.OfType<JsonObject>().Where(m =>
                (m["name"] as JsonValue)?.GetValue<string>() is "CompareTo" or "compareTo"
                && m["params"] is JsonArray ps1 && ps1.Count == 1
                && m["overrides"] is JsonArray overrides
                && overrides.OfType<JsonObject>().Any(o =>
                    TypeJson.Read(o["owner"]) is TypeNode.Fqn ownerSpec
                    && ownerSpec.Name is "kotlin.Comparable" or "System.IComparable"
                    && ownerSpec.Args is { Length: 1 }
                    && ownerSpec.Args[0].Equals(selfArg)
                    && (o["member"] as JsonValue)?.GetValue<string>() is "compareTo" or "CompareTo"
                    && (o["kind"] as JsonValue)?.GetValue<string>() == "method")).ToList();
            if (targets.Count != 1 || targets[0]["params"] is not JsonArray targetParams
                || targetParams[0] is not JsonObject targetParam
                || TypeJson.Read(targetParam["type"]) is not TypeNode targetParamType)
                continue;
            var target = targets[0];
            var targetName = (target["name"] as JsonValue)?.GetValue<string>();
            var forwardCall = new JsonObject
            {
                ["k"] = "callInstance",
                ["ownerType"] = TypeJson.Fqn(owner),
                ["virtual"] = true,
                ["clrOwnerResolved"] = true,
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["method"] = targetName,
                ["sig"] = new JsonArray { TypeJson.Write(targetParamType) },
                ["args"] = new JsonArray(new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(targetParamType),
                    ["e"] = new JsonObject { ["k"] = "local", ["name"] = "obj" },
                }),
            };
            if (target?["ret"] is JsonNode targetReturn)
                forwardCall["sty"] = targetReturn.DeepClone();
            ifaces.Add(TypeJson.Fqn("System.IComparable"));
            var bridge = new JsonObject
            {
                ["name"] = "CompareTo",
                ["static"] = false,
                ["override"] = false,
                ["virtual"] = true,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray(new JsonObject { ["name"] = "obj", ["type"] = TypeJson.Fqn("kotlin.Any") }),
                ["ret"] = TypeJson.Fqn("kotlin.Int"),
                ["body"] = new JsonArray(new JsonObject
                {
                    ["k"] = "return",
                    ["value"] = forwardCall,
                }),
            };
            methods.Add(MaterializedExecutable.Normalize(bridge));
        }
    }
}
