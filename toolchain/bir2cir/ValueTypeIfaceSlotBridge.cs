using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// VALUE-TYPE .NET-INTERFACE-SLOT BRIDGE (#128). A Kotlin class implementing a reference-KLIB-projected .NET GENERIC
// interface instantiated with a VALUE-TYPE arg (e.g. `class C : IComparer<Int>`) declares its override against the
// projected member, whose unconstrained `T` surfaces as `T?` — so post-lowering the override reads
// `Compare(Nullable<int32>, Nullable<int32>)`. But the CONSTRUCTED CLR slot `IComparer<int32>.Compare` uses BARE
// `int32` (a value type substituted into a .NET generic parameter is bare, never Nullable<>). ilemit binds the
// override to that slot via DefineMethodOverride, and the `Nullable<int32>` vs `int32` mismatch throws
// `TypeLoadException: Signature of the body and declaration in a method implementation do not match` at type load.
// (Reference-type args work as-is: `Nullable<String>` == `String` on the CLR — ReferenceNullableStrip already made
// the override param bare `System.String`, matching the slot.)
//
// Fix — mirror the JVM/Java bridge-method idiom (Codex-confirmed CLR idiom; same shape as ComparableBridgeSynthesis):
// for each such override, synthesize a bridge whose signature is the slot's BARE-value shape and which forwards to
// the Nullable-param method. ilemit's EmitNullableCoerced re-wraps each bare value into `Nullable<T>` at the
// forwarding call's args, and ilemit's interface-slot overload disambiguation (Emitter.Assembly.cs — SlotParamMatches
// candidate pick) then wires the BRIDGE (not the Nullable method) to the slot. The original Nullable-param method is
// left as a plain overload the DIRECT call (whose sig is `[Nullable,Nullable]`) still resolves to. Non-ref builds.
//
// SCOPE (tight — must NOT disturb the deliberate value-type-in-type-arg dual representation of Comparable<Int> /
// List<Int> / sorted, memory primitive-dual-representation): a param/return is bare-ified ONLY when (a) the override
// declares it `Nullable<V>` — post ReferenceNullableStrip a surviving Nullable<> wrapper implies V is a VALUE type —
// AND (b) the corresponding .NET slot position is the interface's own UNCONSTRAINED generic parameter (read off the
// .NET refs), which the class instantiates with that value type. A genuinely-`int?`-typed slot param (the .NET method
// itself declares `Nullable<int>`) has a NON-generic-parameter slot position, so it is LEFT nullable.
static class ValueTypeIfaceSlotBridge
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types)
        {
            if (t is not JsonObject to) continue;
            if ((to["kind"] as JsonValue)?.GetValue<string>() != "class") continue;   // interfaces carry no bodies
            if (to["interfaces"] is not JsonArray ifaces) continue;
            if (to["methods"] is not JsonArray methods) continue;
            var owner = (to["name"] as JsonValue)?.GetValue<string>();
            if (string.IsNullOrEmpty(owner)) continue;

            // The class's implemented .NET GENERIC interfaces, resolved to (open .NET def, this class's cir type-args).
            var netIfaces = new List<(Type net, TypeNode[] args)>();
            foreach (var i in ifaces)
            {
                if (TypeJson.Read(i) is not TypeNode.Fqn { Args: { Length: > 0 } fArgs } f) continue;
                var net = refs.ResolveNetType(f.Name, fArgs.Length);
                if (net != null && net.IsInterface && net.IsGenericTypeDefinition) netIfaces.Add((net, fArgs));
            }
            if (netIfaces.Count == 0) continue;

            var bridges = new List<JsonObject>();
            foreach (var m in methods.OfType<JsonObject>().ToList())
            {
                if ((m["override"] as JsonValue)?.GetValue<bool>() != true) continue;
                if (m["params"] is not JsonArray ps) continue;
                if ((m["name"] as JsonValue)?.GetValue<string>() is not string mname) continue;

                // The .NET slot method (same name + arity) on one of the class's .NET generic interfaces.
                Type netIface = null; TypeNode[] ifArgs = null; MethodInfo slot = null;
                foreach (var (net, args) in netIfaces)
                {
                    var cand = SafeMethods(net).FirstOrDefault(x => x.Name == mname && x.GetParameters().Length == ps.Count);
                    if (cand != null) { netIface = net; ifArgs = args; slot = cand; break; }
                }
                if (slot == null) continue;

                var slotPs = slot.GetParameters();
                var declaredParams = new TypeNode[ps.Count];
                var bridgeParams = new TypeNode[ps.Count];
                var needBridge = false;
                for (var k = 0; k < ps.Count; k++)
                {
                    var declared = TypeJson.Read((ps[k] as JsonObject)?["type"]);
                    declaredParams[k] = declared;
                    var bare = BareTypeArgSlot(slotPs[k].ParameterType, ifArgs);
                    if (declared is TypeNode.Nullable && bare != null) { bridgeParams[k] = bare; needBridge = true; }
                    else bridgeParams[k] = declared;
                }
                var declaredRet = TypeJson.Read(m["ret"]);
                var bareRet = BareTypeArgSlot(slot.ReturnType, ifArgs);
                var retBridged = declaredRet is TypeNode.Nullable && bareRet != null;
                var bridgeRet = retBridged ? bareRet : declaredRet;
                if (retBridged) needBridge = true;
                if (!needBridge) continue;

                // Idempotence: a bridge with this bare signature already present (prior pass / hand-written override).
                var exists = methods.OfType<JsonObject>().Any(x =>
                    (x["name"] as JsonValue)?.GetValue<string>() == mname
                    && x["params"] is JsonArray xps && xps.Count == ps.Count
                    && xps.Select((jp, k) => Equals(TypeJson.Read((jp as JsonObject)?["type"]), bridgeParams[k])).All(b => b));
                if (exists) continue;

                bridges.Add(BuildBridge(owner, mname, ps, declaredParams, bridgeParams, declaredRet, bridgeRet, retBridged));
            }
            foreach (var b in bridges) methods.Add(b);
        }
    }

    // The BARE value type the slot uses at a given position when that position is the interface's own UNCONSTRAINED
    // generic parameter (`T`) — the class's cir type-arg at that parameter's index. null when the slot position is a
    // concrete type (NOT a type-arg position — e.g. a genuine `Nullable<int>` slot param, which must stay nullable).
    static TypeNode BareTypeArgSlot(Type slotType, TypeNode[] ifArgs)
    {
        if (!slotType.IsGenericParameter) return null;
        var p = slotType.GenericParameterPosition;
        return p >= 0 && p < ifArgs.Length ? ifArgs[p] : null;
    }

    static IEnumerable<MethodInfo> SafeMethods(Type t)
    {
        try { return t.GetMethods(); } catch { return Array.Empty<MethodInfo>(); }
    }

    // Bridge: BARE-value signature, forwards to the Nullable-param method (`sig` = the original declared params so
    // ilemit resolves the Nullable overload, not the bridge itself). ilemit re-wraps each bare arg into Nullable<T>.
    // A bridged RETURN unwraps the forwarded `Nullable<V>` result back to bare `V` via `nullableValue`.
    static JsonObject BuildBridge(string owner, string mname, JsonArray ps, TypeNode[] declaredParams,
        TypeNode[] bridgeParams, TypeNode declaredRet, TypeNode bridgeRet, bool retBridged)
    {
        var bparams = new JsonArray();
        var fwdArgs = new JsonArray();
        var fwdSig = new JsonArray();
        for (var k = 0; k < ps.Count; k++)
        {
            var name = ((ps[k] as JsonObject)?["name"] as JsonValue)?.GetValue<string>() ?? ("$p" + k);
            bparams.Add(new JsonObject { ["name"] = name, ["type"] = TypeJson.Write(bridgeParams[k]) });
            fwdArgs.Add(new JsonObject { ["k"] = "local", ["name"] = name });
            fwdSig.Add(TypeJson.Write(declaredParams[k]));
        }
        JsonNode forward = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Fqn(owner),
            ["virtual"] = true,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = mname,
            ["sig"] = fwdSig,
            ["args"] = fwdArgs,
        };
        if (retBridged)
            forward = new JsonObject
            {
                ["k"] = "nullableValue",
                ["elem"] = TypeJson.Write(bridgeRet),
                ["e"] = forward,
            };
        // override:false + virtual:true -> Virtual|NewSlot (Emitter.Assembly.cs:721): the standard explicit-slot-impl
        // shape (the slot is wired by ilemit's sig-disambiguated DefineMethodOverride), mirroring ComparableBridgeSynthesis.
        return new JsonObject
        {
            ["name"] = mname,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "public",
            ["params"] = bparams,
            ["ret"] = TypeJson.Write(bridgeRet),
            ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = forward }),
        };
    }
}
