// #370: the FIXED BCL members ilemit expands a Kotlin operation into.
//
// `enumValues()` becomes `Enum.GetValues`, string `+` becomes `String.Concat`, a runtime-classifier read becomes
// `Object.GetType`, an emitted enumerator's slots are `IEnumerator`'s. The source wrote none of them —
// but "did the source write it" is not the question. The question is whether ilemit encodes an EXTERNAL member as a
// CIL operand, and it does, in every one of these.
//
// None of them varies: no type arguments, no overload chosen per call site, the same member every time. So they do
// not need a carrier per node — one table per document, keyed by role, resolved here like everything else. The
// expansion stays in the emitter, which is a question about which layer owns the SHAPE; the member it emits arrives
// named, which is the question this issue is about. The two are separable, and separating them is what lets this
// land without waiting on the intrinsic-binding programme.

using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Toolchain;

static partial class ClrMemberResolution
{
    // role -> (owner, member, parameter types). A role names what the emitter needs it FOR, so a reader of the
    // emitter can find the entry without knowing the BCL signature by heart.
    static readonly (string Role, string Owner, string Name, string[] Params)[] WellKnownMembers =
    {
        ("String.ConcatArray",    "System.String",   "Concat",            new[] { "System.Object[]" }),
        ("Type.FromHandle",       "System.Type",     "GetTypeFromHandle", new[] { "System.RuntimeTypeHandle" }),
        ("Object.GetType",        "System.Object",   "GetType",           new string[0]),
        ("Object.ToString",       "System.Object",   "ToString",          new string[0]),
        ("Object.GetHashCode",    "System.Object",   "GetHashCode",       new string[0]),
        ("Object.Equals",         "System.Object",   "Equals",            new[] { "System.Object" }),
        ("Enum.GetValues",        "System.Enum",     "GetValues",         new[] { "System.Type" }),
        ("Enum.Parse",            "System.Enum",     "Parse",             new[] { "System.Type", "System.String" }),
        ("Enumerable.GetEnumerator", "System.Collections.IEnumerable", "GetEnumerator", new string[0]),
        ("Enumerator.MoveNext",   "System.Collections.IEnumerator", "MoveNext",    new string[0]),
        ("Enumerator.Current",    "System.Collections.IEnumerator", "get_Current", new string[0]),
        ("Enumerator.Reset",      "System.Collections.IEnumerator", "Reset",       new string[0]),
        ("Disposable.Dispose",    "System.IDisposable", "Dispose",        new string[0]),
        ("Array.IndexOf",         "System.Array",    "IndexOf",           new[] { "System.Array", "System.Object" }),
        ("Comparable.CompareTo",  "System.IComparable", "CompareTo",      new[] { "System.Object" }),
        // The OPEN declarations of the generic collection slots. Their instantiation is over a type parameter of
        // a type the emitter synthesizes, which no document can name — but the DECLARATION is fixed, and
        // anchoring a named declaration onto an owner is mechanical rather than a second choice of member.
        ("EnumeratorT.Current",   "System.Collections.Generic.IEnumerator", "get_Current", new string[0]),
        ("EnumerableT.GetEnumerator", "System.Collections.Generic.IEnumerable", "GetEnumerator", new string[0]),
        ("ReadOnlyCollectionT.Count", "System.Collections.Generic.IReadOnlyCollection", "get_Count", new string[0]),
        // The rest of the generic collection faces a Kotlin collection implements. All open declarations, all
        // fixed: the emitter anchors each onto the type it is building.
        ("ReadOnlyListT.Item",    "System.Collections.Generic.IReadOnlyList", "get_Item", new[] { "System.Int32" }),
        ("CollectionT.Count",     "System.Collections.Generic.ICollection", "get_Count",  new string[0]),
        ("CollectionT.IsReadOnly","System.Collections.Generic.ICollection", "get_IsReadOnly", new string[0]),
        ("CollectionT.Clear",     "System.Collections.Generic.ICollection", "Clear",      new string[0]),
        ("ListT.Item",            "System.Collections.Generic.IList", "get_Item",         new[] { "System.Int32" }),
        ("ListT.RemoveAt",        "System.Collections.Generic.IList", "RemoveAt",         new[] { "System.Int32" }),
    };

