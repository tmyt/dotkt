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
// The owner the constrained call names must be the member's DECLARING type, constructed. Those are two separate
// facts, and ordinary method calls therefore run in TWO phases around InheritedMemberOwnerBinding:
//
//   CloseOpenOwners (BEFORE the inherited-owner walk) — kotc names the receiver's classifier and decides no CLR
//     construction, so `fun <N : Node<N>> N.close()` arrives with the bare token `Node`. Close it from N's own
//     lexical BOUND, which is written closed in source. The node stays a `callInstance`, because closing the
//     token is all this phase knows: the bound is where the receiver's type is pinned, not necessarily where the
//     member is declared.
//   ApplyAll (AFTER it) — rewrite to `constrainedCall`. By now the hierarchy substitution has replaced a bound
//     that merely INHERITS the member with the type that declares it (`T : Leaf<Int>` calling a member of
//     `Root<X>` names `Root<Int>`), which it could only do because the phase above handed it a constructed
//     token. Naming the bound instead is a MemberRef on a type that does not declare the member: it survives
//     locally only because the emitted type carries a fake override, and a referenced interface hierarchy —
//     where reflection does not surface an inherited declaration — has nothing to bind to.
// CLR property nodes have a third, narrow post-resolution phase: only ClrMemberResolution can distinguish a real
// accessor from a public field. Resolved virtual accessors take the same constrained-call path; non-virtual class
// accessors receive the explicit constraint conversion their exact MemberRef requires. Fields remain field operations.
//
// A locally declared NON-generic owner (`Tagged`) is closed already and needs neither phase. When no route
// yields a closed owner the node is left untouched — this pass never guesses an instantiation, the same
// conservatism InheritedMemberOwnerBinding applies to overload resolution.
//
// Both phases run after the suspend lowerings, so the state-machine bodies are already in their final type
// vocabulary and an SM field's `T` is the SM class's own type parameter.
static class ConstrainedTypeParameterReceiverBinding
{
    // PHASE 1 — close a bare owner token from the receiver type parameter's bound, leaving the node a
    // callInstance so the inherited-owner walk can still substitute the declaring type into it.
    public static void CloseOpenOwners(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.ToList();
        var arity = CollectTypeArity(rootList);
        foreach (var root in rootList) BindFile(root, arity, close: true);
    }

    // PHASE 2 — author the constrained dispatch over the now-declaring, now-constructed owner.
    public static void ApplyAll(IEnumerable<JsonNode> roots, ValueTypeOracle isValue)
    {
        var rootList = roots.ToList();
        var arity = CollectTypeArity(rootList);
        foreach (var root in rootList) BindFile(root, arity, close: false, isValue);
    }

