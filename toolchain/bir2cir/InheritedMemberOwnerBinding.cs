using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Bind an inherited instance member to its CONSTRUCTED declaring owner.
//
// BIR deliberately records the Kotlin receiver owner.  For example, a call through
// `Derived<T>` to `Base<T>.m()` remains ownerType=Derived<T>; that is the faithful
// Kotlin-IR projection and must not be polluted with a CLR MemberRef decision in kotc.
// A CLR MemberRef, however, cannot name the open `Base<>.m` when the call is made on
// `Base<T>`: doing so produces "containing type is not fully instantiated" at JIT time.
//
// The old ilemit path discovered the base declaration while emitting and then tried to
// reconstruct its generic instantiation.  Besides making emission semantic, that loses
// the substitution on generic-method calls.  This pass performs the general hierarchy
// substitution in bir2cir and rewrites ownerType to the exact constructed declaration
// (`Base<T>`).  ilemit subsequently links that owner one-to-one.
//
// Resolution is intentionally conservative:
//   * a kotc `overrides` fact wins when it names one unambiguous reachable declaration;
//   * otherwise an exact name/generic-arity/parameter-signature match is required at the
//     nearest hierarchy depth;
//   * ambiguity or incomplete type information leaves the node untouched (never guesses
//     an overload).
// No library/member FQNs are special-cased.
static class InheritedMemberOwnerBinding
{
    sealed class TypeDef
    {
        public string Name;
        public string Kind;
        public int TypeParamCount;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public JsonArray Methods;
    }

