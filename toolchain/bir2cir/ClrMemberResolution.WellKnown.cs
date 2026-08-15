// #370: the FIXED BCL members ilemit expands a Kotlin operation into.
//
// `enumValues()` becomes `Enum.GetValues`, string `+` becomes `String.Concat`, a `clrDynInstance` dispatch becomes
// `GetType`/`GetMethod`/`Invoke`, an emitted enumerator's slots are `IEnumerator`'s. The source wrote none of them —
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
        ("Type.GetMethod",        "System.Type",     "GetMethod",         new[] { "System.String" }),
        ("MethodInfo.Invoke",     "System.Reflection.MethodBase", "Invoke", new[] { "System.Object", "System.Object[]" }),
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
        ("NotSupportedException.ctor",   "System.NotSupportedException",   new[] { "System.String" }),
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
    public static void ResolveInterfaceSlots(JsonNode root, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        WalkTypesForSlots(root);
    }

    static void WalkTypesForSlots(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["interfaces"] is JsonArray ifaces && obj["name"] is JsonValue) StampInterfaceSlots(obj, ifaces);
                foreach (var kv in obj) if (kv.Value != null) WalkTypesForSlots(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) WalkTypesForSlots(it);
                break;
        }
    }

    static void StampInterfaceSlots(JsonObject type, JsonArray ifaces)
    {
        if (type.ContainsKey("interfaceSlotRefs")) return;
        var slots = new JsonArray();
        foreach (var entry in ifaces)
        {
            if (TypeJson.Read(entry) is not TypeNode.Fqn iface) continue;
            // A canonical synthetic interface is spelled as a bare name and lives ONLY in the assembly that
            // ships it: the reference twin describes the Kotlin surface and has no name for it, so no amount
            // of reference resolution will ever find one. Read it from the shipped twin, as the emitter does.
            var open = ResolveOwnerType(iface) ?? _refs.PhysicalTypeNamed(iface.Name);
            if (open == null) continue;   // an interface this compilation emits has no reference to make
            var args = iface.Args ?? Array.Empty<TypeNode>();
            foreach (var m in open.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsGenericMethodDefinition) continue;
                slots.Add(MemberRefJson(m, MemberRefNode.Kinds.Method, open, args));
            }
        }
        if (slots.Count > 0) type["interfaceSlotRefs"] = slots;
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
            || !methods.OfType<JsonObject>().Any(m =>
                (m["clrBridgeRole"] as JsonValue)?.GetValue<string>() == "iterator"))
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
        type["enumeratorAdapterCtorRef"] = MemberRefJson(ctors[0], MemberRefNode.Kinds.Ctor, open, Array.Empty<TypeNode>());
    }
}
