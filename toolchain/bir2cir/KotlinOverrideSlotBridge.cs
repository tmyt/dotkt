using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// ERASURE PROPAGATES FROM THE OVERRIDDEN SLOT, NOT FROM SYNTAX (#86 D3).
//
// `interface Sink<T> { fun accept(x: T?): String }` has its parameter object-erased like every other `Nullable(Tv)`
// slot, so the CLR slot an implementor must fill is `accept(object)` — at EVERY instantiation, because the erasure is
// a property of the DECLARATION and not of the type argument. `class IntSink : Sink<Int>` writes
// `override fun accept(x: Int?)`, which holds a CONCRETE type: there is no `Nullable(Tv)` anywhere in it, so no
// syntactic sweep can reach it, and left alone it emits `accept(Nullable<int32>)` — a NEW OVERLOAD. The interface
// method stays unimplemented and the type fails to LOAD.
//
// WHICH DECLARATION MOVES IS THE WHOLE DESIGN. Rewriting the override's own signature to the base slot makes the class
// load and dispatch, but it puts the type's physical shape and its Kotlin surface into permanent disagreement: the
// declaration says `accept(x: Int?)` and the assembly says `accept(object)`, so a separately compiled consumer that
// type-checks against the re-imported surface resolves a member that does not exist ("no referenced method matches the
// resolved descriptor IntSink.accept(nullable:System.Int32)"), and a C# consumer sees a parameter the Kotlin
// declaration never named. So THE OVERRIDE KEEPS ITS OWN PHYSICAL TYPE — `physical(s) = Erase(declared(s))` holds for
// it exactly as for every other declaration — and the base slot is filled by a synthesized PRIVATE bridge with the
// slot's exact signature, forwarding to the typed body across the `object` seam. That is the CLR's own
// explicit-implementation shape and the JVM's bridge-method idiom; `CovariantInterfaceReturnBridge` is the same
// construction for the covariant-return case, and this file is deliberately shaped like it.
//
// Being private, the bridge is not part of the Kotlin surface: dll2klib projects public and protected members only, so
// the re-imported type carries the one `accept(x: Int?)` the author wrote, with a physical member behind it. A public
// bridge would instead re-import as a second `accept(x: Any?)` overload and make `IntSink().accept("s")` compile.
// Private is necessary and not sufficient — see CarryKotlinType for the one path that re-surfaces a private body.
//
// ONE BRIDGE, ONE METHODIMPL PER SLOT IT FILLS. A slot is reached through the whole supertype graph — the constructed
// interface, its own base interfaces (including the synthesized `G$dotkt_star` existential view), and the base-class
// chain — and every one of those declares its own CLR slot even when the signatures coincide. The bridges are keyed by
// signature so the several slots share one body, and each contributes its own resolved MethodImpl descriptor.
//
// NESTED POSITIONS CANNOT BE BRIDGED, and are the one case where the declaration still moves. A base `Box<T?>` erases
// to `Box<object>`, and `Box<object>` and `Box<Nullable<int32>>` are unrelated invariant reified generics that no cast
// converts — there is no forwarding body to write. Such a position is rewritten in the override's declaration to the
// base's shape, with the override's own pre-erasure Kotlin type recorded on the two round-trip channels every erased
// slot uses (the `[KotlinNullableGeneric]` carrier and the slot's NRT byte), so the surface survives even though the
// physical type had to move. The split is exactly the CLR's: a bare `object` seam is one instruction in each
// direction, and a difference under a constructed generic is not expressible at all.
static class KotlinOverrideSlotBridge
{
    public sealed class Def
    {
        public string Name;
        public string Kind;
        public int Arity;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public JsonObject Node;
        public JsonArray Methods;
    }

    // THE TWO HALVES RUN AT DIFFERENT POINTS, because their inputs are valid at different points.
    //
    // The DECLARATION MOVE (a nested position no cast reaches) must land before the erasure sweep and the use-side
    // realign: those are what retype the body that reads the moved parameter, and a slot moved after them leaves the
    // body reading it at the type it used to have — verifiably wrong IL.
    //
    // The BRIDGE must land after the star-projection erasure, because that pass ADDS supertypes: a `G<*>` anywhere in
    // the compilation gives `G` a synthesized `G$dotkt_star` view whose slots an implementer must fill too, and a
    // bridge built before it exists would carry a MethodImpl for the constructed interface and leave the existential
    // one unimplemented. Nothing between the two points can introduce a new nested move — an existential view's slots
    // are copies of the erased ones — so the halves do not race.
    //
    // The declaration half, over every file at once (a base may be declared in another file of this compilation).
    public static void PropagateErasedSlots(IEnumerable<JsonNode> roots, Func<string, bool> isValue) =>
        ApplyAll(roots, isValue, emitBridges: false);

