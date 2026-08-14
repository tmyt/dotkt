// #370: the members a COLLECTION-LITERAL construction invokes.
//
// `listOf(1, 2)` becomes a `newList` node, and an emitter turns that into `newobj List<int>()` followed by a
// `callvirt Add` per element. Those two members are as external as any other — they live in
// System.Collections — but they were never written down, because the node says what to BUILD rather than what
// to call, and the emitter filled in the rest by name.
//
// Choosing the constructor and the accumulator is a decision about physical CLR representation, which is this
// layer's to make. It is not a re-derivation of what the source meant: the source said `listOf`, and the
// choice of `List`1` with a parameterless constructor and a one-argument `Add` is the shape THIS pass picked
// when it minted the node. So it names them, and the emitter stops asking.
//
// The signatures below are the declarations' own — `Add(!0)`, `set_Item(!0, !1)` — matched structurally like
// every other member, so a BCL that grows an overload cannot silently change which one is meant.

using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    // Each collection literal, the type it constructs, and the member it accumulates through.
    static readonly (string Kind, string Owner, int Arity, string Accumulator, string RefKey)[] CollectionTemplates =
    {
        ("newList", "System.Collections.Generic.List", 1, "Add", "addRef"),
        ("newSet", "System.Collections.Generic.HashSet", 1, "Add", "addRef"),
        ("newMap", "System.Collections.Generic.Dictionary", 2, "set_Item", "setItemRef"),
    };

    static void ResolveCollectionTemplate(JsonObject node, string kind)
    {
        var template = CollectionTemplates.FirstOrDefault(t => t.Kind == kind);
        if (template.Kind == null || node.ContainsKey("ctorRef")) return;
        // The element/key/value types the node already states ARE the construction's instantiation.
        var args = kind == "newMap"
            ? new[] { TypeJson.Read(node["keyType"]), TypeJson.Read(node["valType"]) }
            : new[] { TypeJson.Read(node["elem"]) };
        if (args.Any(a => a == null)) return;
        var ownerFqn = new TypeNode.Fqn(template.Owner, args);
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException(
                $"bir2cir: {kind} constructs '{template.Owner}', which does not resolve to a .NET type (#370)");

        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == 0).ToList();
        var ctor = TryPickUniqueCtor(ctors, new List<TypeNode>(), args)
            ?? throw new InvalidOperationException(
                $"bir2cir: '{template.Owner}' has no unique parameterless constructor for {kind} (#370)");
        node["ctorRef"] = MemberRefJson(ctor, MemberRefNode.Kinds.Ctor, open, args);

        // The accumulator takes the construction's own type parameters, positionally — `Add(!0)` for a list,
        // `set_Item(!0, !1)` for a map. Stating that vector is what keeps a future overload from being picked
        // up by a name match.
        var accumulatorSig = Enumerable.Range(0, template.Arity)
            .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToList();
        var accumulators = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == template.Accumulator && m.GetParameters().Length == accumulatorSig.Count).ToList();
        var accumulator = TryPickUnique(accumulators, accumulatorSig, args)
            ?? throw new InvalidOperationException(
                $"bir2cir: '{template.Owner}.{template.Accumulator}' does not resolve to one declaration for {kind} (#370)");
        node[template.RefKey] = MemberRefJson(accumulator, MemberRefNode.Kinds.Method, open, args);
    }

    /// <summary>
    /// The members a SPREAD ARGUMENT builds through: `f(1, *a, 2)` accumulates into a `List&lt;T&gt;` and hands over
    /// its `ToArray()`. Four members, chosen here for the same reason the collection literals' two are.
    /// </summary>
    static void ResolveSpreadConcat(JsonObject node)
    {
        if (node.ContainsKey("ctorRef")) return;
        if (TypeJson.Read(node["elem"]) is not TypeNode elem) return;
        var args = new[] { elem };
        var open = ResolveOwnerType(new TypeNode.Fqn(SpreadOwner, args))
            ?? throw new InvalidOperationException(
                $"bir2cir: spreadConcat accumulates into '{SpreadOwner}', which does not resolve to a .NET type (#370)");

        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == 0).ToList();
        node["ctorRef"] = MemberRefJson(
            TryPickUniqueCtor(ctors, new List<TypeNode>(), args)
                ?? throw Missing(SpreadOwner, ".ctor"),
            MemberRefNode.Kinds.Ctor, open, args);

        // The element parameter is the construction's OWN type parameter, positionally — `Add(!0)` — and the
        // spread arm takes the sequence over it. Stating the vectors is what stops a future overload of either
        // name from being picked up by the name alone.
        var element = new TypeNode.Tv("type", 0);
        StampSpreadMember(node, open, args, "addRef", "Add", new List<TypeNode> { element });
        StampSpreadMember(node, open, args, "addRangeRef", "AddRange",
            new List<TypeNode> { new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { element }) });
        StampSpreadMember(node, open, args, "toArrayRef", "ToArray", new List<TypeNode>());
    }

    const string SpreadOwner = "System.Collections.Generic.List";

    static void StampSpreadMember(JsonObject node, Type open, TypeNode[] args,
        string refKey, string name, List<TypeNode> signature)
    {
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == name && m.GetParameters().Length == signature.Count).ToList();
        node[refKey] = MemberRefJson(
            TryPickUnique(candidates, signature, args) ?? throw Missing(SpreadOwner, name),
            MemberRefNode.Kinds.Method, open, args);
    }

    static InvalidOperationException Missing(string owner, string member) =>
        new($"bir2cir: '{owner}.{member}' does not resolve to one declaration for spreadConcat (#370)");

    /// <summary>
    /// The enumerator protocol an inlined `for` walks. Both arms are named: WHICH arm the emitter takes is a
    /// Reflection.Emit fact (an instantiation over a type still being built cannot carry a usable member token),
    /// so that choice stays where the knowledge is — but choosing between two members already named is not
    /// member selection, and neither arm's members are derived by name any more.
    /// </summary>
    static void ResolveForEachInline(JsonObject node)
    {
        if (node.ContainsKey("moveNextRef")) return;
        if (TypeJson.Read(node["elem"]) is not TypeNode elem) return;
        var element = new TypeNode.Tv("type", 0);

        StampProtocolMember(node, "enumerableGetRef", "System.Collections.Generic.IEnumerable",
            new[] { elem }, "GetEnumerator", new List<TypeNode>());
        StampProtocolMember(node, "currentRef", "System.Collections.Generic.IEnumerator",
            new[] { elem }, "get_Current", new List<TypeNode>());
        // The non-generic arm's owners take no arguments at all — the erased walk exists precisely because the
        // constructed ones cannot be spoken here.
        StampProtocolMember(node, "enumerableGetErasedRef", "System.Collections.IEnumerable",
            Array.Empty<TypeNode>(), "GetEnumerator", new List<TypeNode>());
        StampProtocolMember(node, "currentErasedRef", "System.Collections.IEnumerator",
            Array.Empty<TypeNode>(), "get_Current", new List<TypeNode>());
        StampProtocolMember(node, "moveNextRef", "System.Collections.IEnumerator",
            Array.Empty<TypeNode>(), "MoveNext", new List<TypeNode>());
        _ = element;
    }

    static void StampProtocolMember(JsonObject node, string refKey, string ownerFqn,
        TypeNode[] args, string name, List<TypeNode> signature)
    {
        var ownerNode = args.Length == 0 ? new TypeNode.Fqn(ownerFqn) : new TypeNode.Fqn(ownerFqn, args);
        var open = ResolveOwnerType(ownerNode)
            ?? throw new InvalidOperationException(
                $"bir2cir: forEachInline walks '{ownerFqn}', which does not resolve to a .NET type (#370)");
        var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == name && m.GetParameters().Length == signature.Count).ToList();
        var win = TryPickUnique(candidates, signature, args)
            ?? throw new InvalidOperationException(
                $"bir2cir: '{ownerFqn}.{name}' does not resolve to one declaration for forEachInline (#370)");
        node[refKey] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, args);
    }

    /// <summary>
    /// The `Unit` singleton a void lambda needs when it fills a Unit-returning delegate slot.
    /// </summary>
    /// <remarks>
    /// The emitter reconciles the two by wrapping the lambda in an adapter that calls it and returns
    /// `Unit.INSTANCE`. That adapter is synthesized per arity and belongs to no node — but the FIELD does not
    /// depend on the arity, or on anything else: it is one static field of one type. So it rides the node whose
    /// conversion needs it, and the emitter stops naming a stdlib member by string.
    ///
    /// A build where `kotlin.Unit` is the type being emitted (the stdlib's own) has no reference to make, and
    /// none is needed there — that is the local axis.
    /// </remarks>
    static void ResolveUnitSingleton(JsonObject node)
    {
        if (node.ContainsKey("unitInstanceRef")) return;
        if (TypeJson.Read(node["funcType"]) is not TypeNode.Fn fn) return;
        if (fn.Ret is not TypeNode.Fqn { Name: "void" or "System.Void" }) return;
        var open = ResolveOwnerType(new TypeNode.Fqn(UnitFqn));
        if (open == null) return;
        var instance = open.GetField(UnitInstance, BindingFlags.Public | BindingFlags.Static);
        if (instance == null) return;
        node["unitInstanceRef"] = FieldRefJson(instance, open, Array.Empty<TypeNode>());
    }

    const string UnitFqn = "kotlin.Unit";
    const string UnitInstance = "INSTANCE";

    /// <summary>
    /// The three BCL members a field-like event accessor's CAS loop runs through.
    /// </summary>
    /// <remarks>
    /// `Delegate.Combine`/`Remove` and `Interlocked.CompareExchange&lt;D&gt;` are as external as anything else, and the
    /// emitter was picking them — one by a computed name, one by filtering an enumeration on a name predicate.
    /// Neither shape is visible to a reader scanning for `GetMethod("…")`, which is how they outlived the rest.
    /// </remarks>
    static void ResolveEventCas(JsonObject node)
    {
        if (node.ContainsKey("combineRef")) return;
        var kind = (node["kind"] as JsonValue)?.GetValue<string>();
        if (kind is not ("add" or "remove")) return;
        if (TypeJson.Read(node["delegateType"]) is not TypeNode delegateType) return;

        var delegateFqn = new TypeNode.Fqn("System.Delegate");
        var del = ResolveOwnerType(delegateFqn)
            ?? throw new InvalidOperationException("bir2cir: 'System.Delegate' does not resolve to a .NET type (#370)");
        var pair = new List<TypeNode> { delegateFqn, delegateFqn };
        foreach (var name in new[] { "Combine", "Remove" })
        {
            var cands = del.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == name && m.GetParameters().Length == 2).ToList();
            var win = TryPickUnique(cands, pair, Array.Empty<TypeNode>())
                ?? throw new InvalidOperationException(
                    $"bir2cir: 'System.Delegate.{name}(Delegate, Delegate)' does not resolve to one declaration (#370)");
            node[name == "Combine" ? "combineRef" : "removeRef"] =
                MemberRefJson(win, MemberRefNode.Kinds.Method, del, Array.Empty<TypeNode>());
        }

        // `CompareExchange<T>(ref T, T, T)` — the ONE generic overload; the non-generic siblings take concrete
        // slots and are a different member entirely.
        var interlocked = ResolveOwnerType(new TypeNode.Fqn("System.Threading.Interlocked"))
            ?? throw new InvalidOperationException("bir2cir: 'System.Threading.Interlocked' does not resolve (#370)");
        var cas = interlocked.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CompareExchange" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 3).ToList();
        if (cas.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: 'Interlocked.CompareExchange<T>' resolves to {cas.Count} declarations, not one (#370)");
        node["compareExchangeRef"] = MemberRefJson(cas[0], MemberRefNode.Kinds.Method, interlocked, Array.Empty<TypeNode>());
        _ = delegateType;
    }

    /// <summary>
    /// The members a value-type nullability conversion runs through: `Nullable&lt;T&gt;`'s constructor and its two
    /// accessors.
    /// </summary>
    /// <remarks>
    /// The OWNER varies per site — `Nullable&lt;int&gt;` and `Nullable&lt;char&gt;` are different constructed types with
    /// different members — so no fixed table can carry these. The node states its element, which is all the
    /// owner needs, so each conversion names its own three.
    /// </remarks>
    static void ResolveNullableConversion(JsonObject node, string kind)
    {
        if (node.ContainsKey("ctorRef") || node.ContainsKey("valueRef") || node.ContainsKey("hasValueRef")) return;
        if (TypeJson.Read(node["elem"]) is not TypeNode elem) return;
        var args = new[] { elem };
        var open = ResolveOwnerType(new TypeNode.Fqn(NullableFqn, args))
            ?? throw new InvalidOperationException(
                $"bir2cir: {kind} wraps '{NullableFqn}', which does not resolve to a .NET type (#370)");
        var element = new TypeNode.Tv("type", 0);

        if (kind is "nullableNull" or "nullableWrap")
        {
            var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(c => c.GetParameters().Length == 1).ToList();
            node["ctorRef"] = MemberRefJson(
                TryPickUniqueCtor(ctors, new List<TypeNode> { element }, args)
                    ?? throw new InvalidOperationException(
                        $"bir2cir: '{NullableFqn}' has no unique one-argument constructor for {kind} (#370)"),
                MemberRefNode.Kinds.Ctor, open, args);
            return;
        }
        var accessor = kind == "nullableHasValue" ? "get_HasValue" : "get_Value";
        var cands = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == accessor && m.GetParameters().Length == 0).ToList();
        node[kind == "nullableHasValue" ? "hasValueRef" : "valueRef"] = MemberRefJson(
            TryPickUnique(cands, new List<TypeNode>(), args)
                ?? throw new InvalidOperationException(
                    $"bir2cir: '{NullableFqn}.{accessor}' does not resolve to one declaration for {kind} (#370)"),
            MemberRefNode.Kinds.PropertyAccessor, open, args);
    }

    const string NullableFqn = "System.Nullable";

    /// <summary>
    /// The `Invoke` a Kotlin function-type value is called through.
    /// </summary>
    /// <remarks>
    /// One producer, one rule: a node that states a function type states which delegate it lowered to, and that
    /// delegate's Invoke is determined by the type alone. The emitter used to derive it from the value's emitted
    /// type at each site — thousands of operands from this one path, which is why the unit that matters is the
    /// producer and not the occurrence.
    /// </remarks>
    static void ResolveDelegateInvoke(JsonObject node, string typeKey)
    {
        if (node.ContainsKey("invokeRef")) return;
        // Most nodes state their own function type. An array initializer states the EXPRESSION whose value it
        // calls, and that expression is a node with a function type of its own — one level down, same fact.
        var stated = TypeJson.Read(node["funcType"]) ?? TypeJson.Read(node["clrType"])
            ?? TypeJson.Read((node[typeKey] as JsonObject)?["funcType"]) ?? TypeJson.Read(node[typeKey]);
        if (stated == null) return;
        // The document states the Kotlin function type; the lowering already turned it into the delegate the
        // value physically is. Ask that same lowering rather than re-deriving the delegate here.
        var physical = BirTypeLowering.LowerType(stated, refBuild: false, force: false, typeArg: false);
        if (physical is TypeNode.Fn fnNode)
            physical = BirTypeLowering.DelegateFqnOf(
                (TypeNode.Fn)BirTypeLowering.LowerFnDelegate(fnNode, refBuild: false, force: false));
        if (physical is not TypeNode.Fqn { Args: { Length: > 0 } } delegateFqn) return;
        var open = ResolveOwnerType(delegateFqn);
        if (open == null) return;
        var invoke = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Invoke").ToList();
        if (invoke.Count != 1) return;
        node["invokeRef"] = MemberRefJson(invoke[0], MemberRefNode.Kinds.Method, open, delegateFqn.Args);
    }

    /// <summary>
    /// The constructor a delegate CONSTRUCTION runs through: every delegate's `(object, native int)`.
    /// </summary>
    /// <remarks>
    /// Same producer family as the invoke, and the same rule: the node states the function type, the lowering
    /// says which delegate that is, and the constructor follows from the type. ECMA-335 II.14.6 fixes the
    /// signature; what varies — and what a reference has to state — is WHICH constructed delegate.
    /// </remarks>
    static void ResolveDelegateCtor(JsonObject node, string typeKey)
    {
        if (node.ContainsKey("delegateCtorRef")) return;
        // The node states its delegate under whichever key its kind uses; all five construction kinds carry one.
        var stated = TypeJson.Read(node["funcType"]) ?? TypeJson.Read(node["clrType"]) ?? TypeJson.Read(node[typeKey]);
        if (stated == null)
            throw new InvalidOperationException(
                $"bir2cir: a delegate construction states no function type under funcType/clrType/{typeKey} (#370)");
        // A function type stays an `fn` node through the general lowering — its DELEGATE form is a separate
        // step, and the same one the emitter's own mapping uses. Ask for it rather than assembling `Func`N` here.
        var physical = BirTypeLowering.LowerType(stated, refBuild: false, force: false, typeArg: false);
        if (physical is TypeNode.Fn fnNode)
            physical = BirTypeLowering.DelegateFqnOf(
                (TypeNode.Fn)BirTypeLowering.LowerFnDelegate(fnNode, refBuild: false, force: false));
        if (physical is not TypeNode.Fqn delegateFqn)
            throw new InvalidOperationException(
                $"bir2cir: a delegate construction lowers to {TypeNode.ToJson(physical)}, which is not a named type (#370)");
        var open = ResolveOwnerType(delegateFqn)
            ?? throw new InvalidOperationException(
                $"bir2cir: the delegate '{delegateFqn.Name}' does not resolve to a .NET type (#370)");
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == 2).ToList();
        var win = TryPickUniqueCtor(ctors,
            new List<TypeNode> { new TypeNode.Fqn("System.Object"), new TypeNode.Fqn("System.IntPtr") },
            delegateFqn.Args ?? Array.Empty<TypeNode>())
            ?? throw new InvalidOperationException(
                $"bir2cir: '{delegateFqn.Name}' has no unique (object, native int) constructor — "
                + $"{ctors.Count} two-argument candidate(s) (#370)");
        node["delegateCtorRef"] = MemberRefJson(win, MemberRefNode.Kinds.Ctor, open,
            delegateFqn.Args ?? Array.Empty<TypeNode>());
    }
}
