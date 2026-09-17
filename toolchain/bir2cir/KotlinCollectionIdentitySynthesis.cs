using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// NOMINAL KOTLIN COLLECTION IDENTITIES alongside the operational BCL faces.
//
// Collection/Set currently lower to the same IReadOnlyCollection<E> face, and MutableCollection/MutableSet to the
// same ICollection<E> face. Those aliases are useful operational ABIs, but cannot answer the Kotlin classifier
// question: a user `Collection` and a user `Set` become indistinguishable. Attach an empty compiler-owned identity
// interface to every emitted Kotlin implementation while the Kotlin supertype graph is still available. BCL-backed
// values cannot be modified, so StarProjectionLowering recognizes their existing generic CLR faces separately.
//
// The identities form the Kotlin relation themselves. A MutableSet identity is also a Set and Collection identity;
// a Set identity is also a Collection identity. Set membership and Collection mutability are independent:
// implementing Set and MutableCollection does not imply implementing MutableSet.
static class KotlinCollectionIdentitySynthesis
{
    const string Iterable = "kotlin.collections.Iterable";
    const string MutableIterable = "kotlin.collections.MutableIterable";
    const string Collection = "kotlin.collections.Collection";
    const string MutableCollection = "kotlin.collections.MutableCollection";
    const string List = "kotlin.collections.List";
    const string MutableList = "kotlin.collections.MutableList";
    const string Set = "kotlin.collections.Set";
    const string MutableSet = "kotlin.collections.MutableSet";

    const string CollectionIdentity = "DotKt.Runtime.CompilerServices.KotlinCollectionClassifier";
    const string MutableCollectionIdentity = "DotKt.Runtime.CompilerServices.KotlinMutableCollectionClassifier";
    const string ListIdentity = "DotKt.Runtime.CompilerServices.KotlinListClassifier";
    const string MutableListIdentity = "DotKt.Runtime.CompilerServices.KotlinMutableListClassifier";
    const string SetIdentity = "DotKt.Runtime.CompilerServices.KotlinSetClassifier";
    const string MutableSetIdentity = "DotKt.Runtime.CompilerServices.KotlinMutableSetClassifier";
    const string IterableIdentity = "DotKt.Runtime.CompilerServices.KotlinIterableClassifier";
    const string MutableIterableIdentity = "DotKt.Runtime.CompilerServices.KotlinMutableIterableClassifier";

    sealed class Def
    {
        public JsonObject Node;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        var defs = new Dictionary<string, Def>(StringComparer.Ordinal);
        foreach (var root in roots) Collect(root, defs);
        foreach (var def in defs.Values) Apply(def, defs);
    }

    static void Collect(JsonNode root, Dictionary<string, Def> defs)
    {
        if (root is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is string name)
                defs[name] = new Def
                {
                    Node = type,
                    Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                    Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                        .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                };
            Collect(type, defs);
        }
    }

    static void Apply(Def def, IReadOnlyDictionary<string, Def> defs)
    {
        var names = SupertypeNames(def, defs);
        var identities = new List<string>();
        if (names.Contains(MutableSet)) identities.Add(MutableSetIdentity);
        else if (names.Contains(Set)) identities.Add(SetIdentity);
        if (names.Contains(MutableList)) identities.Add(MutableListIdentity);
        else if (names.Contains(List)) identities.Add(ListIdentity);
        if (names.Contains(MutableCollection) && !names.Overlaps(new[] { MutableSet, MutableList }))
            identities.Add(MutableCollectionIdentity);
        if (identities.Count == 0 && names.Contains(Collection)) identities.Add(CollectionIdentity);
        // Mutable iteration is independent of collection mutation (e.g. Collection + MutableIterable).
        if (names.Contains(MutableIterable) && !names.Overlaps(new[] { MutableCollection, MutableSet, MutableList }))
            identities.Add(MutableIterableIdentity);
        if (identities.Count == 0 && names.Contains(Iterable)) identities.Add(IterableIdentity);
        if (identities.Count == 0) return;

        if (def.Node["interfaces"] is not JsonArray interfaces)
        {
            interfaces = new JsonArray();
            def.Node["interfaces"] = interfaces;
        }
        foreach (var identity in identities)
            if (!interfaces.Any(i => TypeJson.Read(i) is TypeNode.Fqn f && f.Name == identity))
                interfaces.Add(TypeJson.Fqn(identity));
    }

    static HashSet<string> SupertypeNames(Def start, IReadOnlyDictionary<string, Def> defs)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<TypeNode.Fqn>();
        if (start.Base != null) pending.Enqueue(start.Base);
        foreach (var face in start.Interfaces) pending.Enqueue(face);
        while (pending.Count != 0)
        {
            var face = pending.Dequeue();
            if (!names.Add(face.Name) || !defs.TryGetValue(face.Name, out var local)) continue;
            if (local.Base != null) pending.Enqueue(local.Base);
            foreach (var inherited in local.Interfaces) pending.Enqueue(inherited);
        }
        return names;
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