    // The bridge half.
    public static void ApplyAll(IEnumerable<JsonNode> roots, Func<string, bool> isValue) =>
        ApplyAll(roots, isValue, emitBridges: true);

    static void ApplyAll(IEnumerable<JsonNode> roots, Func<string, bool> isValue, bool emitBridges)
    {
        var defs = Collect(roots);
        foreach (var cls in defs.Values.Where(d => d.Kind == "class").ToList())
            ApplyClass(cls, defs, isValue, emitBridges);
    }

    static Dictionary<string, Def> Collect(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<string, Def>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, result);
        return result;
    }

    static void CollectFrom(JsonNode node, Dictionary<string, Def> result)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is not string name) continue;
            result[name] = new Def
            {
                Name = name,
                Kind = Str(type["kind"]),
                Arity = (type["typeParams"] as JsonArray)?.Count ?? 0,
                Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                    .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                Node = type,
                Methods = type["methods"] as JsonArray ?? new JsonArray(),
            };
            CollectFrom(type, result);
        }
    }

    static void ApplyClass(Def cls, IReadOnlyDictionary<string, Def> defs, Func<string, bool> isValue, bool emitBridges)
    {
        if (cls.Node["methods"] is not JsonArray methods) return;
        var ownArgs = ClassOwnArgs(cls);
        var bridges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var ordinal = 0;

        foreach (var (spec, supIsInterface) in ReachableSupertypes(cls, defs))
        {
            if (!defs.TryGetValue(spec.Name, out var sup)) continue;
            var supArgs = EffectiveArgs(spec, sup.Arity);
            if (supArgs == null) continue;

            foreach (var slot in sup.Methods.OfType<JsonObject>().ToList())
            {
                if (Bool(slot["static"]) || Str(slot["name"]) is not string name
                    || slot["params"] is not JsonArray slotParamNodes) continue;
                var methodArity = (slot["typeParams"] as JsonArray)?.Count ?? 0;
                // `Subst(Erase(declared), typeArgs)` and never `Erase(Subst(...))` — a substituted `Nullable(kotlin.Int)`
                // has no type variable left to erase and would state the wrong slot. The erasure is idempotent, so
                // this reads the same slot before the sweep has run and after.
                var slotParams = slotParamNodes.OfType<JsonObject>()
                    .Select(p => TypeJson.Read(p["type"]))
                    .Select(t => t == null ? null : SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(t, isValue), supArgs))
                    .ToArray();
                var slotRet0 = TypeJson.Read(slot["ret"]);
                if (slotParams.Any(p => p == null) || slotRet0 == null) continue;
                var slotRet = SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(slotRet0, isValue), supArgs);

                if (Implementer(cls, defs, methods, spec.Name, name, methodArity, slotParams, ownArgs) is not JsonObject impl)
                    continue;
                var declParams = impl["params"] as JsonArray;
                var declRet = TypeJson.Read(impl["ret"]);
                if (declParams == null || declRet == null || declParams.Count != slotParams.Length) continue;

                var fit = new Fit[slotParams.Length];
                for (var i = 0; i < slotParams.Length; i++)
                {
                    var declT = TypeJson.Read((declParams[i] as JsonObject)?["type"]);
                    if (declT == null) { fit = null; break; }
                    fit[i] = Classify(slotParams[i], SubstOwnerTvs(declT, ownArgs));
                }
                // A parameter difference this erasure did not create belongs to whatever pass did create it.
                if (fit == null || fit.Contains(Fit.Foreign)) continue;
                var retFit = Classify(slotRet, SubstOwnerTvs(declRet, ownArgs));
                if (retFit == Fit.Foreign)
                {
                    // A COVARIANT return over an otherwise-exact signature is CovariantInterfaceReturnBridge's, and
                    // both passes emitting a bridge would declare the slot's signature twice. It only becomes this
                    // pass's when a parameter erased too — which is exactly when that pass, matching on an exact
                    // parameter vector, cannot fire — and then the one bridge states the whole slot and upcasts.
                    if (!fit.Any(f => f is Fit.Bridge or Fit.Rewrite)) continue;
                    retFit = Fit.Bridge;
                }

                // A position no conversion reaches moves the DECLARATION onto the slot's shape — the only option the
                // CLR leaves under a constructed generic — carrying its Kotlin surface on the round-trip channels.
                for (var i = 0; i < slotParams.Length; i++)
                    if (fit[i] == Fit.Rewrite && declParams[i] is JsonObject po)
                        Rewrite(po, "type", "nullableGeneric", "nullableFlags", slotParams[i], isValue);
                if (retFit == Fit.Rewrite) Rewrite(impl, "ret", "nullableGenericRet", "retNullableFlags", slotRet, isValue);
                if (!emitBridges || (!fit.Contains(Fit.Bridge) && retFit != Fit.Bridge)) continue;

                // The typed body is what the bridge dispatches to, so it must own a virtual slot of its own. Kotlin
                // does not require `open` to satisfy an interface, and the exact-signature normalization that would
                // otherwise mark it no longer matches — the signatures deliberately differ now.
                impl["virtual"] = true;

                var key = name + "(" + string.Join(",", slotParams.Select(TypeKey)) + ")->" + TypeKey(slotRet);
                if (!bridges.TryGetValue(key, out var bridge))
                {
                    bridge = BuildBridge(cls, impl, slotParams, slotRet, $"dotkt$ovslot${SafeName(name)}${ordinal++}", isValue);
                    bridges[key] = bridge;
                    methods.Add(bridge);
                }
                // Which METADATA wiring fills the slot: an interface slot is a MethodImpl against the constructed
                // interface, a base-class slot a MethodImpl against the constructed base. ilemit consumes the
                // resolved descriptor and resolves nothing itself.
                ((JsonArray)bridge[supIsInterface ? "clrInterfaceImpls" : "clrBaseImpls"])
                    .Add(ImplDescriptor(spec, name, slotParams, slotRet));
            }
        }
    }

    enum Fit { Same, Bridge, Rewrite, Foreign }

    // How the override's own physical type meets the slot the supertype requires.
    //   Same    — it already IS the slot (or lowers to it): nothing to do.
    //   Bridge  — the slot is a bare `object` and the declaration is not: the two interconvert in ONE instruction
    //             (`unbox.any` inward, `box`/`castclass` outward), so a forwarding body exists and the declaration
    //             keeps its own, truthful type.
    //   Rewrite — the difference is the SAME erasure, one level down: under a constructed generic or an array, where
    //             no conversion exists in either direction, so the declaration has to adopt the slot's shape.
    //   Foreign — a difference this erasure did not create (a covariantly narrowed return, a `@ClrTypeAlias` reshape).
    //             Not this pass's to reconcile, and moving it would state a type the author never wrote.
    static Fit Classify(TypeNode slot, TypeNode declared)
    {
        if (declared.Equals(slot)) return Fit.Same;
        if (IsBareObject(slot))
            // `Any?`/`Any` reach the same bare `object` the slot is, one lowering later — a bridge for them would
            // declare a second member with the identical CLR signature.
            return LowersToObject(declared) ? Fit.Same : Fit.Bridge;
        return ErasureAligned(slot, declared) ? Fit.Rewrite : Fit.Foreign;
    }

    // True iff `slot` is `declared` with a bare `object` at some positions and nothing else changed — i.e. the two
    // differ ONLY where the base declaration was object-erased. Every other divergence, at any depth, says the slot
    // and the declaration part company for a reason that is not this erasure.
    static bool ErasureAligned(TypeNode slot, TypeNode declared)
    {
        if (IsBareObject(slot) || slot.Equals(declared)) return true;
        switch (slot, declared)
        {
            case (TypeNode.Fqn { Args: { } sa } sf, TypeNode.Fqn { Args: { } da } df)
                when sf.Name == df.Name && sa.Length == da.Length:
                return !sa.Where((s, i) => !ErasureAligned(s, da[i])).Any();
            case (TypeNode.Array s, TypeNode.Array d): return ErasureAligned(s.Elem, d.Elem);
            case (TypeNode.Nullable s, TypeNode.Nullable d): return ErasureAligned(s.Of, d.Of);
            case (TypeNode.Oblivious s, TypeNode.Oblivious d): return ErasureAligned(s.Of, d.Of);
            case (TypeNode.ByRef s, TypeNode.ByRef d): return ErasureAligned(s.Of, d.Of);
            case (TypeNode.Fn s, TypeNode.Fn d)
                when s.Suspend == d.Suspend && s.Params.Length == d.Params.Length && (s.Recv == null) == (d.Recv == null):
                return ErasureAligned(s.Ret, d.Ret)
                       && !s.Params.Where((p, i) => !ErasureAligned(p, d.Params[i])).Any()
                       && (s.Recv == null || ErasureAligned(s.Recv, d.Recv));
            default: return false;
        }
    }

    static bool IsBareObject(TypeNode t) => t is TypeNode.Fqn { Name: "object" or "System.Object", Args: null };

    // True iff BirTypeLowering will make this type the bare `object` the erased slot already is. A reference `T?` is
    // stripped to its bare inner, so the question recurses through the wrapper; a VALUE `T?` stays `Nullable<V>`, and
    // a type variable stays `!T`.
    static bool LowersToObject(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => LowersToObject(o.Of),
        TypeNode.Nullable n => LowersToObject(n.Of),
        TypeNode.Fn f => f.Suspend,
        TypeNode.Fqn { Args: null } f => f.Name is "object" or "System.Object" or "kotlin.Any",
        _ => false,
    };

    // The class's own declaration that fills this slot. Two independent proofs are accepted, and both are needed:
    // the Kotlin `overrides` fact names the supertype the author wrote (`Sink`), which is what makes the match safe;
    // and a slot may be reached through a supertype the author never named (`Sink`'s own base interfaces, including
    // the synthesized `Sink$dotkt_star` existential view), so an ANCESTOR of an overridden owner counts too. An
    // unrelated same-name overload proves neither and is left alone — mis-wiring a MethodImpl fails type LOAD.
    static JsonObject Implementer(Def cls, IReadOnlyDictionary<string, Def> defs, JsonArray methods, string supName,
        string name, int methodArity, TypeNode[] slotParams, TypeNode[] ownArgs)
    {
        JsonObject found = null;
        foreach (var m in methods.OfType<JsonObject>())
        {
            if (Bool(m["static"]) || Str(m["name"]) != name) continue;
            if (((m["typeParams"] as JsonArray)?.Count ?? 0) != methodArity) continue;
            if (m["params"] is not JsonArray ps || ps.Count != slotParams.Length) continue;
            if (Str(m["vis"]) is not (null or "public" or "protected")) continue;
            if (!OverridesInto(m, defs, supName, name)) continue;
            // Every position must either already BE the slot or differ from it exactly where the slot was erased —
            // the only positions an override is free to narrow, and so the only ones whose types legitimately differ.
            var ok = true;
            for (var i = 0; i < ps.Count && ok; i++)
            {
                var t = TypeJson.Read((ps[i] as JsonObject)?["type"]);
                ok = t != null && ErasureAligned(slotParams[i], SubstOwnerTvs(t, ownArgs));
            }
            if (!ok) continue;
            if (found != null) return null;   // ambiguous overload set: never guess which declaration owns the slot
            found = m;
        }
        return found;
    }

    // True iff this method declares itself an override of `supName`'s member — directly, or of some owner that
    // `supName` is a supertype of (a redeclared slot on a base interface is a DISTINCT CLR slot with the same shape).
    static bool OverridesInto(JsonObject method, IReadOnlyDictionary<string, Def> defs, string supName, string member)
    {
        if (method["overrides"] is not JsonArray overrides) return false;
        foreach (var o in overrides.OfType<JsonObject>())
        {
            if (TypeJson.OwnerName(o["owner"]) is not string owner) continue;
            var m = Str(o["member"]);
            if (m != member && "get_" + m != member && "set_" + m != member) continue;
            if (owner == supName || IsAncestor(defs, supName, owner)) return true;
        }
        return false;
    }

    static bool IsAncestor(IReadOnlyDictionary<string, Def> defs, string ancestor, string of)
    {
        var queue = new Queue<string>();
        queue.Enqueue(of);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (!seen.Add(n)) continue;
            if (n == ancestor) return true;
            if (!defs.TryGetValue(n, out var d)) continue;
            foreach (var i in d.Interfaces) queue.Enqueue(i.Name);
            if (d.Base != null) queue.Enqueue(d.Base.Name);
        }
        return false;
    }

    // Move a slot the CLR cannot bridge onto the supertype's shape, carrying the override's own pre-erasure Kotlin
    // type across on the round-trip channels so the surface survives the move.
    static void Rewrite(JsonObject decl, string typeKey, string factKey, string flagsKey, TypeNode slot,
        Func<string, bool> isValue)
    {
        if (TypeJson.Read(decl[typeKey]) is not TypeNode t || t.Equals(slot)) return;
        decl[typeKey] = TypeJson.Write(slot);
        decl[factKey] ??= TypeNode.ToJson(t);
        if (!decl.ContainsKey(flagsKey) && NullableFlags.Compute(t, isValue) is JsonArray f) decl[flagsKey] = f;
    }

    // The bridge: the slot's exact signature, forwarding VIRTUALLY to the typed body so a further-derived override of
    // that body is still what runs. Each argument is narrowed out of the slot's `object` into the declared type
    // (`unbox.any` for a value, `castclass` for a reference) and the result widened back.
    static JsonObject BuildBridge(Def cls, JsonObject impl, TypeNode[] slotParams, TypeNode slotRet, string bridgeName,
        Func<string, bool> isValue)
    {
        var declParams = impl["params"] as JsonArray ?? new JsonArray();
        var bridgeParams = new JsonArray();
        var callArgs = new JsonArray();
        var callSig = new JsonArray();
        for (var i = 0; i < slotParams.Length; i++)
        {
            var p = declParams[i] as JsonObject;
            var pn = Str(p?["name"]) ?? "p" + i;
            var bp = new JsonObject { ["name"] = pn, ["type"] = TypeJson.Write(slotParams[i]) };
            var declT = TypeJson.Read(p?["type"]);
            if (declT != null && !declT.Equals(slotParams[i]))
            {
                CarryKotlinType(bp, "nullableGeneric", "nullableFlags", declT, slotParams[i], isValue);
                bridgeParams.Add(bp);
                callArgs.Add(new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(declT),
                    ["e"] = new JsonObject { ["k"] = "local", ["name"] = pn },
                });
            }
            else
            {
                bridgeParams.Add(bp);
                callArgs.Add(new JsonObject { ["k"] = "local", ["name"] = pn });
            }
            callSig.Add(p?["type"]?.DeepClone());
        }

        var implRet = TypeJson.Read(impl["ret"]);
        var ownArgs = ClassOwnArgs(cls);
        var owner = new TypeNode.Fqn(cls.Name, ownArgs.Length == 0 ? null : ownArgs);
        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(owner),
            ["virtual"] = true,
            // Synthesized by bir2cir with its exact CLR declaration owner: the later inherited-owner pass must not
            // reinterpret it as an ordinary Kotlin receiver call and rebind it to the slot this bridge implements.
            ["clrOwnerResolved"] = true,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = Str(impl["name"]),
            ["sig"] = callSig,
            ["dynRet"] = TypeJson.Write(implRet),
            ["ret"] = TypeJson.Write(implRet),
            ["args"] = callArgs,
        };
        if (impl["typeParams"] is JsonArray methodTps && methodTps.Count > 0)
        {
            var typeArgs = new JsonArray();
            for (var i = 0; i < methodTps.Count; i++) typeArgs.Add(TypeJson.Write(new TypeNode.Tv("method", i)));
            call["typeArgs"] = typeArgs;
        }

        var body = new JsonArray();
        var retCarried = false;
        if (slotRet is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void" })
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = call });
        else
        {
            JsonNode value = call;
            if (implRet != null && !implRet.Equals(slotRet))
            {
                value = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(slotRet), ["e"] = value };
                retCarried = true;
            }
            body.Add(new JsonObject { ["k"] = "return", ["value"] = value });
        }

        var bridge = new JsonObject
        {
            ["name"] = bridgeName,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            // PRIVATE is what keeps the bridge off the Kotlin surface: dll2klib projects public and protected members
            // only, so the re-imported type carries the author's declaration and nothing else.
            ["vis"] = "private",
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(slotRet),
            ["body"] = body,
            ["attrs"] = new JsonArray(),
            ["generated"] = true,
            ["clrInterfaceImpls"] = new JsonArray(),
            ["clrBaseImpls"] = new JsonArray(),
        };
        if (retCarried && implRet != null)
            CarryKotlinType(bridge, "nullableGenericRet", "retNullableFlags", implRet, slotRet, isValue);
        if (impl["typeParams"] is JsonArray tps) bridge["typeParams"] = tps.DeepClone();
        return bridge;
    }

    // The Kotlin type of a slot the bridge states physically, on BOTH round-trip channels every erased slot uses: the
    // `[KotlinNullableGeneric]` carrier for the type node, and the NRT byte for its outer `?`. Neither alone restores
    // `Int?` — the carrier's outer nullability is deliberately the byte's job — and a bridge carrying only the node
    // re-imports as `Int`.
    //
    // It is not cosmetic here, and it is the reason a private bridge is not simply invisible. dll2klib deliberately
    // re-surfaces a PRIVATE MethodImpl body under the interface member's name — a class that satisfies an interface
    // slot only privately would otherwise re-import still carrying the abstract obligation — and it de-duplicates
    // that against the class's public functions by SIGNATURE. Uncarried, the bridge re-surfaces as `accept(x: Any)`:
    // a second overload beside the author's `accept(x: Int?)`, which makes `IntSink().accept("s")` compile and
    // `IntSink().accept(3)` ambiguous. Carried, the two keys coincide, the bridge de-duplicates away, and the
    // re-imported type carries exactly the one declaration that was written.
    static void CarryKotlinType(JsonObject decl, string factKey, string flagsKey, TypeNode kotlin, TypeNode slot,
        Func<string, bool> isValue)
    {
        decl[factKey] ??= TypeNode.ToJson(kotlin);
        // The byte walk describes the PHYSICAL slot, which is the bare `object` this bridge states, carrying the
        // Kotlin type's outer nullability onto it. Walking the Kotlin type instead would stamp nothing for an `Int?`
        // — a value type's `?` is the structural `Nullable<V>` and contributes no NRT byte — and the slot would
        // re-import as a non-null `Int`, which is a different overload again.
        var shape = kotlin is TypeNode.Nullable ? new TypeNode.Nullable(slot) : slot;
        if (!decl.ContainsKey(flagsKey) && NullableFlags.Compute(shape, isValue) is JsonArray f) decl[flagsKey] = f;
    }

    static JsonObject ImplDescriptor(TypeNode.Fqn spec, string member, TypeNode[] slotParams, TypeNode slotRet)
    {
        var ps = new JsonArray();
        foreach (var p in slotParams) ps.Add(TypeJson.Write(p));
        return new JsonObject
        {
            ["owner"] = TypeJson.Write(spec),
            ["member"] = member,
            ["params"] = ps,
            ["ret"] = TypeJson.Write(slotRet),
        };
    }

    // Every supertype this class reaches, as a CONSTRUCTED spec in the class's own type-parameter frame: the
    // interface graph (transitively, so a base interface's redeclared slot is reached) and the base-class chain.
    static IEnumerable<(TypeNode.Fqn spec, bool isInterface)> ReachableSupertypes(Def cls, IReadOnlyDictionary<string, Def> defs)
    {
        var queue = new Queue<(TypeNode.Fqn, bool)>();
        foreach (var i in cls.Interfaces) queue.Enqueue((i, true));
        if (cls.Base != null) queue.Enqueue((cls.Base, false));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var (spec, isInterface) = queue.Dequeue();
            if (!seen.Add(TypeKey(spec))) continue;
            yield return (spec, isInterface);
            if (!defs.TryGetValue(spec.Name, out var def)) continue;
            var args = EffectiveArgs(spec, def.Arity);
            if (args == null) continue;
            foreach (var parent in def.Interfaces) queue.Enqueue(((TypeNode.Fqn)SubstOwnerTvs(parent, args), true));
            if (def.Base != null) queue.Enqueue(((TypeNode.Fqn)SubstOwnerTvs(def.Base, args), false));
        }
    }

    static TypeNode[] ClassOwnArgs(Def def) =>
        Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

    static TypeNode[] EffectiveArgs(TypeNode.Fqn spec, int arity)
    {
        if (arity == 0) return Array.Empty<TypeNode>();
        return spec.Args is { } args && args.Length == arity ? args : null;
    }

    static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstOwnerTvs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args)),
        TypeNode.ByRef r => new TypeNode.ByRef(SubstOwnerTvs(r.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args)),
        _ => type,
    };

    static string TypeKey(TypeNode t) => TypeJson.Write(t).ToJsonString();
    static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