    readonly record struct Reachable(TypeNode.Fqn Type, int Depth);

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var types = CollectTypes(rootList);
        foreach (var root in rootList) Walk(root, types, refs);
    }

    static Dictionary<string, TypeDef> CollectTypes(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, result);
        return result;
    }

    static void CollectFrom(JsonNode node, Dictionary<string, TypeDef> result)
    {
        if (node is not JsonObject obj) return;
        if (obj["types"] is not JsonArray arr) return;
        foreach (var item in arr)
        {
            if (item is not JsonObject type || Str(type["name"]) is not string name) continue;
            result[name] = new TypeDef
            {
                Name = name,
                Kind = Str(type["kind"]),
                TypeParamCount = (type["typeParams"] as JsonArray)?.Count ?? 0,
                Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                    .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                Methods = type["methods"] as JsonArray,
            };
            CollectFrom(type, result);
        }
    }

    static void Walk(JsonNode node, Dictionary<string, TypeDef> types, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                Bind(obj, types, refs);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, types, refs);
                break;
            case JsonArray arr:
                foreach (var item in arr) if (item != null) Walk(item, types, refs);
                break;
        }
    }

    static void Bind(JsonObject call, Dictionary<string, TypeDef> types, ReferenceMetadataIndex refs)
    {
        var kind = Str(call["k"]);
        if (kind is not ("callInstance" or "newBoundDelegate" or "newBoundClrDelegate")) return;
        // An explicit Kotlin `super` call already names the exact non-virtual declaration owner selected by the
        // frontend.  Walking farther up the hierarchy is not inherited-member binding: it changes
        // `C.super<B>.m()` into `A.m()` when both B and A declare the same slot, skipping B's implementation.
        // Preserve that Kotlin semantic fact; bir2cir only needs hierarchy binding for ordinary receiver calls whose
        // BIR owner is the receiver type rather than the member's declaring type.
        if (Bool(call["super"])) return;
        if (TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn owner
            || (!types.ContainsKey(owner.Name) && !refs.TryReferenceTypeShape(owner.Name, out _, out _, out _, out _))) return;
        if (Str(call["method"]) is not string method) return;

        var sig = ReadTypes(call["sig"] as JsonArray);
        if (sig == null) return; // exact overload binding requires the declaration signature
        var methodArity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var hierarchy = ReachableTypes(owner, types, refs).ToList();

        // Prefer the frontend's semantic override/declaration fact, but still verify that
        // the reachable constructed declaration exactly matches this call signature.
        var overrideOwners = new HashSet<string>(StringComparer.Ordinal);
        if (call["overrides"] is JsonArray ovs)
            foreach (var ov in ovs.OfType<JsonObject>())
                if ((Str(ov["kind"]) is null or "method") && Str(ov["member"]) == method
                    && Str(ov["owner"]?["name"]) is string declared)
                    overrideOwners.Add(declared);

        var candidates = hierarchy
            .Where(r => !r.Type.Equals(owner))
            .Where(r => overrideOwners.Count == 0 || overrideOwners.Contains(r.Type.Name))
            .Where(r => DeclaresExact(types.GetValueOrDefault(r.Type.Name), method, methodArity, sig, r.Type.Args)
                || refs.DeclaresExactInstanceMember(r.Type.Name, method, methodArity, sig))
            .ToList();

        if (overrideOwners.Count > 0)
        {
            // `overrides` is a closure, not a single direct-parent pointer: Element.get may carry both
            // Element.get and CoroutineContext.get.  Select the unique nearest declaration in that
            // semantic closure; only same-depth collisions are genuinely ambiguous.
            if (candidates.Count == 0) return;
            var overrideDepth = candidates.Min(c => c.Depth);
            var direct = candidates.Where(c => c.Depth == overrideDepth)
                .Select(c => c.Type).Distinct().ToList();
            if (direct.Count != 1) return;
            call["ownerType"] = TypeJson.Write(direct[0]);
            if (IsInterface(direct[0].Name, types, refs)) call["virtual"] = true;
            if (kind == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(direct[0]);
            return;
        }

        if (candidates.Count == 0) return;
        var nearestDepth = candidates.Min(c => c.Depth);
        var nearest = candidates.Where(c => c.Depth == nearestDepth)
            .Select(c => c.Type).Distinct().ToList();
        if (nearest.Count != 1) return;
        call["ownerType"] = TypeJson.Write(nearest[0]);
        if (IsInterface(nearest[0].Name, types, refs)) call["virtual"] = true;
        if (kind == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(nearest[0]);
    }

    static IEnumerable<Reachable> ReachableTypes(TypeNode.Fqn start, Dictionary<string, TypeDef> types,
        ReferenceMetadataIndex refs)
    {
        var queue = new Queue<Reachable>();
        var seen = new HashSet<TypeNode.Fqn>();
        queue.Enqueue(new Reachable(start, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Type)) continue;
            yield return current;
            TypeNode.Fqn baseType;
            TypeNode.Fqn[] interfaces;
            int typeParamCount;
            if (types.TryGetValue(current.Type.Name, out var def))
            {
                typeParamCount = def.TypeParamCount;
                baseType = def.Base;
                interfaces = def.Interfaces;
            }
            else if (refs.TryReferenceTypeShape(current.Type.Name, out typeParamCount, out _, out baseType, out interfaces)) { }
            else continue;
            var args = EffectiveArgs(current.Type, typeParamCount);
            if (args == null) continue;
            if (baseType is not null)
                queue.Enqueue(new Reachable((TypeNode.Fqn)SubstOwnerTvs(baseType, args), current.Depth + 1));
            foreach (var iface in interfaces)
                queue.Enqueue(new Reachable((TypeNode.Fqn)SubstOwnerTvs(iface, args), current.Depth + 1));
        }
    }

    static bool IsInterface(string owner, Dictionary<string, TypeDef> types, ReferenceMetadataIndex refs)
    {
        if (types.GetValueOrDefault(owner)?.Kind == "interface") return true;
        return refs.TryReferenceTypeShape(owner, out _, out var kind, out _, out _) && kind == "interface";
    }

    static TypeNode[] EffectiveArgs(TypeNode.Fqn type, int count)
    {
        if (count == 0) return Array.Empty<TypeNode>();
        return type.Args is { } args && args.Length == count ? args : null;
    }

    static bool DeclaresExact(TypeDef def, string name, int methodArity, TypeNode[] callSig, TypeNode[] ownerArgs)
    {
        if (def?.Methods == null) return false;
        ownerArgs ??= def.TypeParamCount == 0 ? Array.Empty<TypeNode>() : null;
        if (ownerArgs == null || ownerArgs.Length != def.TypeParamCount) return false;

        var matches = 0;
        foreach (var method in def.Methods.OfType<JsonObject>())
        {
            if (Str(method["name"]) != name) continue;
            if (((method["typeParams"] as JsonArray)?.Count ?? 0) != methodArity) continue;
            if (method["params"] is not JsonArray ps || ps.Count != callSig.Length) continue;
            var exact = true;
            for (var i = 0; i < ps.Count; i++)
            {
                var declared = ps[i] is JsonObject p ? TypeJson.Read(p["type"]) : null;
                // `sig` is a declaration-signature descriptor, not an expression type: its type/method Tv indices
                // remain relative to the callee declaration even when ownerType is constructed.  Substituting those
                // Tvs here would compare an actual call-site type with a formal signature and reject the very inherited
                // generic call we need to bind.  The hierarchy args are used only to construct the MemberRef owner.
                if (declared == null || declared != callSig[i])
                {
                    exact = false;
                    break;
                }
            }
            if (exact) matches++;
        }
        return matches == 1;
    }

    static TypeNode[] ReadTypes(JsonArray array)
    {
        if (array == null) return null;
        var result = new TypeNode[array.Count];
        for (var i = 0; i < array.Count; i++)
            if ((result[i] = TypeJson.Read(array[i])) == null) return null;
        return result;
    }

    static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstOwnerTvs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstOwnerTvs(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args)),
        _ => type,
    };

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
}
