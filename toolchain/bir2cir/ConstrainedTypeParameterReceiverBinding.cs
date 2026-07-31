using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CONSTRAINED DISPATCH ON A TYPE-PARAMETER RECEIVER.
//
// `fun <T : Tagged> f(t: T) = t.tag()` calls an instance member on a receiver whose static type is the type
// PARAMETER `T`, not the interface. ECMA-335 does not allow that as a plain `callvirt Tagged::tag()`: the
// evaluation stack holds a `!!T`, which is neither a `Tagged` reference nor an address, so the verifier reports
// `[found value 'T'][expected ref 'Tagged']` and a value-type instantiation of T would dispatch through a boxed
// copy (or not at all). The verifiable, boxing-free form is `constrained. !!T ; callvirt <constraint>::m`, and
// bir2cir — the layer that fixes the physical CLR representation — is where that call shape is decided. This
// pass authors it explicitly as a `constrainedCall`; ilemit emits that node one-to-one.
//
// The rule is keyed on the RECEIVER'S STATIC TYPE, not on how the receiver is spelled: `t.tag()`, a local copy
// of it, an SM field the suspend lowering spilled it into, a captured `cap$t` in a lambda's state machine and a
// `T`-returning call result are all the same fault, and all of them are reached by reading the one uniform
// static-type source (StaticTypeResolver's `StaticType.Surface`). A receiver already cast/boxed to the
// interface has a non-type-variable surface type and is left alone.
//
// The owner has to be a CLOSED type token, because a MemberRef cannot name an open generic:
//   * BIR that already names the constructed owner (`Keyed<Int>`) is used as-is;
//   * otherwise the type parameter's own lexical BOUND supplies it — `fun <N : Node<N>> N.close()` has receiver
//     static type !!N while faithful BIR names the declaration classifier as bare `Node` (kotc decides no CLR
//     construction), so `Node<N>` comes from N's constraint list;
//   * a locally declared NON-generic owner (`Tagged`) is already closed and needs no such recovery.
// When neither route yields a closed owner the node is left untouched — this pass never guesses an
// instantiation, the same conservatism InheritedMemberOwnerBinding applies to overload resolution.
//
// Runs after the suspend lowerings — the state-machine bodies are then in their final type vocabulary, so an SM
// field's `T` is the SM class's own type parameter — and after InheritedMemberOwnerBinding, whose hierarchy
// substitution has by then named the exact constructed declaring owner. That ordering matters for the second
// route above: a member declared on a GENERIC BASE of the constraint (`T : Derived<Int>` calling
// `Base<X>.m()`) has no `Base` entry in T's constraint list, so the bound cannot close it — but the inherited
// owner binding already has.
static class ConstrainedTypeParameterReceiverBinding
{
    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.ToList();
        var arity = CollectTypeArity(rootList);
        foreach (var root in rootList) BindFile(root, arity);
    }

    // name -> declared type-parameter count, for every type declared in this compilation. Only the ARITY is
    // needed: it says whether a bare owner token is already closed (0) or still has to be constructed.
    static Dictionary<string, int> CollectTypeArity(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        void Collect(JsonNode node)
        {
            if (node is not JsonObject obj || obj["types"] is not JsonArray arr) return;
            foreach (var item in arr)
            {
                if (item is not JsonObject type || Str(type["name"]) is not string name) continue;
                result[name] = (type["typeParams"] as JsonArray)?.Count ?? 0;
                Collect(type);
            }
        }
        foreach (var root in roots) Collect(root);
        return result;
    }

    static void BindFile(JsonNode root, Dictionary<string, int> arity)
    {
        if (root is not JsonObject file) return;
        var noTypeParams = new JsonArray();
        if (file["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindMethod(method, noTypeParams, arity);
        BindAccessors(file["properties"] as JsonArray, noTypeParams, arity);
        if (file["types"] is JsonArray declared)
            foreach (var type in declared.OfType<JsonObject>())
                BindType(type, arity);
    }

    static void BindType(JsonObject type, Dictionary<string, int> arity)
    {
        var typeParams = type["typeParams"] as JsonArray ?? new JsonArray();
        if (type["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>())
                BindMethod(ctor, typeParams, arity);
        if (type["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindMethod(method, typeParams, arity);
        // A property ACCESSOR body is executable code like any other, and `class H<T : Tagged>(val item: T) { val v
        // get() = item.tag() }` reaches this pass only through here — BIR keeps accessors under `properties`, not
        // `methods`.
        BindAccessors(type["properties"] as JsonArray, typeParams, arity);
        if (type["types"] is JsonArray nested)
            foreach (var child in nested.OfType<JsonObject>())
                BindType(child, arity);
    }

    static void BindAccessors(JsonArray properties, JsonArray typeParams, Dictionary<string, int> arity)
    {
        if (properties == null) return;
        foreach (var property in properties.OfType<JsonObject>())
            foreach (var slot in new[] { "getter", "setter" })
                if (property[slot] is JsonObject accessor)
                    BindMethod(accessor, typeParams, arity);
    }

    static void BindMethod(JsonObject method, JsonArray typeParams, Dictionary<string, int> arity)
    {
        var methodParams = method["typeParams"] as JsonArray ?? new JsonArray();
        // The declaration's local/param type environment, for a receiver read that carries no frontend `sty`
        // stamp (a bir2cir-synthesized temp). A name declared twice with DIFFERENT types is dropped rather than
        // resolved last-wins — this pass never guesses which declaration a read refers to.
        var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        void Record(JsonObject declaration)
        {
            if (Str(declaration["name"]) is not string name || TypeJson.Read(declaration["type"]) is not TypeNode type)
                return;
            if (locals.TryGetValue(name, out var prior) && prior != type)
            {
                ambiguous.Add(name);
                locals.Remove(name);
            }
            else if (!ambiguous.Contains(name))
                locals[name] = type;
        }

        if (method["params"] is JsonArray parameters)
            foreach (var p in parameters.OfType<JsonObject>()) Record(p);
        void Collect(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (Str(o["k"]) == "var") Record(o);
                    foreach (var kv in o)
                        if (kv.Value != null) Collect(kv.Value);
                    break;
                case JsonArray a:
                    foreach (var item in a)
                        if (item != null) Collect(item);
                    break;
            }
        }
        if (method["body"] is JsonNode body) Collect(body);
        var scope = BirScope.FromVars(locals);

        void Bind(JsonNode node)
        {
            switch (node)
            {
                case JsonObject call:
                    if (Str(call["k"]) == "callInstance"
                        && TypeJson.Read(call["ownerType"]) is TypeNode.Fqn owner
                        && call["recv"] is JsonObject recv
                        && ReceiverTypeVariable(recv, scope, locals) is TypeNode.Tv tv
                        && ClosedOwner(owner, tv, typeParams, methodParams, arity) is TypeNode.Fqn iface)
                    {
                        // A !!T receiver is not an interface reference on the evaluation stack. Even with the
                        // exact constructed MemberRef, ECMA-335 requires an address plus `constrained. !!T;
                        // callvirt`. Author that dispatch explicitly in CIR; ilemit emits this node one-to-one.
                        // Only the DISPATCH changes. `sig` (the overload key), `typeArgs` (a generic member's
                        // instantiation) and `ret` (the declared call-result view) are facts about the CALL and are
                        // carried through untouched — ilemit consumes all three on this node exactly as it does on a
                        // callInstance, and dropping any of them would make it guess.
                        call["k"] = "constrainedCall";
                        call["recvType"] = TypeJson.Write(tv);
                        call["iface"] = TypeJson.Write(iface);
                        if (call["ret"] == null && call["dynRet"] is JsonNode dynRet)
                            call["ret"] = dynRet.DeepClone();
                        call.Remove("ownerType");
                        call.Remove("virtual");
                        call.Remove("dynRet");
                    }
                    foreach (var kv in call)
                        if (kv.Value != null) Bind(kv.Value);
                    break;
                case JsonArray a:
                    foreach (var item in a)
                        if (item != null) Bind(item);
                    break;
            }
        }
        if (method["body"] is JsonNode methodBody) Bind(methodBody);
    }

    // The receiver's own static type when it is a bare type VARIABLE. The platform-type and value-nullable wrappers
    // are peeled — a `T!` or `T?` receiver is still a `!!T` on the stack once the dispatch happens (the same two
    // MemberCallSubstitution.RecvStaticType peels, so the two readers agree). Anything else (an interface-typed
    // expression, a cast, a concrete class) dispatches fine as an ordinary callvirt and is not this pass's business.
    //
    // A `local` read is confirmed against its DECLARATION as well: `StaticType.Surface` prefers the frontend `sty`
    // stamp, which a later bir2cir retype of the same slot (InlineSplice.RetypeReceiverToConcrete) deliberately
    // shadows. That shadowing is harmless to a name-keyed classifier but not to a decision about the physical call
    // shape — so a stamp that says `T` over a slot re-declared CONCRETE does not get to author `constrained. !!T`.
    static TypeNode.Tv ReceiverTypeVariable(
        JsonObject recv, BirScope scope, Dictionary<string, TypeNode> locals)
    {
        if (Peel(StaticType.Surface(recv, scope)) is not TypeNode.Tv tv) return null;
        if (Str(recv["k"]) == "local" && Str(recv["name"]) is string name
            && locals.TryGetValue(name, out var declared) && Peel(declared) is not TypeNode.Tv)
            return null;
        return tv;
    }

    static TypeNode Peel(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => Peel(o.Of),
        TypeNode.Nullable n => Peel(n.Of),
        _ => t,
    };

    // The CLOSED owner token for a member called on `tv`. See the pass header for the three routes; null means
    // "no closed owner is derivable", which leaves the call untouched.
    static TypeNode.Fqn ClosedOwner(
        TypeNode.Fqn owner,
        TypeNode.Tv tv,
        JsonArray typeParams,
        JsonArray methodParams,
        Dictionary<string, int> arity)
    {
        if (owner.Args != null) return owner;
        var bound = ConstraintAt(tv, owner.Name, typeParams, methodParams);
        if (!arity.TryGetValue(owner.Name, out var count)) return bound;   // referenced type: only the bound closes it
        if (count == 0) return owner;
        return bound?.Args?.Length == count ? bound : null;
    }

    // The unique constraint of `tv` naming `ownerName`, as written in source — a closed type token by
    // construction. Ambiguity (two constraints with that name) yields null rather than a guess.
    static TypeNode.Fqn ConstraintAt(TypeNode.Tv tv, string ownerName, JsonArray typeParams, JsonArray methodParams)
    {
        var source = tv.Scope == "type" ? typeParams : tv.Scope == "method" ? methodParams : null;
        if (source == null || tv.I < 0 || tv.I >= source.Count || source[tv.I] is not JsonObject descriptor
            || descriptor["constraints"] is not JsonArray constraints)
            return null;
        var fqns = constraints.Select(TypeJson.Read).OfType<TypeNode.Fqn>()
            .Where(f => f.Name == ownerName).ToList();
        return fqns.Count == 1 ? fqns[0] : null;
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