    // Constructors with a FIXED owner. `newobj` needs a token exactly as `call` does, so these are the same
    // question — the split that matters is whether the owner varies per site, not whether the member has a name.
    static readonly (string Role, string Owner, string[] Params)[] WellKnownCtors =
    {
        ("Object.ctor",                  "System.Object",                  new string[0]),
        ("NotSupportedException.ctor0",  "System.NotSupportedException",   new string[0]),
        ("IndexOutOfRangeException.ctor","System.IndexOutOfRangeException", new[] { "System.String" }),
        // The OPEN `Nullable<T>..ctor(T)`. A coercion computes the constructed owner from the slot it is filling,
        // which no document states — but the declaration does not vary, and anchoring is mechanical.
        ("NullableT.ctor",               "System.Nullable`1",              new[] { "!0" }),
        ("SpanT.ctorPointer",            "System.Span`1",                  new[] { "System.Void*", "System.Int32" }),
    };

    /// <summary>Stamp the fixed-member table on a document root. Every entry resolves or the build stops.</summary>
    public static void ResolveWellKnown(JsonNode root, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        if (root is not JsonObject document) return;
        var table = new JsonObject();
        foreach (var (role, owner, name, parameters) in WellKnownMembers)
        {
            var open = ResolveOwnerType(new TypeNode.Fqn(owner))
                ?? throw new InvalidOperationException(
                    $"bir2cir: the fixed-member table needs '{owner}' for role '{role}', which does not resolve "
                    + "to a .NET type (#370)");
            var wanted = parameters.Select(ParseWellKnownParam).ToList();
            var cands = open.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == name && m.GetParameters().Length == wanted.Count
                    && !m.IsGenericMethodDefinition).ToList();
            // A role that cannot be resolved is a producer defect. Skipping it quietly hands the emitter a
            // table with a hole in it, and the hole only shows up as a missing member at emit time — one layer
            // past the one that could say what went wrong.
            var win = TryPickUnique(cands, wanted, Array.Empty<TypeNode>())
                ?? throw new InvalidOperationException(
                    $"bir2cir: '{owner}.{name}({string.Join(", ", parameters)})' does not resolve to one "
                    + $"declaration for role '{role}' (#370)");
            table[role] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, OwnParameters(open));
        }
        foreach (var (role, owner, parameters) in WellKnownCtors)
        {
            // A name may state its arity — `System.Nullable`1` is a different type from the static `System.Nullable`
            // beside it, and the bare name resolves to the wrong one.
            var open = OwnerOfSpec(owner)
                ?? throw new InvalidOperationException(
                    $"bir2cir: the fixed-member table needs '{owner}' for role '{role}' (#370)");
            var wanted = parameters.Select(ParseWellKnownParam).ToList();
            var ownerArgs = OwnParameters(open);
            var cands = open.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(c => !c.IsStatic && c.GetParameters().Length == wanted.Count).ToList();
            var win = TryPickUniqueCtor(cands, wanted, Array.Empty<TypeNode>())
                ?? throw new InvalidOperationException(
                    $"bir2cir: '{owner}..ctor({string.Join(", ", parameters)})' does not resolve to one "
                    + $"declaration for role '{role}' (#370)");
            table[role] = MemberRefJson(win, MemberRefNode.Kinds.Ctor, open, ownerArgs);
        }
        document["wellKnownRefs"] = table;
    }

    // An OPEN generic declaration is named over its own parameters — `IEnumerator`1<!0>` — because that is what
    // the declaration IS. The emitter anchors it onto the owner it is building; the reference states the
    // declaration, exactly as it does everywhere else.
    static TypeNode[] OwnParameters(Type open) =>
        open.IsGenericTypeDefinition
            ? open.GetGenericArguments().Select(p => (TypeNode)new TypeNode.Tv("type", p.GenericParameterPosition)).ToArray()
            : Array.Empty<TypeNode>();

    // `Name`N` -> the arity-N definition; a bare name -> whatever that name alone resolves to.
    static Type OwnerOfSpec(string spec)
    {
        var tick = spec.IndexOf('`');
        return tick < 0
            ? ResolveOwnerType(new TypeNode.Fqn(spec))
            : RefDef(spec[..tick], int.Parse(spec[(tick + 1)..]));
    }

    static TypeNode ParseWellKnownParam(string spec) =>
        spec.StartsWith("!", StringComparison.Ordinal) ? new TypeNode.Tv("type", int.Parse(spec[1..])) :
        spec.EndsWith("[]", StringComparison.Ordinal)
            ? new TypeNode.Array(new TypeNode.Fqn(spec[..^2]))
            : new TypeNode.Fqn(spec);
}

