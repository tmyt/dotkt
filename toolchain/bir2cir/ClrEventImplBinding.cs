using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// .NET EVENT IMPLEMENT/RAISE binding (§4.2/§4.3 of design-clr-event-model.md) — the Kotlin↔CLR relation for a Kotlin
// class that IMPLEMENTS a CLR interface event (`class ViewModelBase : INotifyPropertyChanged { override val
// PropertyChanged by clrEvent() }`) or DECLARES a new one (`val clicked: ClrEvent<D> by clrEvent()`). Sibling of the
// CONSUME pass ClrEventSubscriptionBinding.
//
// kotc emitted PURE-KOTLIN identities: a per-event `clrEventBacking{name, handlerType}` directive (the handler as a
// Kotlin FUNCTION type) in the type's `clrEvents` array, plus three synthesized accessor methods add_/remove_/raise_<E>
// whose bodies are tagged `clrEventAccessor{kind,event}` and whose add_/remove_ carry an `overrides` closure naming the
// interface event slot by Kotlin identity (owner FQN + event name). This pass — the ONLY layer that reads the ref.dll —
// resolves the concrete delegate `D` (the interface event's EventHandlerType, or an Action/Func synthesized from the
// handler fn for a new event) and:
//   * inserts a real backing field `<E>$delegate : D`;
//   * rewrites each accessor's param signature to `D` (add/remove) / D.Invoke's params (raise) and its tagged body to a
//     `clrEventAccessorImpl{kind, name:<field>, delegateType:D}` CIR directive (ilemit emits the CAS loop / raise);
//   * replaces `clrEventBacking` with a type-level `clrEventDecl{name, delegateType:D}` (ilemit emits the `.event` row).
// The interface MethodImpl wiring is FREE: ilemit's referenced-interface binding pass matches the public-virtual
// add_/remove_<E> to the interface's add_/remove_ event-accessor slots by name+signature (DefineMethodOverride).
// A RAISE (`clrEventRaise`) is bound separately below to a `callInstance raise_<E>` on the receiver's declaring type,
// with the §6 guard: raise is legal ONLY for a Kotlin-DECLARED event (one that has a synthesized `raise_<E>`).
static class ClrEventImplBinding
{
    public static JsonNode BindImplementations(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is JsonObject obj && obj["types"] is JsonArray types)
            foreach (var t in types.OfType<JsonObject>())
                BindType(t, refs);
        return root;
    }

    // Raise sites and their declaring event may live in different source files. Resolve them only after every local
    // event implementation has its final synthesized raise_<E> declaration, then copy that declaration's exact
    // parameter vector onto the call. ilemit links this descriptor; it never selects a local member by name/arity.
    public static void BindRaisesAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var signatures = new Dictionary<(string Owner, string Method), JsonArray>();
        foreach (var root in rootList.OfType<JsonObject>())
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                {
                    var owner = Str(type["name"]);
                    if (owner == null || type["methods"] is not JsonArray methods) continue;
                    foreach (var method in methods.OfType<JsonObject>())
                    {
                        var name = Str(method["name"]);
                        if (name == null || !name.StartsWith("raise_", StringComparison.Ordinal)
                            || method["params"] is not JsonArray parameters) continue;
                        signatures[(owner, name)] = new JsonArray(parameters.OfType<JsonObject>()
                            .Select(p => p["type"]?.DeepClone()).ToArray());
                    }
                }
        foreach (var root in rootList) BindRaises(root, refs, signatures);
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // Resolve every `clrEvents` backing directive on one type into a real field + accessor impls + a `clrEventDecl`.
    static void BindType(JsonObject type, ReferenceMetadataIndex refs)
    {
        if (type["clrEvents"] is not JsonArray backings || backings.Count == 0) return;
        var methods = type["methods"] as JsonArray ?? new JsonArray();
        var fields = type["fields"] as JsonArray;
        if (fields == null) { fields = new JsonArray(); type["fields"] = fields; }
        var decls = new JsonArray();
        foreach (var backing in backings.OfType<JsonObject>())
        {
            var name = Str(backing["name"]);
            if (name == null) continue;
            var addM = FindMethod(methods, "add_" + name);
            var remM = FindMethod(methods, "remove_" + name);
            var raiseM = FindMethod(methods, "raise_" + name);
            if (addM == null || remM == null || raiseM == null)
                throw new InvalidOperationException($"bir2cir: clrEvent '{name}' is missing a synthesized accessor (add/remove/raise) — kotc synthesis defect (§4.2)");

            // The concrete delegate `D` + its Invoke param nodes (raise's signature).
            var (delegateNode, invokeParamNodes) = ResolveDelegate(name, addM, backing["handlerType"], refs);

            var fieldName = name + "$delegate";
            fields.Add(new JsonObject { ["name"] = fieldName, ["type"] = delegateNode.DeepClone(), ["vis"] = "private" });

            RewriteFieldLike(addM, "add", fieldName, delegateNode);
            RewriteFieldLike(remM, "remove", fieldName, delegateNode);
            RewriteRaise(raiseM, fieldName, delegateNode, invokeParamNodes);

            decls.Add(new JsonObject { ["k"] = "clrEventDecl", ["name"] = name, ["delegateType"] = delegateNode.DeepClone() });
        }
        type["clrEvents"] = decls;
    }

    static JsonObject FindMethod(JsonArray methods, string name) =>
        methods.OfType<JsonObject>().FirstOrDefault(m => Str(m["name"]) == name);

    // add_/remove_: param[0].type := D; body := the CAS-loop directive; drop the (consumed) overrides closure.
    static void RewriteFieldLike(JsonObject m, string kind, string fieldName, JsonNode delegateNode)
    {
        if (m["params"] is JsonArray ps && ps.Count > 0 && ps[0] is JsonObject p0)
            p0["type"] = delegateNode.DeepClone();
        m["body"] = new JsonArray { AccessorImpl(kind, fieldName, delegateNode) };
        m["specialName"] = true;   // ECMA-335 event-accessor convention (ilemit stamps MethodAttributes.SpecialName)
        m.Remove("overrides");
    }

    // raise_: params := D.Invoke's params (so `field?.Invoke(args)` forwards them); body := the raise directive.
    static void RewriteRaise(JsonObject m, string fieldName, JsonNode delegateNode, List<JsonNode> invokeParamNodes)
    {
        var ps = new JsonArray();
        for (int i = 0; i < invokeParamNodes.Count; i++)
            ps.Add(new JsonObject { ["name"] = "arg" + i, ["type"] = invokeParamNodes[i].DeepClone() });
        m["params"] = ps;
        m["body"] = new JsonArray { AccessorImpl("raise", fieldName, delegateNode) };
        m["specialName"] = true;   // ECMA-335 event-accessor convention (the .event's Fire)
        m.Remove("overrides");
    }

    static JsonObject AccessorImpl(string kind, string fieldName, JsonNode delegateNode) => new()
    {
        ["k"] = "clrEventAccessorImpl",
        ["kind"] = kind,
        ["name"] = fieldName,
        ["delegateType"] = delegateNode.DeepClone(),
    };

    // The concrete delegate type node for an event + the type nodes of its Invoke's params (raise's signature). For an
    // OVERRIDE (the add_ accessor's `overrides` closure names the interface event slot) the delegate is the interface
    // event's `EventHandlerType`, read off the ref.dll — bir2cir OWNS this resolution (§9). For a NEW event (no override)
    // the handler Kotlin FUNCTION type maps to a System.Action/Func delegate of the same shape.
    static (JsonNode delegateNode, List<JsonNode> invokeParams) ResolveDelegate(
        string name, JsonObject addM, JsonNode handlerType, ReferenceMetadataIndex refs)
    {
        if (addM["overrides"] is JsonArray ovs)
            foreach (var o in ovs.OfType<JsonObject>())
            {
                var owner = TypeJson.OwnerName(o["owner"]);
                var member = Str(o["member"]);
                if (owner == null || member == null) continue;
                var iface = refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(owner));
                var ev = iface?.GetEvent(member, BindingFlags.Public | BindingFlags.Instance);
                var d = ev?.EventHandlerType;
                if (d == null) continue;
                var invoke = d.GetMethod("Invoke");
                var invokeParams = invoke?.GetParameters().Select(p => TypeJson.Write(NetTypeToNode(p.ParameterType))).ToList()
                                   ?? new List<JsonNode>();
                return (TypeJson.Write(NetTypeToNode(d)), invokeParams);
            }

        // A NEW event: the overrides closure was empty (no interface slot). Map the handler fn -> Action/Func.
        if (handlerType is JsonObject fn && Str(fn["t"]) == "fn")
            return FnToDelegate(fn);

        throw new InvalidOperationException(
            $"bir2cir: cannot resolve the delegate type for event '{name}' — it neither overrides a resolvable .NET interface "
            + "event nor carries an inferable handler function type (#187 / §4.2)");
    }

    // A handler Kotlin function type `(A,B)->R` -> `System.Action<A,B>` (R = Unit/void) or `System.Func<A,B,R>`, plus the
    // Invoke param nodes (= the handler's params). The natural CLR lowering of a Kotlin fn type for a brand-new event.
    static (JsonNode, List<JsonNode>) FnToDelegate(JsonObject fn)
    {
        var pars = (fn["params"] as JsonArray)?.OfType<JsonNode>().Select(p => p.DeepClone()).ToList() ?? new List<JsonNode>();
        var ret = fn["ret"];
        bool voidRet = ret == null || (ret is JsonObject ro && Str(ro["name"]) is "kotlin.Unit" or "void");
        var args = new JsonArray();
        foreach (var p in pars) args.Add(p.DeepClone());
        JsonNode delegateNode;
        if (voidRet)
            delegateNode = args.Count == 0 ? TypeJson.Fqn("System.Action")
                : new JsonObject { ["t"] = "fqn", ["name"] = "System.Action", ["args"] = args };
        else
        {
            args.Add(ret.DeepClone());
            delegateNode = new JsonObject { ["t"] = "fqn", ["name"] = "System.Func", ["args"] = args };
        }
        return (delegateNode, pars);
    }

    // An MLC Type -> its concrete lowered TypeNode (BCL FullName spelling, generic args recursed, Nullable<T> -> T?). Keeps
    // the delegate's CONCRETE Fqn (a named event delegate like PropertyChangedEventHandler must link by name, not as a `fn`).
    static TypeNode NetTypeToNode(Type t)
    {
        if (t.IsGenericParameter) return new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        if (t.IsByRef) return new TypeNode.ByRef(NetTypeToNode(t.GetElementType()));
        if (t.IsArray) return new TypeNode.Array(NetTypeToNode(t.GetElementType()));
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(NetTypeToNode).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    static string Dotted(string s) => s.Replace('+', '.');
    static string StripArity(string s) { var i = s.IndexOf('`'); return i >= 0 ? s[..i] : s; }

    // ---- RAISE call sites -----------------------------------------------------------------------

    // Bind every `clrEventRaise{type,event,recv,args}` (a Kotlin-declared event handle `.invoke(...)`) to a
    // `callInstance raise_<E>` on the receiver's static type — the type that declares the synthesized `raise_<E>`. §6
    // GUARD: raise is legal ONLY for a Kotlin-declared event; a `clrEventRaise` whose owner is a CONSUMED foreign .NET
    // event (no synthesized raise_) is caught in ilemit as a missing method — but here we keep the shape honest.
    static void BindRaises(JsonNode node, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<(string Owner, string Method), JsonArray> signatures)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
                if (kv.Value != null) BindRaises(kv.Value, refs, signatures);
            if (Str(obj["k"]) == "clrEventRaise")
            {
                var owner = obj["type"]?.DeepClone();
                // §6 GUARD: raise is legal ONLY for a Kotlin-DECLARED event. If the receiver's owner resolves to a real
                // .NET type off the ref.dll, this is a CONSUMED foreign event (no synthesized raise_) — a hard error (you
                // raise on the declaring instance, and only Kotlin-declared events have a raise_ accessor).
                if (TypeJson.OwnerName(owner) is string ownerName
                    && refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerName)) != null)
                    throw new InvalidOperationException(
                        $"bir2cir: cannot raise the .NET event '{Str(obj["event"])}' on '{ownerName}' — you can only raise an "
                        + "event you DECLARE in Kotlin (`override val E by clrEvent()`), not a consumed foreign .NET event (§6/#187)");
                var raiseName = "raise_" + Str(obj["event"]);
                var localOwner = TypeJson.OwnerName(owner)
                    ?? throw new InvalidOperationException($"bir2cir: event raise '{raiseName}' has no owner type");
                if (!signatures.TryGetValue((localOwner, raiseName), out var signature))
                    throw new InvalidOperationException(
                        $"bir2cir: event raise '{localOwner}.{raiseName}' has no synthesized local declaration");
                var recv = obj["recv"]?.DeepClone();
                var args = obj["args"] as JsonArray ?? new JsonArray();
                // Replace the node's contents in place with a plain instance call to raise_<E>.
                foreach (var k in obj.Select(kv => kv.Key).ToList()) obj.Remove(k);
                obj["k"] = "callInstance";
                obj["ownerType"] = owner;
                obj["method"] = raiseName;
                obj["sig"] = signature.DeepClone();
                obj["virtual"] = true;
                obj["recv"] = recv;
                obj["args"] = args;
            }
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) BindRaises(it, refs, signatures);
    }
}
