using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using Def = SupertypeGraph.Def;

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
// interface, its own base interfaces (including a synthesized existential view), and the base-class
// chain — and every one of those declares its own CLR slot even when the signatures coincide. The bridges are keyed by
// signature so the several slots share one body, and each contributes its own resolved MethodImpl descriptor. The walk
// itself is `SupertypeGraph`, shared with the crossing refusal that asks the same graph a different question.
//
// NESTED POSITIONS CANNOT BE BRIDGED, and are the one case where the declaration still moves. A base `Box<T?>` erases
// to `Box<object>`, and `Box<object>` and `Box<Nullable<int32>>` are unrelated invariant reified generics that no cast
// converts — there is no forwarding body to write. Such a position is rewritten in the override's declaration to the
// base's shape, with the override's own pre-erasure Kotlin type recorded on the two round-trip channels every erased
// slot uses (the `[KotlinNullableGeneric]` carrier and the slot's NRT byte), so the surface survives even though the
// physical type had to move. The split is exactly the CLR's: a bare `object` seam is one instruction in each
// direction, and a difference under a constructed generic is not expressible at all.
//
// A BASE DECLARED IN A REFERENCED ASSEMBLY IS READ, NOT SKIPPED. The supertype graph of the current compilation
// answers a same-module base; a base declared elsewhere — a referenced DotKt library, and the STDLIB, which is where
// `kotlin.Comparable` lives — is answered by the same D1 carrier reader every other referenced-declaration
// derivation uses (`ReferenceMetadataIndex.TryNullableGenericSlot`: the producing assembly's
// `[KotlinNullableGeneric]` carrier where the erasure recorded one, its physical signature otherwise).
//
// The two arms ask the question from opposite ends, because that is what each side can answer. A LOCAL supertype
// hands over its slot list, so the walk goes slot -> implementer. A REFERENCED supertype has no slot list here, but
// every override that must fill one names its owner and member in its own `overrides` marker — so the walk goes
// implementer -> slot, and asks the reader for exactly the member the author said they were overriding. Neither arm
// guesses: a slot with no `overrides` claim and a claim with no readable declaration are both left alone.
//
// The DESCRIPTOR names the CLR slot, not the Kotlin member. A referenced interface may be `@ClrTypeAlias`'d onto a
// BCL one whose member has a different name (`kotlin.Comparable.compareTo` fills `System.IComparable.CompareTo`), so
// the descriptor's member is resolved through the same `@ClrIntrinsic` binding `DeclarationRename` reads for the
// declaration itself. A MethodImpl naming a member the interface does not have fails type LOAD exactly as an
// unfilled slot does.
static class KotlinOverrideSlotBridge
{
    // THE TWO HALVES RUN AT DIFFERENT POINTS, because their inputs are valid at different points.
    //
    // The DECLARATION MOVE (a nested position no cast reaches) must land before the erasure sweep and the use-side
    // realign: those are what retype the body that reads the moved parameter, and a slot moved after them leaves the
    // body reading it at the type it used to have — verifiably wrong IL.
    //
    // The BRIDGE must land after the star-projection erasure, because that pass ADDS supertypes: a `G<*>` anywhere in
    // the compilation gives `G` a synthesized existential view whose slots an implementer must fill too, and a
    // bridge built before it exists would carry a MethodImpl for the constructed interface and leave the existential
    // one unimplemented. Nothing between the two points can introduce a new nested move — an existential view's slots
    // are copies of the erased ones — so the halves do not race.
    //
    // The declaration half, over every file at once (a base may be declared in another file of this compilation).
    public static void PropagateErasedSlots(IEnumerable<JsonNode> roots, Func<string, bool> isValue,
        ReferenceMetadataIndex refs) => ApplyAll(roots, isValue, refs, emitBridges: false);

    // The bridge half.
    public static void ApplyAll(IEnumerable<JsonNode> roots, Func<string, bool> isValue, ReferenceMetadataIndex refs) =>
        ApplyAll(roots, isValue, refs, emitBridges: true);

    static void ApplyAll(IEnumerable<JsonNode> roots, Func<string, bool> isValue, ReferenceMetadataIndex refs,
        bool emitBridges)
    {
        var defs = SupertypeGraph.Collect(roots);
        foreach (var cls in defs.Values.Where(d => d.Kind == "class").ToList())
            ApplyClass(cls, defs, isValue, refs, emitBridges);
    }