    // A CLR property cannot be classified as an accessor rather than a public field until ClrMemberResolution has
    // inspected the referenced declaration. Run this narrow second half after that resolution: only a node already
    // stamped `member:accessor` is a method call and therefore eligible for constrained dispatch. A field stays a
    // clrPropGet/Set and retains its ldfld/stfld representation.
    public static void ApplyResolvedProperties(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.ToList();
        var arity = CollectTypeArity(rootList);
        foreach (var root in rootList)
            BindFile(root, arity, close: false, isValue: null, resolvedPropertiesOnly: true);
        foreach (var root in rootList) DropErasedConstraintFacts(root);
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
                result[name] = TypeParameterFrame.Count(type);
                Collect(type);
            }
        }
        foreach (var root in roots) Collect(root);
        return result;
    }

    static void BindFile(JsonNode root, Dictionary<string, int> arity, bool close,
        ValueTypeOracle isValue = null, bool resolvedPropertiesOnly = false)
    {
        if (root is not JsonObject file) return;
        var noTypeParams = new JsonArray();
        if (file["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindMethod(method, noTypeParams, arity, close, isValue, resolvedPropertiesOnly);
        BindAccessors(file["properties"] as JsonArray, noTypeParams, arity, close, isValue, resolvedPropertiesOnly);
        if (file["types"] is JsonArray declared)
            foreach (var type in declared.OfType<JsonObject>())
                BindType(type, arity, close, isValue, resolvedPropertiesOnly);
    }

    static void BindType(JsonObject type, Dictionary<string, int> arity, bool close,
        ValueTypeOracle isValue, bool resolvedPropertiesOnly)
    {
        var typeParams = TypeParameterFrame.CloneDeclarations(type);
        if (type["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>())
                BindMethod(ctor, typeParams, arity, close, isValue, resolvedPropertiesOnly);
        if (type["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                BindMethod(method, typeParams, arity, close, isValue, resolvedPropertiesOnly);
        // A property ACCESSOR body is executable code like any other, and `class H<T : Tagged>(val item: T) { val v
        // get() = item.tag() }` reaches this pass only through here — BIR keeps accessors under `properties`, not
        // `methods`.
        BindAccessors(type["properties"] as JsonArray, typeParams, arity, close, isValue, resolvedPropertiesOnly);
        if (type["types"] is JsonArray nested)
            foreach (var child in nested.OfType<JsonObject>())
                BindType(child, arity, close, isValue, resolvedPropertiesOnly);
    }

    static void BindAccessors(JsonArray properties, JsonArray typeParams, Dictionary<string, int> arity, bool close,
        ValueTypeOracle isValue, bool resolvedPropertiesOnly)
    {
        if (properties == null) return;
        foreach (var property in properties.OfType<JsonObject>())
            foreach (var slot in new[] { "getter", "setter" })
                if (property[slot] is JsonObject accessor)
                    BindMethod(accessor, typeParams, arity, close, isValue, resolvedPropertiesOnly);
    }

    static void BindMethod(JsonObject method, JsonArray typeParams, Dictionary<string, int> arity, bool close,
        ValueTypeOracle isValue, bool resolvedPropertiesOnly)
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
                    var kind = Str(call["k"]);
                    if (resolvedPropertiesOnly
                        && (kind == "clrPropGet" || kind == "clrPropSet")
                        && Str(call["member"]) == "accessor"
                        && !(call["static"]?.GetValue<bool>() ?? false)
                        && TypeJson.Read(call["type"]) is TypeNode.Fqn propertyOwner
                        && call["recv"] is JsonObject propertyRecv
                        && ReceiverTypeVariable(propertyRecv, scope, locals) is TypeNode.Tv propertyTv
                        && ClosedOwner(propertyOwner, arity) is TypeNode.Fqn closedPropertyOwner)
                    {
                        if (ConstraintDispatchWasErased(propertyTv, typeParams, methodParams))
                            CastResolvedPropertyReceiver(call, closedPropertyOwner);
                        else if (Str(call["dispatch"]) == "callvirt")
                            ConstrainResolvedProperty(call, kind == "clrPropSet", propertyTv, closedPropertyOwner);
                        else
                            CastResolvedPropertyReceiver(call, closedPropertyOwner);
                    }
                    var clrCall = kind is "clrInstance" or "clrGenericInstance";
                    var genericClrCall = kind == "clrGenericInstance";
                    var ownerKey = clrCall ? "type" : "ownerType";
                    if (!resolvedPropertiesOnly && (kind == "callInstance" || clrCall)
                        && TypeJson.Read(call[ownerKey]) is TypeNode.Fqn owner
                        && call["recv"] is JsonObject recv
                        && ReceiverTypeVariable(recv, scope, locals) is TypeNode.Tv tv)
                    {
                        if (close)
                        {
                            // PHASE 1. Only the TOKEN changes: a bare owner gets the construction its receiver's
                            // bound already spells out, so the inherited-owner walk that follows has a constructed
                            // type to substitute the declaring owner into.
                            if (owner.Args == null
                                && ConstraintAt(tv, owner.Name, typeParams, methodParams) is TypeNode.Fqn bound
                                && bound.Args != null)
                                call[ownerKey] = TypeJson.Write(bound);
                        }
                        else if (ClosedOwner(owner, arity) is TypeNode.Fqn iface)
                        {
                            if (ConstraintDispatchWasErased(tv, typeParams, methodParams))
                            {
                                // The source bound remains authoritative Kotlin metadata, but an inner TypeDef whose
                                // bound mentions a star outer cannot carry that relation as a CLR GenericParamConstraint.
                                // Convert the value to the already-selected bound explicitly; constrained. would ask
                                // the verifier to prove a relation deliberately absent from the physical declaration.
                                call["recv"] = new JsonObject
                                {
                                    ["k"] = "cast",
                                    ["type"] = TypeJson.Write(iface),
                                    ["e"] = recv.DeepClone(),
                                };
                            }
                            else
                            {
                                // PHASE 2. A !!T receiver is not an interface reference on the evaluation stack. Even
                                // with the exact constructed MemberRef, ECMA-335 requires an address plus
                                // `constrained. !!T; callvirt`. Author that dispatch explicitly in CIR; ilemit emits
                                // this node one-to-one.
                                // Only the DISPATCH changes. `sig` (the overload key), `typeArgs` (a generic member's
                                // instantiation) and `ret` (the declared call-result view) are facts about the CALL and
                                // are carried through untouched. A clrGenericInstance's bir2cir-internal matching input
                                // is `resolvedMemberParams`; constrainedCall uses the same input under its ordinary
                                // `sig` slot. Move that fact rather than selecting again by name/arity.
                                if (genericClrCall && call["resolvedMemberParams"] is JsonNode resolvedMemberParams)
                                {
                                    call.Remove("resolvedMemberParams");
                                    call["sig"] = resolvedMemberParams;
                                }
                                call["k"] = "constrainedCall";
                                call["recvType"] = TypeJson.Write(tv);
                                call["iface"] = TypeJson.Write(iface);
                                AlignArguments(call, iface, scope, isValue);
                                if (call["ret"] == null && call["dynRet"] is JsonNode dynRet)
                                    call["ret"] = dynRet.DeepClone();
                                call.Remove(ownerKey);
                                call.Remove("virtual");
                                call.Remove("dynRet");
                            }
                        }
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

    static void ConstrainResolvedProperty(JsonObject node, bool write, TypeNode.Tv recvType, TypeNode.Fqn owner)
    {
        var args = new JsonArray();
        var sig = new JsonArray();
        if (write)
        {
            if (node["value"] is JsonNode value) args.Add(value.DeepClone());
            if (node["memberRef"] is JsonObject memberRef
                && memberRef["parameterTypes"] is JsonArray parameters && parameters.Count > 0)
                sig.Add(parameters[^1]?.DeepClone());
        }
        node["k"] = "constrainedCall";
        node["recvType"] = TypeJson.Write(recvType);
        node["iface"] = TypeJson.Write(owner);
        node["method"] = node["accessor"]?.DeepClone();
        node["sig"] = sig;
        node["args"] = args;
        foreach (var key in new[] { "type", "name", "static", "member", "accessor", "dispatch", "super", "value", "sty" })
            node.Remove(key);
    }

    // A non-virtual class accessor has no polymorphic slot for constrained. to select. The receiver's declaration is
    // still !!T, which is not verifier-compatible with the exact class MemberRef, so materialize the ordinary
    // constraint conversion and keep the already-resolved clrProp node/dispatch intact.
    static void CastResolvedPropertyReceiver(JsonObject node, TypeNode.Fqn owner)
    {
        if (node["recv"] is not JsonNode recv) return;
        node["recv"] = new JsonObject
        {
            ["k"] = "cast",
            ["type"] = TypeJson.Write(owner),
            ["e"] = recv.DeepClone(),
        };
    }

    // The constrained owner becomes physically closed only in phase 2. Before that point the nullable-generic write
    // axis sees the bare semantic owner and cannot derive the slot an argument fills. Once `iface` is known, apply the
    // ordinary use-site rule to its declaration signature: Subst(Erase(declared slot), owner args). Only the castable
    // object-erasure seam is materialized; differences inside a constructed generic remain outside this conversion.
    static void AlignArguments(JsonObject call, TypeNode.Fqn iface, BirScope scope, ValueTypeOracle isValue)
    {
        if (isValue == null || iface.Args == null || call["sig"] is not JsonArray sig
            || call["args"] is not JsonArray args || sig.Count != args.Count)
            return;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is not JsonObject arg || TypeJson.Read(sig[i]) is not TypeNode declared) continue;
            if (NullableTvErasureCallRealign.EraseAndSubstituteOwnerSlot(declared, iface.Args, isValue)
                is not TypeNode target) continue;
            var source = StaticType.Surface(arg, scope);
            if (NullableTvErasureCallRealign.CastForErasedObjectSlot(arg, source, target, isValue) is JsonNode wrapped)
                args[i] = wrapped;
        }
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
        // For a local/parameter, its declaration is the authoritative physical stack type. A frontend `sty` may be
        // the selected member owner's face (IPropertySlot/System.String) rather than the receiver slot itself after
        // property substitution; using that refinement here hides the fact that the loaded value is still !!T.
        if (Str(recv["k"]) == "local" && Str(recv["name"]) is string name
            && locals.TryGetValue(name, out var declared))
            return Peel(declared) as TypeNode.Tv;
        return Peel(StaticType.Surface(recv, scope)) as TypeNode.Tv;
    }

    static TypeNode Peel(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => Peel(o.Of),
        TypeNode.Nullable n => Peel(n.Of),
        _ => t,
    };

    // The owner token to name in the constrained call, or null to leave the node alone. By phase 2 the token is
    // whatever the inherited-owner walk settled on, so the only judgement left is whether it is CLOSED: a
    // constructed token stands; a bare one is closed already iff its type is non-generic — a locally declared
    // type says so by its arity, and a referenced one that phase 1 could not construct from the bound has no
    // generic construction to be missing. A locally declared GENERIC type still bare here was never closed by
    // either route, and a MemberRef cannot name it.
    static TypeNode.Fqn ClosedOwner(TypeNode.Fqn owner, Dictionary<string, int> arity)
    {
        if (owner.Args != null) return owner;
        return arity.TryGetValue(owner.Name, out var count) && count > 0 ? null : owner;
    }

    // The unique constraint of `tv` naming `ownerName`, as written in source — a closed type token by
    // construction. Ambiguity (two constraints with that name) yields null rather than a guess.
    static TypeNode.Fqn ConstraintAt(TypeNode.Tv tv, string ownerName, JsonArray typeParams, JsonArray methodParams)
    {
        var source = tv.Scope == "type" ? typeParams : tv.Scope == "method" ? methodParams : null;
        if (source == null || tv.I < 0 || tv.I >= source.Count || source[tv.I] is not JsonObject descriptor
            || ConstraintDeclarations(descriptor) is not JsonArray constraints)
            return null;
        var fqns = constraints.Select(TypeJson.Read).OfType<TypeNode.Fqn>()
            .Where(f => f.Name == ownerName).ToList();
        return fqns.Count == 1 ? fqns[0] : null;
    }

    static bool ConstraintDispatchWasErased(TypeNode.Tv tv, JsonArray typeParams, JsonArray methodParams)
    {
        var source = tv.Scope == "type" ? typeParams : tv.Scope == "method" ? methodParams : null;
        return source != null && tv.I >= 0 && tv.I < source.Count
            && source[tv.I] is JsonObject descriptor
            && descriptor[FBoundStarProjectionErasure.ErasedInnerConstraintKey] is JsonArray;
    }

    static JsonArray ConstraintDeclarations(JsonObject descriptor) =>
        descriptor[FBoundStarProjectionErasure.ErasedInnerConstraintKey] as JsonArray
        ?? descriptor["constraints"] as JsonArray;

    static void DropErasedConstraintFacts(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove(FBoundStarProjectionErasure.ErasedInnerConstraintKey);
                foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                    DropErasedConstraintFacts(child);
                break;
            case JsonArray array:
                foreach (var child in array.Where(value => value != null).ToList())
                    DropErasedConstraintFacts(child);
                break;
        }
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