static partial class ClrMemberResolution
{
    /// <summary>
    /// Every slot of every EXTERNAL interface a type declares, named on the type that implements them.
    /// </summary>
    /// <remarks>
    /// The emitter wires a MethodImpl for each slot of each interface it does not itself emit, and it enumerates
    /// those slots off the interface by reflection — the last external member reaching an operand unnamed. The
    /// existing `clrInterfaceImpls` descriptors do not cover this: individual bridge passes author them for the
    /// slots THOSE passes create, while an implicitly implemented slot has no descriptor at all.
    ///
    /// Which BODY fills each slot stays the emitter's question — that member belongs to the assembly being
    /// built, which is the local axis. What arrives named is the slot the MethodImpl points AT.
    /// </remarks>
    public static void ResolveInterfaceSlots(JsonNode root, IEnumerable<JsonNode> moduleRoots,
        ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var all = moduleRoots.Where(moduleRoot => moduleRoot != null).ToList();
        var defs = SupertypeGraph.Collect(all);
        WalkTypesForSlots(root, defs);
    }

    /// <summary>
    /// Name an external declaration reached through a type emitted by this compilation.
    /// </summary>
    /// <remarks>
    /// A call can quite correctly state a local receiver owner while its selected declaration lives on an
    /// inherited external interface (for example, a local interface extending one with a default method).  The
    /// early referenced-call pass must skip that owner because an older shipped copy of a local type is never an
    /// authority.  Once all physically-lowered local declarations are available together, the shared supertype
    /// graph can prove whether a local declaration answers the call and, only when none does, expose the external
    /// declaration.  This keeps the receiver local while carrying the actual declarer to ilemit.
    /// </remarks>
    public static void ResolveInheritedExternalCalls(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var all = roots.Where(root => root != null).ToList();
        var defs = SupertypeGraph.Collect(all);
        foreach (var root in all) WalkInheritedExternalCalls(root, defs, refs);
    }

