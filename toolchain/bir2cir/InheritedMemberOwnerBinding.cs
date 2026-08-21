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
        public JsonArray InheritedDefaultMethods;
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
                TypeParamCount = TypeParameterFrame.Count(type),
                Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                    .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                Methods = type["methods"] as JsonArray,
                InheritedDefaultMethods = type[KotlinPropertyAccessors.InheritedDefaultMethodsKey] as JsonArray,
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
        if (kind is not ("callInstance" or "newBoundDelegate" or "newBoundClrDelegate"
            or "clrInstance" or "clrPropGet" or "clrPropSet" or "clrEventAdd" or "clrEventRemove")) return;
        // Some earlier bir2cir passes synthesize a call whose CLR declaration owner and dispatch have already been
        // selected. Rebinding such a call from its receiver hierarchy would undo that decision (in particular, an
        // exact covariant-interface bridge would call its own interface slot and recurse).
        if (Bool(call["clrOwnerResolved"])) return;
        // An explicit Kotlin `super` call already names the exact non-virtual declaration owner selected by the
        // frontend.  Walking farther up the hierarchy is not inherited-member binding: it changes
        // `C.super<B>.m()` into `A.m()` when both B and A declare the same slot, skipping B's implementation.
        // Preserve that Kotlin semantic fact; bir2cir only needs hierarchy binding for ordinary receiver calls whose
        // BIR owner is the receiver type rather than the member's declaring type.
        if (Bool(call["super"])) return;
        var ownerSlot = kind switch
        {
            "clrInstance" or "clrPropGet" or "clrPropSet" or "clrEventAdd" or "clrEventRemove" => "type",
            "newBoundClrDelegate" => "clrType",
            _ => "ownerType",
        };
        if (TypeJson.Read(call[ownerSlot]) is not TypeNode.Fqn owner) return;
        // FIR can state the member's OPEN declaration owner while the receiver already carries the constructed
        // use-site type.  For example, `TargetList<String> : List<T>` may surface `Count` as owned by `List<type#0>`
        // even in a non-generic caller.  That type variable belongs to the declaration hierarchy, not the caller's
        // lexical frame.  Project the named declaration through the receiver's exact static hierarchy here, while
        // both `sty` and the local declarations are still available.  A receiver may reach the same generic owner
        // through more than one construction; only a unique constructed spec is authoritative, so ambiguity stays
        // unresolved instead of being guessed from arguments or expression values.
        if (TypeJson.Read(call["recv"]?["sty"]) is TypeNode.Fqn receiver && receiver.Name != owner.Name)
        {
            var projectedOwners = ReachableTypes(receiver, types, refs)
                .Where(candidate => candidate.Type.Name == owner.Name)
                .Select(candidate => candidate.Type)
                .GroupBy(SupertypeGraph.TypeKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (projectedOwners.Count == 1)
            {
                owner = projectedOwners[0];
                call[ownerSlot] = TypeJson.Write(owner);
                if (kind == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(owner);
            }
        }
        // The CLR-shaped nodes have already crossed MemberCallSubstitution. Their declaration descriptor and member
        // kind are resolved later by ClrMemberResolution; this pass owns only the constructed declaring owner.
        if (kind is "newBoundClrDelegate" or "clrInstance" or "clrPropGet" or "clrPropSet"
            or "clrEventAdd" or "clrEventRemove") return;
        if (!types.ContainsKey(owner.Name)
            && !refs.TryReferenceTypeShape(owner.Name, out _, out _, out _, out _)) return;
        if (Str(call["method"]) is not string method) return;
        var hasPropertyIdentity = KotlinPropertyAccessors.TryCallIdentity(
            call, out var propertyName, out var propertyAccessor);
        if (!hasPropertyIdentity)
        {
            propertyName = null;
            propertyAccessor = null;
        }

        var methodArity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var sig = ReadTypes(call["sig"] as JsonArray);
        var paramCount = (call["args"] as JsonArray)?.Count ?? -1;

        // A call can already name its exact declaration owner yet still lack the CLR dispatch bit. This is especially
        // visible cross-module when a Kotlin-final accessor implements an existential interface slot and is therefore
        // virtual in metadata. Consume that declaration fact here. With no BIR signature, require a unique
        // name/method-arity/parameter-count declaration; never guess among overloads.
        if (paramCount >= 0
            && (DeclaresVirtual(types.GetValueOrDefault(owner.Name), method, methodArity, sig, paramCount,
                    propertyName, propertyAccessor)
                || (propertyAccessor != null
                    ? refs.DeclaresVirtualInstancePropertyAccessor(owner.Name, propertyName, propertyAccessor,
                        methodArity, sig, paramCount, owner.Args ?? Array.Empty<TypeNode>())
                    : refs.DeclaresVirtualInstanceMember(owner.Name, method, methodArity, sig, paramCount))))
            call["virtual"] = true;

        if (sig == null) return; // inherited owner binding requires the declaration signature

        // The static receiver owner can itself declare the exact slot while also carrying the Kotlin override closure.
        // That closure records semantic ancestry; it does not ask CIR to call the base/interface declaration. Retargeting
        // a covariant concrete method (`Derived.m(): Derived`) to its interface slot (`Base.m(): Base`) changes the
        // physical return type and makes an immediately-narrow consumer unverifiable. Prefer the real declaration on
        // the current owner. A local fake override is not emitted as a slot, so it deliberately falls through to the
        // hierarchy search.
        var ownDeclarationSig = ExactDeclarationSignature(
            types.GetValueOrDefault(owner.Name), method, methodArity, sig, owner.Args,
            propertyName, propertyAccessor);
        if (ownDeclarationSig != null)
        {
            call["sig"] = ownDeclarationSig;
            return;
        }
        // A local interface can expose an inherited external default implementation as a fake override. That fake
        // method is not emitted and therefore cannot be a bound-delegate target, but its inheritedImplementation
        // carrier is the frontend's exact declaration fact. Retarget the callable reference to that declaration now;
        // leaving it on the local owner makes the later exact local lookup (correctly) find no MethodDef.
        if (kind == "newBoundDelegate"
            && ExactInheritedDefault(types.GetValueOrDefault(owner.Name), method, methodArity, sig, owner.Args)
                is JsonObject inherited
            && inherited["implementation"] is JsonObject implementation
            && TypeJson.Read(implementation["owner"]) is TypeNode.Fqn implementationOwner
            && Str(implementation["member"]) is string implementationMember)
        {
            // kotc's implementation fact identifies the declaration, not its use-site instantiation. Project that
            // identity through the already-constructed receiver hierarchy so `Local<T> : External<T>` becomes
            // `External<T>`, rather than throwing away T by copying the carrier's deliberately bare owner.
            var projectedOwners = ReachableTypes(owner, types, refs)
                .Where(candidate => ReferenceMetadataIndex.BareOwnerFqn(candidate.Type.Name)
                    == ReferenceMetadataIndex.BareOwnerFqn(implementationOwner.Name))
                .Select(candidate => candidate.Type).Distinct().ToList();
            if (projectedOwners.Count != 1) return;
            var projectedOwner = projectedOwners[0];
            call["ownerType"] = TypeJson.Write(projectedOwner);
            call["calleeOwner"] = TypeJson.Write(projectedOwner);
            call["method"] = implementationMember;
            call["virtual"] = true;
            return;
        }
        if (propertyAccessor != null
                ? refs.DeclaresExactInstancePropertyAccessor(owner.Name, propertyName, propertyAccessor,
                    methodArity, sig, owner.Args ?? Array.Empty<TypeNode>())
                : refs.DeclaresExactInstanceMember(owner.Name, method, methodArity, sig))
            return;

        var hierarchy = ReachableTypes(owner, types, refs).ToList();

        // Prefer the frontend's semantic override/declaration fact, but still verify that
        // the reachable constructed declaration exactly matches this call signature.
        var overrideOwners = new HashSet<string>(StringComparer.Ordinal);
        if (call["overrides"] is JsonArray ovs)
            foreach (var ov in ovs.OfType<JsonObject>())
                if ((propertyAccessor == null
                        ? Str(ov["kind"]) is null or "method"
                        : Str(ov["kind"]) == (propertyAccessor == "get" ? "getter" : "setter"))
                    && Str(ov["member"]) == (propertyName ?? method)
                    && Str(ov["owner"]?["name"]) is string declared)
                    overrideOwners.Add(declared);

        var candidates = hierarchy
            .Where(r => !r.Type.Equals(owner))
            .Where(r => overrideOwners.Count == 0 || overrideOwners.Contains(r.Type.Name))
            .Where(r => DeclaresExact(types.GetValueOrDefault(r.Type.Name), method, methodArity, sig, r.Type.Args,
                    propertyName, propertyAccessor)
                || (propertyAccessor != null
                    ? refs.DeclaresExactInstancePropertyAccessor(r.Type.Name, propertyName, propertyAccessor,
                        methodArity, sig, r.Type.Args ?? Array.Empty<TypeNode>())
                    : refs.DeclaresExactInstanceMember(r.Type.Name, method, methodArity, sig)))
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
            if (ExactDeclarationSignature(types.GetValueOrDefault(direct[0].Name), method, methodArity,
                    sig, direct[0].Args, propertyName, propertyAccessor) is { } directDeclarationSig)
                call["sig"] = directDeclarationSig;
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
        if (ExactDeclarationSignature(types.GetValueOrDefault(nearest[0].Name), method, methodArity,
                sig, nearest[0].Args, propertyName, propertyAccessor) is { } nearestDeclarationSig)
            call["sig"] = nearestDeclarationSig;
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

    static bool DeclaresExact(TypeDef def, string name, int methodArity, TypeNode[] callSig, TypeNode[] ownerArgs,
        string propertyName, string propertyAccessor)
        => ExactDeclarationSignature(def, name, methodArity, callSig, ownerArgs,
            propertyName, propertyAccessor) != null;

    static JsonArray ExactDeclarationSignature(TypeDef def, string name, int methodArity,
        TypeNode[] callSig, TypeNode[] ownerArgs, string propertyName, string propertyAccessor)
    {
        var match = ExactMethod(def, name, methodArity, callSig, ownerArgs,
            propertyName, propertyAccessor, fakeOverride: false);
        return match?["params"] is JsonArray ps
            ? new JsonArray(ps.OfType<JsonObject>()
                .Select(p => TypeJson.Write(TypeJson.Read(p["type"]))).ToArray())
            : null;
    }

    static JsonObject ExactMethod(TypeDef def, string name, int methodArity,
        TypeNode[] callSig, TypeNode[] ownerArgs, string propertyName, string propertyAccessor,
        bool fakeOverride)
    {
        if (def?.Methods == null) return null;
        ownerArgs ??= def.TypeParamCount == 0 ? Array.Empty<TypeNode>() : null;
        if (ownerArgs == null || ownerArgs.Length != def.TypeParamCount) return null;

        var matches = new List<JsonObject>();
        foreach (var method in def.Methods.OfType<JsonObject>())
        {
            if (KotlinPropertyAccessors.IsPhysicalSlotBridge(method)) continue;
            if (propertyAccessor != null)
            {
                if (!KotlinPropertyAccessors.TryIdentity(method, out var candidateProperty, out var candidateAccessor)
                    || candidateProperty != propertyName || candidateAccessor != propertyAccessor) continue;
            }
            else if (Str(method["name"]) != name
                || KotlinPropertyAccessors.TryIdentity(method, out _, out _)) continue;
            if (Bool(method["fakeOverride"]) != fakeOverride) continue;
            if (((method["typeParams"] as JsonArray)?.Count ?? 0) != methodArity) continue;
            if (method["params"] is not JsonArray ps || ps.Count != callSig.Length) continue;
            var exact = true;
            for (var i = 0; i < ps.Count; i++)
            {
                var declared = ps[i] is JsonObject p ? TypeJson.Read(p["type"]) : null;
                // kotc preserves the formal descriptor where FIR exposes it, but some inherited call sites carry the
                // same declaration after owner substitution (`Base<T>.m(T)` reached through `Derived : Base<Leaf>`
                // arrives as `m(Leaf)`). Both are exact descriptions of one declaration; accept only raw identity or
                // the hierarchy-derived owner substitution, never expression assignability.
                var substituted = declared == null ? null : SubstOwnerTvs(declared, ownerArgs);
                if (declared == null || (declared != callSig[i]
                    && substituted != callSig[i]
                    // An override of `Base<T>.m(List<T?>)` may itself be declared as `m(List<String?>)`, while the
                    // CLR slot is rewritten to the base declaration's uniform `IReadOnlyList<object>`. The call still
                    // carries the Kotlin declaration descriptor (`IReadOnlyList<string>`). This is one declaration
                    // precisely when the emitted vector is its NESTED object-erasure image; a bare `object` is not
                    // accepted here because that would turn ordinary argument assignability into member selection.
                    && !IsNestedObjectErasureOf(declared, callSig[i])
                    && !IsNestedObjectErasureOf(substituted, callSig[i])))
                {
                    exact = false;
                    break;
                }
            }
            if (exact) matches.Add(method);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    static JsonObject ExactInheritedDefault(TypeDef def, string name, int methodArity,
        TypeNode[] callSig, TypeNode[] ownerArgs)
    {
        if (def?.InheritedDefaultMethods == null) return null;
        ownerArgs ??= def.TypeParamCount == 0 ? Array.Empty<TypeNode>() : null;
        if (ownerArgs == null || ownerArgs.Length != def.TypeParamCount) return null;
        var matches = def.InheritedDefaultMethods.OfType<JsonObject>().Where(fact =>
        {
            if (Str(fact["member"]) != name || fact["params"] is not JsonArray ps
                || ps.Count != callSig.Length || fact["implementation"] is not JsonObject implementation
                || ((implementation["typeParams"] as JsonArray)?.Count ?? 0) != methodArity) return false;
            return ps.Select(TypeJson.Read).Select((declared, i) =>
            {
                var substituted = declared == null ? null : SubstOwnerTvs(declared, ownerArgs);
                return declared != null && (declared == callSig[i] || substituted == callSig[i]
                    || IsNestedObjectErasureOf(declared, callSig[i])
                    || IsNestedObjectErasureOf(substituted, callSig[i]));
            }).All(match => match);
        }).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    static bool IsNestedObjectErasureOf(TypeNode candidate, TypeNode source)
    {
        if (candidate == null || source == null || candidate.Equals(source)) return candidate != null;
        return (candidate, source) switch
        {
            (TypeNode.Fqn { Args: { } ca } cf, TypeNode.Fqn { Args: { } sa } sf)
                when cf.Name == sf.Name && ca.Length == sa.Length
                => ca.Zip(sa, IsObjectErasureOf).All(x => x),
            (TypeNode.Array c, TypeNode.Array s) => IsObjectErasureOf(c.Elem, s.Elem),
            (TypeNode.Nullable c, TypeNode.Nullable s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.Oblivious c, TypeNode.Oblivious s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.ByRef c, TypeNode.ByRef s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.Fn c, TypeNode.Fn s)
                when c.Params.Length == s.Params.Length && c.Suspend == s.Suspend
                     && (c.Recv == null) == (s.Recv == null)
                => IsObjectErasureOf(c.Ret, s.Ret)
                   && c.Params.Zip(s.Params, IsObjectErasureOf).All(x => x)
                   && (c.Recv == null || IsObjectErasureOf(c.Recv, s.Recv)),
            _ => false,
        };
    }

    static bool IsObjectErasureOf(TypeNode candidate, TypeNode source)
    {
        if (candidate.Equals(source)) return true;
        if (candidate is TypeNode.Fqn { Name: "object", Args: null }) return true;
        return IsNestedObjectErasureOf(candidate, source);
    }

    static bool DeclaresVirtual(TypeDef def, string name, int methodArity, TypeNode[] callSig, int paramCount,
        string propertyName, string propertyAccessor)
    {
        if (def?.Methods == null) return false;
        var matches = def.Methods.OfType<JsonObject>()
            .Where(method => !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
                && (propertyAccessor != null
                    ? KotlinPropertyAccessors.TryIdentity(method, out var candidateProperty, out var candidateAccessor)
                        && candidateProperty == propertyName && candidateAccessor == propertyAccessor
                    : Str(method["name"]) == name && !KotlinPropertyAccessors.TryIdentity(method, out _, out _))
                && ((method["typeParams"] as JsonArray)?.Count ?? 0) == methodArity
                && method["params"] is JsonArray ps && ps.Count == paramCount)
            .Where(method => callSig == null || callSig.Length == paramCount
                && method["params"] is JsonArray ps
                && ps.Select((p, i) => p is JsonObject parameter
                    && TypeJson.Read(parameter["type"]) == callSig[i]).All(x => x))
            .ToList();
        return matches.Count == 1 && Bool(matches[0]["virtual"]);
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
