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
}