    static void WalkInheritedExternalCalls(JsonNode node,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                    if (kv.Value != null) WalkInheritedExternalCalls(kv.Value, defs, refs);
                ResolveInheritedExternalCall(obj, defs, refs);
                break;
            case JsonArray arr:
                foreach (var item in arr.ToList())
                    if (item != null) WalkInheritedExternalCalls(item, defs, refs);
                break;
        }
    }

    static void ResolveInheritedExternalCall(JsonObject call,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs, ReferenceMetadataIndex refs)
    {
        if ((call["k"] as JsonValue)?.GetValue<string>() != "callInstance"
            || call.ContainsKey("memberRef")
            || TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn owner
            || !defs.TryGetValue(owner.Name, out var ownerDef)
            || (call["method"] as JsonValue)?.GetValue<string>() is not { } name
            || call["sig"] is not JsonArray sig)
            return;

        var callSig = sig.Select(TypeJson.Read).ToArray();
        if (callSig.Any(type => type == null)) return;
        var methodArity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var ownerArgs = owner.Args ?? (ownerDef.Arity == 0
            ? Array.Empty<TypeNode>()
            : Enumerable.Range(0, ownerDef.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        // Reachable() starts in a definition's own type-parameter frame.  Calls, however, can name a constructed
        // local owner (`Local<string>`), so project its first edges into the call's frame before walking onward.
        var projectedOwner = new SupertypeGraph.Def
        {
            Name = ownerDef.Name,
            Kind = ownerDef.Kind,
            Arity = ownerDef.Arity,
            Base = ownerDef.Base == null ? null
                : SupertypeGraph.SubstOwnerTvs(ownerDef.Base, ownerArgs) as TypeNode.Fqn,
            Interfaces = ownerDef.Interfaces.Select(iface =>
                (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(iface, ownerArgs)).ToArray(),
            Node = ownerDef.Node,
            Methods = ownerDef.Methods,
        };
        var reachable = SupertypeGraph.Reachable(projectedOwner, defs, refs).ToList();

        // A declaration in this compilation owns the identity.  Never let a reflected base with the same shape
        // displace it merely because the call's local owner does not declare the method itself.
        if (LocalDeclares(ownerDef, ownerArgs, name, methodArity, callSig)
            || reachable.Any(entry => defs.TryGetValue(entry.spec.Name, out var local)
                && LocalDeclares(local, entry.spec.Args, name, methodArity, callSig)))
            return;

        var answers = new List<(MethodInfo Declaration, JsonNode Reference, TypeNode[] OwnerArgs)>();
        foreach (var (spec, _) in reachable)
        {
            if (defs.ContainsKey(spec.Name)) continue;
            // Resolve against this exact constructed graph edge.  The reference index's ordinary call lookup is
            // receiver-oriented and may legitimately answer from an implemented interface; here the graph has
            // already identified each declaration owner, so declared-only reflection avoids counting it twice.
            var open = ResolveOwnerType(spec) ?? refs.PhysicalTypeNamed(spec.Name);
            if (open == null) continue;
            var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name
                    && method.GetGenericArguments().Length == methodArity
                    && method.GetParameters().Length == callSig.Length
                    && IsPublicOrProtected(method))
                .ToList();
            var declaration = TryPickUnique(candidates, callSig.ToList(), spec.Args ?? Array.Empty<TypeNode>());
            if (declaration == null) continue;
            var reference = MemberRefJson(declaration, MemberRefNode.Kinds.Method, open,
                spec.Args ?? Array.Empty<TypeNode>());
            answers.Add((declaration, reference, spec.Args ?? Array.Empty<TypeNode>()));
        }
        // A derived interface can redeclare a base slot with the same source signature.  That is not ambiguity:
        // ordinary member lookup selects the most-derived declaration.  Apply the same existing metadata rule here,
        // before comparing the complete scalar references; unrelated declarations remain an unresolved ambiguity.
        var mostDerived = MostDerived(answers.Select(answer => answer.Declaration)
            .GroupBy(method => (method.Module, method.MetadataToken)).Select(group => group.First()).ToList());
        if (mostDerived.Count == 1)
        {
            var selected = mostDerived[0];
            answers = answers.Where(answer => answer.Declaration.Module == selected.Module
                && answer.Declaration.MetadataToken == selected.MetadataToken).ToList();
        }
        var distinctAnswers = answers.Select(answer => answer.Reference)
            .GroupBy(reference => reference.ToJsonString(), StringComparer.Ordinal)
            .Select(group => group.First()).ToList();
        // Multiple distinct external declarations are a semantic dispatch question, not an emitter choice.  The
        // upstream declaration identity must distinguish that case; leave it unstamped so the fail-closed audit
        // reports the unresolved site instead of picking by graph order.
        if (distinctAnswers.Count != 1) return;
        call["memberRef"] = distinctAnswers[0];
        var chosen = answers[0];
        StampDelegateArgumentTargets(call, chosen.Declaration, chosen.OwnerArgs);
    }

    static bool LocalDeclares(SupertypeGraph.Def def, TypeNode[] ownerArgs, string name, int methodArity,
        IReadOnlyList<TypeNode> callSig)
    {
        var args = ownerArgs ?? (def.Arity == 0
            ? Array.Empty<TypeNode>()
            : Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        foreach (var method in def.Methods.OfType<JsonObject>())
        {
            if ((method["static"] as JsonValue)?.TryGetValue<bool>(out var isStatic) == true && isStatic) continue;
            if ((method["name"] as JsonValue)?.GetValue<string>() != name
                || ((method["typeParams"] as JsonArray)?.Count ?? 0) != methodArity
                || method["params"] is not JsonArray parameters
                || parameters.Count != callSig.Count)
                continue;
            var declared = parameters.OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"]))
                .ToArray();
            if (declared.Length != callSig.Count || declared.Any(type => type == null)) continue;
            var wanted = callSig.Select(SupertypeGraph.TypeKey);
            if (declared.Select(SupertypeGraph.TypeKey).SequenceEqual(wanted)
                || declared.Select(type => SupertypeGraph.SubstOwnerTvs(type, args))
                    .Select(SupertypeGraph.TypeKey).SequenceEqual(callSig.Select(SupertypeGraph.TypeKey)))
                return true;
        }
        return false;
    }

    static void WalkTypesForSlots(JsonNode node,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["interfaces"] is JsonArray ifaces && obj["name"] is JsonValue)
                    StampInterfaceSlots(obj, ifaces, defs);
                foreach (var kv in obj) if (kv.Value != null) WalkTypesForSlots(kv.Value, defs);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) WalkTypesForSlots(it, defs);
                break;
        }
    }

    static void StampInterfaceSlots(JsonObject type, JsonArray ifaces,
        IReadOnlyDictionary<string, SupertypeGraph.Def> defs)
    {
        if (type.ContainsKey("interfaceSlotRefs")) return;
        var slotSets = new JsonArray();
        var owners = new List<TypeNode.Fqn>();
        // Late synthetic types are not necessarily present in the module definition index. Their direct physical
        // interface edges are nevertheless authoritative CIR facts and must always seed the carrier walk.
        owners.AddRange(ifaces.Select(TypeJson.Read).OfType<TypeNode.Fqn>());
        if ((type["name"] as JsonValue)?.GetValue<string>() is { } typeName
            && defs.TryGetValue(typeName, out var def))
        {
            // ilemit also visits interfaces inherited through the emitted base-class chain. Mirror that finite local
            // walk here, keeping every type argument in THIS type's frame. Do not traverse arbitrary referenced
            // supertypes: the emitter never reconstructs that graph, and self-referential metadata can otherwise grow
            // constructed specs without bound.
            var baseSpec = def.Base;
            var baseSeen = new HashSet<string>(StringComparer.Ordinal);
            while (baseSpec != null && baseSeen.Add(SupertypeGraph.TypeKey(baseSpec))
                && defs.TryGetValue(baseSpec.Name, out var baseDef) && baseDef.Kind != "interface")
            {
                var args = SupertypeGraph.EffectiveArgs(baseSpec, baseDef.Arity);
                if (args == null) break;
                owners.AddRange(baseDef.Interfaces.Select(parent =>
                    (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(parent, args)));
                baseSpec = baseDef.Base == null ? null
                    : (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(baseDef.Base, args);
            }
        }
        // A bridge descriptor can name a slot declared by a BASE interface which is not one of this type's direct
        // interface edges. Reflection's Interface.GetMethods() does not include inherited slots, while ilemit seeds
        // each descriptor owner into its worklist. Carry those exact external owners too; otherwise the direct
        // interface's own slots are named but the inherited MethodImpl silently falls back to reflection.
        if (type["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                if (method["clrInterfaceImpls"] is JsonArray implementations)
                    foreach (var implementation in implementations.OfType<JsonObject>())
                        if (TypeJson.Read(implementation["owner"]) is TypeNode.Fqn owner)
                            owners.Add(owner);

        // The shared module-wide supertype walk above covers direct interfaces, local interface chains, interfaces
        // inherited through local base classes, and referenced base-interface chains in the implementing type's
        // parameter frame. Descriptor-only owners are appended separately because a representation carrier may have
        // replaced their source-level edge.
        var externalOwners = new List<TypeNode.Fqn>();
        var pending = new Queue<TypeNode.Fqn>(owners);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var iface = pending.Dequeue();
            if (!seen.Add(SupertypeGraph.TypeKey(iface))) continue;
            if (defs.TryGetValue(iface.Name, out var localIface) && localIface.Kind == "interface")
            {
                // A generated interface can be present as an identical local template while the runtime universe
                // already owns its canonical CLR identity. ilemit consequently links that identity externally. Do
                // not let the template's presence in the module graph misclassify the operand as local: the shipped
                // declaration is the authority for its slot tokens. This is structural (generated interface + exact
                // physical type), not a synthetic-name allowlist.
                if ((localIface.Node?["generated"] as JsonValue)?.TryGetValue<bool>(out var generated) == true
                    && generated
                    && ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(iface.Name)
                    && _refs.PhysicalTypeNamed(iface.Name, localIface.Arity) != null)
                {
                    externalOwners.Add(iface);
                    continue;
                }
                var args = SupertypeGraph.EffectiveArgs(iface, localIface.Arity);
                if (args == null) continue;
                foreach (var parent in localIface.Interfaces)
                    pending.Enqueue((TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(parent, args));
                continue;
            }
            externalOwners.Add(iface);
        }

        foreach (var iface in externalOwners)
        {
            // A canonical synthetic interface is spelled as a bare name and lives ONLY in the assembly that
            // ships it: the reference twin describes the Kotlin surface and has no name for it, so no amount
            // of reference resolution will ever find one. Read it from the shipped twin, as the emitter does.
            var open = ResolveOwnerType(iface) ?? _refs.PhysicalTypeNamed(iface.Name);
            // A late synthetic interface can be emitted by this compilation without appearing in the source-level
            // module index. It has no external declaration to name. A genuinely external unresolved owner cannot be
            // consumed silently: ilemit requires this carrier at the external wiring site and fails closed there.
            if (open == null) continue;
            var args = iface.Args ?? Array.Empty<TypeNode>();
            var slots = new JsonArray();
            foreach (var m in open.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                slots.Add(MemberRefJson(m, MemberRefNode.Kinds.Method, open, args));
            }
            var assembly = open.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(assembly))
                throw new InvalidOperationException(
                    $"bir2cir: external interface '{iface.Name}' has no assembly identity (#370)");
            slotSets.Add(new JsonObject
            {
                ["owner"] = TypeJson.Write(iface),
                ["assembly"] = ManagedReferenceCatalog.PhysicalAssemblyName(assembly),
                ["slots"] = slots,
            });
        }
        // Presence is the contract, including the exact answer "this external interface declares no slots" (for
        // example a marker interface or an interface whose slots are all inherited). Without the empty marker,
        // ilemit cannot distinguish that answer from a producer omission without reconstructing the hierarchy.
        if (slotSets.Count > 0) type["interfaceSlotRefs"] = slotSets;
        StampEnumeratorAdapterCtor(type);
    }

    // The reverse enumerator bridge wraps this type's iterator in the shipped adapter. Its constructor is external
    // exactly when this compilation does NOT emit the adapter, which is the ordinary local/external split: a stdlib
    // build emits it and uses its own ConstructorBuilder, an app build links the shipped one and so must be given
    // its identity. The trigger is the same structural fact the bridge itself keys on — an `iterator` bridge role —
    // and the declaration is selected by the signature the local emission builds, never by position.
    static void StampEnumeratorAdapterCtor(JsonObject type)
    {
        if (type.ContainsKey("enumeratorAdapterCtorRef")) return;
        if (type["methods"] is not JsonArray methods
            || methods.OfType<JsonObject>().FirstOrDefault(m =>
                (m["clrBridgeRole"] as JsonValue)?.GetValue<string>() == "iterator") is not JsonObject iter)
            return;
        // The adapter is instantiated with the element the bridge actually wraps, which the iterator's own return
        // type states: Iterator<E> -> adapter<E>. Reading it from the wrapped member rather than from a supertype
        // list keeps the two in step whatever collection interface the type reached the bridge through.
        if (TypeJson.Read(iter["ret"]) is not TypeNode.Fqn iterRet
            || iterRet.Args is not { Length: 1 } elem)
            return;
        var open = _refs.PhysicalTypeNamed("dotkt$EnumeratorOverKotlinIterator", 1);
        if (open == null) return;   // this compilation emits the adapter: the local branch needs no reference
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters() is { Length: 1 } ps
                && ps[0].ParameterType.IsGenericType
                && ps[0].ParameterType.GetGenericTypeDefinition().FullName == "kotlin.collections.Iterator`1")
            .ToList();
        if (ctors.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: the shipped enumerator adapter declares {ctors.Count} constructors taking "
                + "kotlin.collections.Iterator`1, so the reverse bridge cannot be given one identity (#370)");
        type["enumeratorAdapterCtorRef"] = MemberRefJson(ctors[0], MemberRefNode.Kinds.Ctor, open, elem);
    }
}
