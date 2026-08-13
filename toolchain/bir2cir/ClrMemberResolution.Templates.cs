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
}