    static void ApplyClass(Def cls, IReadOnlyDictionary<string, Def> defs, Func<string, bool> isValue,
        ReferenceMetadataIndex refs, bool emitBridges)
    {
        if (cls.Node["methods"] is not JsonArray methods) return;
        var ownArgs = ClassOwnArgs(cls);
        var bridges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var ordinal = 0;

        // EVERY EARLY EXIT BELOW MEANS "THIS SUPERTYPE OR SLOT IS NOT ONE THIS ERASURE DIVERGED", never "give up on a
        // slot that needs filling". A supertype absent from `defs` is declared elsewhere and goes to the referenced
        // arm rather than being dropped; an arity that does not match its spec, or a slot whose types do not read, is
        // a declaration this pass has no opinion about and leaves exactly as the other passes found it. The one
        // judgement call is `Implementer` returning null on an ambiguous overload set, and it is documented there.
        //
        // The shared tail of both arms: given a SLOT (its constructed owner, its name, its erased parameter vector and
        // return) and the class declaration that claims to fill it, decide per position whether the declaration is
        // already the slot, is one `object` seam away from it (bridge), or has to adopt it (rewrite).
        void Fill(TypeNode.Fqn spec, bool supIsInterface, string name, string descriptorMember,
            TypeNode[] slotParams, TypeNode slotRet, JsonObject impl)
        {
            var declParams = impl["params"] as JsonArray;
            var declRet = TypeJson.Read(impl["ret"]);
            if (declParams == null || declRet == null || declParams.Count != slotParams.Length) return;

            var fit = new Fit[slotParams.Length];
            for (var i = 0; i < slotParams.Length; i++)
            {
                var declT = TypeJson.Read((declParams[i] as JsonObject)?["type"]);
                if (declT == null) { fit = null; break; }
                fit[i] = Classify(slotParams[i], SupertypeGraph.SubstOwnerTvs(declT, ownArgs));
            }
            // A parameter difference this erasure did not create belongs to whatever pass did create it.
            if (fit == null || fit.Contains(Fit.Foreign)) return;
            var retFit = Classify(slotRet, SupertypeGraph.SubstOwnerTvs(declRet, ownArgs));
            if (retFit == Fit.Foreign)
            {
                // A COVARIANT return over an otherwise-exact signature is CovariantInterfaceReturnBridge's, and
                // both passes emitting a bridge would declare the slot's signature twice. It only becomes this
                // pass's when a parameter erased too — which is exactly when that pass, matching on an exact
                // parameter vector, cannot fire — and then the one bridge states the whole slot and upcasts.
                if (!fit.Any(f => f is Fit.Bridge or Fit.Rewrite)) return;
                retFit = Fit.Bridge;
            }

            // A position no conversion reaches moves the DECLARATION onto the slot's shape — the only option the
            // CLR leaves under a constructed generic — carrying its Kotlin surface on the round-trip channels.
            for (var i = 0; i < slotParams.Length; i++)
                if (fit[i] == Fit.Rewrite && declParams[i] is JsonObject po)
                    Rewrite(po, "type", "nullableGeneric", "nullableFlags", slotParams[i], isValue);
            if (retFit == Fit.Rewrite) Rewrite(impl, "ret", "nullableGenericRet", "retNullableFlags", slotRet, isValue);
            if (!emitBridges || (!fit.Contains(Fit.Bridge) && retFit != Fit.Bridge)) return;

            // The typed body is what the bridge dispatches to, so it must own a virtual slot of its own. Kotlin
            // does not require `open` to satisfy an interface, and the exact-signature normalization that would
            // otherwise mark it no longer matches — the signatures deliberately differ now.
            impl["virtual"] = true;

            // ARITY IS PART OF THE IDENTITY (ECMA-335 I.8.6.1.6), so it is part of the key. `put(T?)` and
            // `<U> put(T?)` erase to one parameter vector and are two CLR slots; one bridge shared between them is
            // wired to both, and the CLR rejects the pair ("Signature of the body and declaration in a method
            // implementation do not match"). The DESCRIPTOR carries it too, so the emitter matches on it rather than
            // taking the arity from whichever slot it is currently looking at.
            var arity = (impl["typeParams"] as JsonArray)?.Count ?? 0;
            // THE BODY IS PART OF THE IDENTITY TOO. Two slots with the same ERASED signature can be filled by two
            // DIFFERENT declarations — `class Two : A1<Int>, B1<String>` has two `accept` overloads that both erase
            // to `accept(object)` — and one bridge shared between them forwards both slots to whichever body it was
            // built for, casting a `string` into a `Nullable<int32>` at run time. Keyed by the declaration it
            // forwards to as well, the two slots get one bridge each, while the sharing that IS correct — one body
            // reached through several supertypes of the same shape — still collapses to one.
            var body = string.Join(",", (impl["params"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>().Select(pn => TypeJson.Read(pn["type"]) is TypeNode t ? SupertypeGraph.TypeKey(t) : "?"));
            var key = name + "`" + arity + "(" + string.Join(",", slotParams.Select(SupertypeGraph.TypeKey)) + ")->" + SupertypeGraph.TypeKey(slotRet)
                      + "{" + Str(impl["name"]) + "(" + body + ")}";
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
                .Add(ImplDescriptor(spec, descriptorMember, arity, slotParams, slotRet));
        }

        foreach (var (spec, supIsInterface) in SupertypeGraph.Reachable(cls, defs, refs))
        {
            if (!defs.TryGetValue(spec.Name, out var sup))
            {
                // A referenced BASE CLASS reaches the same arm; only its wiring differs (a MethodImpl against the
                // constructed base rather than the interface), and the emitter resolves that base externally.
                FillFromReference(cls, defs, spec, supIsInterface, methods, ownArgs, isValue, refs, Fill);
                continue;
            }
            var supArgs = SupertypeGraph.EffectiveArgs(spec, sup.Arity);
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
                    .Select(t => t == null ? null : SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(t, isValue), supArgs))
                    .ToArray();
                var slotRet0 = TypeJson.Read(slot["ret"]);
                if (slotParams.Any(p => p == null) || slotRet0 == null) continue;
                var slotRet = SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(slotRet0, isValue), supArgs);

                if (Implementer(cls, defs, methods, spec.Name, name, methodArity, slotParams, ownArgs) is not JsonObject impl)
                    continue;
                Fill(spec, supIsInterface, name, name, slotParams, slotRet, impl);
            }
        }
    }

    // THE REFERENCED ARM, walked implementer -> slot. A referenced supertype hands over no slot list, so the class's
    // own `overrides` markers are what name the members that must fill one: each says the OWNER the author wrote and
    // the MEMBER on it. For every own method claiming this supertype, the D1 reader answers that member's pre-erasure
    // declaration off the producing assembly, and the slot is then derived by the identical
    // `Subst(Erase(declared), typeArgs)` formula the local arm uses.
    //
    // Every `return`/`continue` here means "this pass has no opinion about that slot", never "give up on one that
    // needs filling": no reference index (a build with no references), a supertype whose arity the spec does not
    // match, a member the reader refuses (an ambiguous same-shape overload set, which it will not guess at), or a
    // parameter vector that does not read. A REFUSED position is respected exactly as it is on the argument axis —
    // the reader saw the declaration and declined to state it, and inventing a slot from the physical signature is
    // the derivation the refusal exists to prevent.
    static void FillFromReference(Def cls, IReadOnlyDictionary<string, Def> defs, TypeNode.Fqn spec,
        bool supIsInterface, JsonArray methods, TypeNode[] ownArgs, Func<string, bool> isValue,
        ReferenceMetadataIndex refs, Action<TypeNode.Fqn, bool, string, string, TypeNode[], TypeNode, JsonObject> fill)
    {
        if (refs == null) return;
        var supArgs = spec.Args ?? Array.Empty<TypeNode>();
        foreach (var impl in methods.OfType<JsonObject>().ToList())
        {
            if (Bool(impl["static"]) || Str(impl["name"]) is not string ownName) continue;
            if (Str(impl["vis"]) is not (null or "public" or "protected")) continue;
            if (impl["params"] is not JsonArray ps) continue;
            if (impl["overrides"] is not JsonArray overrides) continue;
            var methodArity = (impl["typeParams"] as JsonArray)?.Count ?? 0;
            foreach (var o in overrides.OfType<JsonObject>())
            {
                // THE MARKER SAYS WHAT WAS OVERRIDDEN; THE SPEC SAYS WHERE THE SLOT IS REACHED. Those differ the
                // moment a referenced supertype inherits the member — `class C : Derived<Int>` where `accept` is
                // declared on `Sink` — so requiring the marker's owner to EQUAL the spec skipped the slot entirely.
                // The question is asked of the SPEC instead, and the reader walks the referenced supertype graph
                // itself, mapping the declaration through each supertype's arguments into the spec's own frame.
                //
                // But the two must still be RELATED, or a marker for one supertype answers against an unrelated one
                // that merely exposes the same erased shape — `class C : A<Int>, B<Int>` with `accept(T?)` on each
                // wires both slots to whichever body the first marker named. "Not declared here" establishes only
                // externality; reachability from the spec is what makes this marker THIS spec's business.
                if (TypeJson.OwnerName(o["owner"]) is not string owner || defs.ContainsKey(owner)) continue;
                if (!SupertypeGraph.Reaches(spec.Name, owner, defs, refs)) continue;
                if (Str(o["member"]) is not string member) continue;
                // A PROPERTY marker names the Kotlin property (`v`, kind `getter`/`setter`) while the CLR slot is the
                // ACCESSOR (`get_v`/`set_v`) — the same translation DeclarationRename makes for the declaration.
                var slotMember = Str(o["kind"]) switch
                {
                    "getter" => "get_" + member,
                    "setter" => "set_" + member,
                    _ => member,
                };
                if (!refs.TryNullableGenericSlot(spec.Name, slotMember, isStatic: false, ps.Count, methodArity,
                        out var slotRet0, out var slotParams0, out var refused))
                    continue;
                if (slotParams0 == null || slotParams0.Length != ps.Count) continue;
                if (refused != null && refused.Any(r => r)) continue;
                // A null PARAMETER fact is a slot this reader cannot state, and inventing one from the physical
                // signature is the derivation its silence exists to prevent — so the member is left alone.
                if (slotParams0.Any(t => t == null)) continue;
                var slotParams = slotParams0
                    .Select(t => SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(t, isValue), supArgs))
                    .ToArray();
                // A null RETURN fact is the opposite: the reader states a return only while it still says something a
                // call site completes (a carrier, or a physical type still holding a type variable), so `null` means
                // the slot's return is not one the erasure moved and the override's own return already IS it.
                // `compareTo(T): Int` is exactly that — the parameter is the whole divergence.
                var slotRet = slotRet0 == null
                    ? SupertypeGraph.SubstOwnerTvs(TypeJson.Read(impl["ret"]), ownArgs)
                    : SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(slotRet0, isValue), supArgs);
                if (slotRet == null) continue;
                // The CLR slot's own NAME. A referenced Kotlin interface that is `@ClrTypeAlias`'d onto a BCL one
                // fills a differently-named member (`compareTo` -> `CompareTo`), and the MethodImpl has to name the
                // member the interface actually declares.
                var descriptorMember = refs.TryMemberIntrinsicExact(spec.Name, slotMember, ps.Count, out var clrName)
                    ? clrName : slotMember;
                fill(spec, supIsInterface, ownName, descriptorMember, slotParams, slotRet, impl);
                break;
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

    // WHICH PASS OWNS A DIVERGENT SLOT. `CovariantInterfaceReturnBridge` bridges a return the override narrowed, and
    // it runs first; this erasure narrows returns too, so without a boundary both fire on one slot and emit two
    // private bridges with the SAME signature and the SAME MethodImpl descriptor — and the emitter, taking the first
    // match, silently picks between them. The boundary is the divergence itself: a difference this erasure created
    // belongs to this pass, whose bridge forwards VIRTUALLY (so a further-derived override is what runs) where the
    // covariant one deliberately does not.
    public static bool IsErasureDivergence(TypeNode slot, TypeNode declared) =>
        slot != null && declared != null && !slot.Equals(declared) && ErasureAligned(slot, declared);

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
    // a synthesized existential view of `Sink`), so an ANCESTOR of an overridden owner counts too. An
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
                ok = t != null && ErasureAligned(slotParams[i], SupertypeGraph.SubstOwnerTvs(t, ownArgs));
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

    static JsonObject ImplDescriptor(TypeNode.Fqn spec, string member, int arity, TypeNode[] slotParams, TypeNode slotRet)
    {
        var ps = new JsonArray();
        foreach (var p in slotParams) ps.Add(TypeJson.Write(p));
        return new JsonObject
        {
            ["owner"] = TypeJson.Write(spec),
            ["member"] = member,
            // The slot's METHOD GENERIC ARITY. Without it the emitter takes the arity from whichever slot it is
            // matching, so a directive for the arity-0 member also answers for the arity-1 one.
            ["arity"] = arity,
            ["params"] = ps,
            ["ret"] = TypeJson.Write(slotRet),
        };
    }

    static TypeNode[] ClassOwnArgs(Def def) =>
        Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

    static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
