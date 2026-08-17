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
// That includes the TYPE the literal constructs. A reference states its member's declaring type together with
// the instantiation the use site anchors it on, so the constructor named below already says `List<int>` —
// the emitter reads the constructed type back off it and names no BCL type of its own (#400).
//
// The two spellings agree because this pass runs on the ALREADY-LOWERED tree: the reference's declaring
// arguments are the node's own `elem`/`keyType`/`valType` put back through physical type lowering, which is
// idempotent by then. That fixed point is what makes the constructor's declaring type the same type the emitter
// used to build from the node — it is a property of WHERE this pass sits, not a coincidence of the two paths.
//
// The signatures below are the declarations' own — `Add(!0)`, `set_Item(!0, !1)` — matched structurally like
// every other member, so a BCL that grows an overload cannot silently change which one is meant.
//
// A node whose element/key/value type cannot be read is a node this pass cannot complete. It fails here, where
// the missing fact is, rather than travelling to the emitter as a construction with nothing to construct.

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
        if (args.Any(a => a == null))
            throw new InvalidOperationException(
                $"bir2cir: {kind} states no readable "
                + (kind == "newMap" ? "keyType/valType" : "elem")
                + " and so names no constructed type (#400)");
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
        if (TypeJson.Read(node["elem"]) is not TypeNode elem)
            throw new InvalidOperationException(
                "bir2cir: spreadConcat states no readable elem and so names no accumulator type (#400)");
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
    /// The enumerator protocol an inlined `for` walks. Both arms are named, and WHICH arm the emitter takes is
    /// deliberately not decided here: the predicate is whether the element type maps to a Reflection.Emit builder
    /// in the emitting frame, and that is not a function of anything this layer can see. A `tv` does not settle it
    /// (the emitter answers System.Object for a type-scope tv with no parameter in scope), and the emitter builds
    /// types this pass never sees at all — closures, per-arity delegate adapters, dotkt$ synthetics. So both arms
    /// are stated and the emitter picks between two members already named; it takes the enumerator's own type from
    /// whichever GetEnumerator it emits, so both arms must be present on every node (#400).
    /// </summary>
    static void ResolveForEachInline(JsonObject node)
    {
        if (node.ContainsKey("moveNextRef")) return;
        if (TypeJson.Read(node["elem"]) is not TypeNode elem)
            throw new InvalidOperationException(
                "bir2cir: forEachInline states no readable elem and so names no enumerator protocol (#400)");
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
    /// The static field a `staticField` node reads, named here rather than re-found by name downstream (#370).
    /// </summary>
    /// <remarks>
    /// Every Kotlin `object` reaches its instance through this node, so no singleton is a special case —
    /// including the `kotlin.Unit` instance a void-to-value delegate adapter returns, which reaches this
    /// resolver through the ordinary `staticField` node its authored body carries. An owner this compilation
    /// emits does not resolve and correctly carries nothing: that is the local axis, which is #395's subject
    /// and not this one's.
    /// </remarks>
    static void ResolveStaticField(JsonObject node)
    {
        if (node.ContainsKey("fieldRef")) return;
        // `staticField`/`staticFieldSet` use ownerType; the CLR-vocabulary `clrStaticField` uses type.
        // They are the same physical operation after this point and must receive the same field identity.
        if (ReadOwnerNode(node["ownerType"] ?? node["type"]) is not TypeNode.Fqn owner
            || (node["name"] as JsonValue)?.GetValue<string>() is not string name)
            return;
        // A type this compilation emits keeps the local axis (#395) and must not be given an external identity,
        // even though the reference surface can answer for it: the stdlib runtime build compiles against a
        // PREVIOUS build of the assembly it is producing, so kotlin.text.HexFormat+$Companion resolved there and
        // was handed a reference to itself that the emitter's own universe then could not contain.
        if (_localTypes.Contains(owner.Name)) return;
        var open = ResolveOwnerType(owner);
        if (open == null) return;
        var field = open.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null && !(field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)) field = null;
        if (field == null) return;
        node["fieldRef"] = FieldRefJson(field, open, owner.Args ?? Array.Empty<TypeNode>());
    }

    // `lateinitGet` is a direct load of the property storage followed by the null check encoded
    // by that CIR node.  When an inline/reference payload makes that storage external, name the
    // field just as an ordinary static/instance field operand does; ilemit must not rediscover it.
    static void ResolveLateinitField(JsonObject node)
    {
        if (node.ContainsKey("fieldRef")) return;
        if (ReadOwnerNode(node["ownerType"]) is not TypeNode.Fqn owner
            || (node["name"] as JsonValue)?.GetValue<string>() is not string name)
            return;
        if (_localTypes.Contains(owner.Name)) return;
        var open = ResolveOwnerType(owner);
        if (open == null) return;
        var isStatic = node["static"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        var flags = BindingFlags.Public | BindingFlags.NonPublic
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy;
        var field = open.GetField(name, flags);
        if (field != null && !(field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)) field = null;
        if (field == null)
            throw new InvalidOperationException(
                $"bir2cir: external lateinit storage '{owner.Name}.{name}' does not resolve to a field (#370)");
        node["fieldRef"] = FieldRefJson(field, open, owner.Args ?? Array.Empty<TypeNode>());
    }

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
        var kind = (node["kind"] as JsonValue)?.GetValue<string>();
        // A raise emits a callvirt to the concrete event delegate's Invoke.  It is the same
        // operation as every other delegate invocation, so it carries the same resolved
        // declaration instead of asking ilemit to recover it from the delegate type.
        if (kind == "raise")
        {
            ResolveDelegateInvoke(node, "delegateType");
            return;
        }
        if (node.ContainsKey("combineRef")) return;
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
            ?? TypeJson.Read((node[typeKey] as JsonObject)?["funcType"])
            ?? TypeJson.Read((node[typeKey] as JsonObject)?["sty"])
            ?? TypeJson.Read(node[typeKey]);
        if (stated == null)
            throw new InvalidOperationException(
                $"bir2cir: a function-type call states no type under funcType/clrType/{typeKey} (#370)");
        ResolveDelegateInvoke(node, stated);
    }

    // Used by a semantic producer that still has the handler's function type before transient `sty`
    // annotations are consumed.  The result is the same ordinary invokeRef carrier used by all
    // delegate-call nodes; no event-specific identity dialect is introduced.
    internal static void ResolveDelegateInvoke(
        JsonObject node, JsonNode stated, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypes)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _localTypes = localTypes ?? new HashSet<string>();
        ResolveDelegateInvoke(node, TypeJson.Read(stated)
            ?? throw new InvalidOperationException("bir2cir: a delegate invocation has no readable handler type (#370)"));
    }

    static void ResolveDelegateInvoke(JsonObject node, TypeNode stated)
    {
        if (node.ContainsKey("invokeRef")) return;
        // The document states the Kotlin function type; the lowering already turned it into the delegate the
        // value physically is. Ask that same lowering rather than re-deriving the delegate here.
        var physical = BirTypeLowering.LowerType(stated, refBuild: false, force: false, typeArg: false);
        if (physical is TypeNode.Fn fnNode)
            physical = BirTypeLowering.DelegateFqnOf(
                (TypeNode.Fn)BirTypeLowering.LowerFnDelegate(fnNode, refBuild: false, force: false));
        if (physical is not TypeNode.Fqn delegateFqn)
            throw new InvalidOperationException(
                $"bir2cir: a function-type call lowers to {TypeNode.ToJson(physical)}, which is not a named type (#370)");
        var open = ResolveOwnerType(delegateFqn)
            ?? throw new InvalidOperationException(
                $"bir2cir: the delegate '{delegateFqn.Name}' does not resolve to a .NET type (#370)");
        var invoke = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Invoke").ToList();
        if (invoke.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: '{delegateFqn.Name}' has {invoke.Count} Invoke declarations, not one (#370)");
        node["invokeRef"] = MemberRefJson(invoke[0], MemberRefNode.Kinds.Method, open, delegateFqn.Args,
            ownerArgumentsAreMethodSlots: IsFunctionShape(stated));
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
        ResolveDelegateCtor(node, stated);
    }

    static void ResolveDelegateCtor(JsonObject node, TypeNode stated, string carrier = "delegateCtorRef")
    {
        if (node.ContainsKey(carrier)) return;
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
        node[carrier] = MemberRefJson(win, MemberRefNode.Kinds.Ctor, open,
            delegateFqn.Args ?? Array.Empty<TypeNode>(), ownerArgumentsAreMethodSlots: IsFunctionShape(stated));
    }

    static bool IsFunctionShape(TypeNode type) => type switch
    {
        TypeNode.Fn => true,
        TypeNode.Nullable n => IsFunctionShape(n.Of),
        TypeNode.Oblivious o => IsFunctionShape(o.Of),
        _ => false,
    };

    /// <summary>
    /// The interface slot a constrained call dispatches through.
    /// </summary>
    /// <remarks>
    /// The node states the interface and the member; the emitter was looking the member up on the interface by
    /// name. Same fact, resolved where resolution belongs.
    /// </remarks>
    static void ResolveConstrainedCall(JsonObject node)
    {
        if (node.ContainsKey("memberRef")) return;
        if (TypeJson.Read(node["iface"]) is not TypeNode.Fqn iface) return;
        if ((node["method"] as JsonValue)?.GetValue<string>() is not string name) return;
        var open = ResolveOwnerType(iface);
        if (open == null) return;
        // constrainedCall has two dialects: the historical compareTo form carries one `arg`, while the
        // general form carries `args`.  Count the declaration operands from the carrier itself; treating every
        // general-form call as parameterless silently left its member unresolved.
        var argCount = node["args"] is JsonArray args
            ? args.Count
            : node["arg"] is JsonObject ? 1 : 0;
        var methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        var cands = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == name && m.GetParameters().Length == argCount
                && (m.IsGenericMethodDefinition
                    ? m.GetGenericArguments().Length == methodArity
                    : methodArity == 0))
            .ToList();
        var sig = (node["sig"] as JsonArray)?.Select(TypeJson.Read).ToList();
        var win = sig == null
            ? (cands.Count == 1 ? cands[0] : null)
            : TryPickUnique(cands, sig, iface.Args ?? Array.Empty<TypeNode>());
        if (win == null)
            throw new InvalidOperationException(
                $"bir2cir: constrained call '{iface.Name}.{name}' does not resolve to one declared signature (#370)");
        node["memberRef"] = MemberRefJson(win, MemberRefNode.Kinds.Method, open,
            iface.Args ?? Array.Empty<TypeNode>());
        StampDelegateArgumentTargets(node, win, iface.Args ?? Array.Empty<TypeNode>());
        StampResolvedMemberReturn(node, win.ReturnType);
    }
}
