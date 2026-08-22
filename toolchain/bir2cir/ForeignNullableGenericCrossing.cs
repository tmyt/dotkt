using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE ONE SHAPE CARRIER-ARGUMENT ERASURE CANNOT MEET (#86).
//
// Kotlin's `X?` for a possibly-value `X` is `System.Object` in every reified ARGUMENT, so a Kotlin `List<Int?>` is an
// `IReadOnlyList<object>` and there is no Kotlin type whose physical form is `List<Nullable<int32>>`. A .NET API may
// nevertheless DECLARE one — `List<int?>`, `Dictionary<string, int?>`, `int?[]`, `Func<int?, string>` — and a
// resolved foreign declaration is authoritative: NullableGenericErasure does not restate what a CLR member declares.
//
// The two therefore do not meet, and neither side can be bent to the other:
//   * `List<object>` and `List<Nullable<int32>>` are unrelated INVARIANT reified generics; no `castclass` joins them
//     (one throws) and no covariance relates them, because a reified value-type argument has none.
//   * Adapting silently — copying into a fresh `List<int?>` at the call, or wrapping — would give the argument
//     different identity and different mutation semantics than the Kotlin source says it has. An adapter that
//     changes what `xs.add(1)` does to the caller's list is a wrong program, not a bridge.
//
// So the crossing is REFUSED, naming the member and the slot. That is the same discipline as the other refusals in
// this family: a program with no valid CIL lowering owes its author an actionable message rather than a silently
// different meaning. The refusal is narrow by construction — it needs a .NET member whose signature genuinely nests
// a `Nullable<V>` inside a reified argument, which the BCL surface almost never does — and a DIRECT `Nullable<V>`
// parameter or return is untouched, because a Kotlin scalar `Int?` IS a `System.Nullable<int32>` and crosses exactly.
//
// WHICH NODES ARE ASKED is the presence of a stamped .NET declaration (`resolvedMemberParams`/`resolvedMemberReturn`), not a list of node
// kinds: those keys exist on exactly the nodes ClrMemberResolution resolved, so the trigger cannot drift from the
// stamping. That reaches a bound method reference, an event accessor and an accessor-backed external field — each of
// which carries a declaration and none of which a kind list assembled by hand had included.
//
// WHICH POSITIONS COUNT is not decided here. `NullableGenericErasure.ErasureWouldMove` answers it, beside the `Erase`
// it has to agree with position for position: a delegate PARAMETER keeps a concrete `V?` in that rule, so a foreign
// `Func<int?, string>` parameter is inhabited exactly and is NOT a crossing, while a delegate RETURN, a type argument
// and an array element are. A second copy of that walk lived here and said the opposite about delegate parameters,
// which refused programs Kotlin runs.
//
// Runs on the LOWERED tree, where `resolvedMemberParams`/`ret` are the final CLR signature: earlier the same node may still be
// mid-resolution and a Kotlin-vocabulary `Nullable(Tv)` would be read as a foreign declaration it is not.
static class ForeignNullableGenericCrossing
{
    public static void Check(JsonNode root, string file) => Walk(root, file);

    // THE SAME CROSSING AT THE IMPLEMENTING POSITION. A call is not the only way to meet an uninhabitable slot: a
    // Kotlin class can DERIVE from a .NET type that declares one — `class C : ITake` for a C# `interface ITake {
    // string Take(List<int?> xs); }` — and there the crossing is in the slot the class must fill, not in anything it
    // calls. Nothing above sees it, because no node resolves against a member; the class compiled clean and died at
    // load with "Signature of the body and declaration in a method implementation do not match", or, for the
    // abstract base twin, "does not have an implementation".
    //
    // FILLING THE SLOT FROM THE REFLECTED DECLARATION IS NOT A FIX, though the reflected signature is right there to
    // copy. A method emitted with the declaration's own `List<Nullable<int32>>` parameter would still have a Kotlin
    // BODY, and that body reads its parameter as the `List<object>` Kotlin says it is — the identical pair of
    // unrelated invariant reified generics the call-side refusal exists to prevent, except silent rather than
    // load-time. Our body model has no way to state a body whose parameter type no Kotlin expression inhabits: the
    // author would be writing an override they cannot name their own argument in, so they are owed the message
    // rather than a TypeLoadException with our type's name on it.
    //
    // ASKED OF EVERY PROVENANCE. The carrier machinery that repairs erased slots reads DotKt metadata and so covers
    // only DotKt-authored supertypes; a plain BCL or third-party interface has none, which is exactly the column
    // that fell through. This asks the REFLECTED declaration, which every referenced assembly has.
    //
    // OVER THE WHOLE GRAPH, AND OVER THE WHOLE COMPILATION. `SupertypeGraph` is the same walk the override-slot
    // bridge uses, so a .NET interface reached only through another .NET interface (`class C : IDerived`, the slot
    // on `IBase`) and one reached only through a Kotlin interface declared in a sibling FILE are both reached. Only
    // a walk over every root at once can do the second, which is why this half does not run per file the way the
    // call-side sweep below does.
    //
    // AND IT CONSUMES THE RECORD IT ASKS. The pre-erasure Kotlin type of an erased declaration slot is a
    // pass-to-pass fact: RoundtripMetadata mints it into `[KotlinNullableGeneric]` in a ref/app build, and in the
    // runtime build — which mints nothing — it is still on the slot when this runs, because this is its last
    // reader. Either way `nullableGeneric`/`nullableGenericRet` must not reach CIR, so this drops them, exactly
    // as the call-side sweep below drops `resolvedMemberReturn`. That is also why bir2cir writes no CIR file until this has
    // run: a file serialized inside the lowering loop would freeze the record of every file but the last.
    public static void CheckImplementedSlots(IReadOnlyList<(JsonNode Root, string File)> roots,
        ReferenceMetadataIndex refs)
    {
        var defs = SupertypeGraph.Collect(roots.Select(r => r.Root));
        foreach (var (root, file) in roots)
            if (root is JsonObject o && o["types"] is JsonArray types)
                foreach (var t in types.OfType<JsonObject>())
                    CheckTypeSlots(t, defs, refs, file);
        foreach (var (root, _) in roots) DropSlotRecords(root);
    }

    static void DropSlotRecords(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("nullableGeneric");
                obj.Remove("nullableGenericRet");
                foreach (var kv in obj) if (kv.Value != null) DropSlotRecords(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) DropSlotRecords(it);
                break;
        }
    }

    static void CheckTypeSlots(JsonObject to, IReadOnlyDictionary<string, SupertypeGraph.Def> defs,
        ReferenceMetadataIndex refs, string file)
    {
        if (to["types"] is JsonArray nested)
            foreach (var n in nested.OfType<JsonObject>()) CheckTypeSlots(n, defs, refs, file);

        if (Str(to["name"]) is not string typeName || !defs.TryGetValue(typeName, out var cls) || cls.Node != to)
            return;

        // WHO IS OBLIGED TO FILL AN ABSTRACT SLOT. Only a type that must be instantiable: a Kotlin `interface KI :
        // ITake` and an `abstract class KA : BTake()` inherit the obligation without discharging it, and refusing
        // them refuses a program that has a perfectly good lowering — they emit no body at all. Their concrete
        // subclasses are asked instead, and reach the same slot through this walk.
        var mustFillAbstract = Str(to["kind"]) != "interface" && !Bool(to["abstract"]);

        // THE SLOTS THIS TYPE INHERITS, grouped by CLR identity — name, method generic ARITY (ECMA-335 I.8.6.1.6
        // makes it part of the signature, so `M<T>(int)` and `M(int)` are two slots) and the declared parameter
        // vector. A group is one slot seen from several supertypes, and its abstractness is the group's: `class B :
        // A` overriding an abstract `A.M` contributes a CONCRETE `M` under the same key, so a Kotlin `class C : B()`
        // inherits an obligation that is already discharged and must not be refused for it.
        //
        // IN THE SPEC'S OWN FRAME. The reflected declaration is the OPEN definition's — `Base<T>.Put(T, List<int?>)`
        // — and the class that derives from `Base<String>` declares `Put(String, List<object>)`. Left open, the
        // comparison below fails at the type-variable and the uninhabitable override goes through, so the spec's
        // arguments are substituted in exactly as the override-slot bridge substitutes them.
        var groups = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (var (spec, _) in SupertypeGraph.Reachable(cls, defs, refs))
        {
            if (defs.ContainsKey(spec.Name)) continue;   // declared here: erased consistently with its users
            var supArgs = spec.Args ?? Array.Empty<TypeNode>();
            foreach (var m in ReflectedSlots(spec))
            {
                var ps = m.Params.Select(p => SupertypeGraph.SubstOwnerTvs(p, supArgs)).ToArray();
                var ret = m.Ret == null ? null : SupertypeGraph.SubstOwnerTvs(m.Ret, supArgs);
                var key = m.Name + "`" + m.Arity + "(" + string.Join(",", ps.Select(SupertypeGraph.TypeKey)) + ")";
                if (!groups.TryGetValue(key, out var slot))
                    groups[key] = slot = new Slot
                    {
                        Name = m.Name, Owner = m.Owner ?? spec.Name, Arity = m.Arity, Params = ps, Ret = ret,
                    };
                if (m.Implemented) slot.Implemented = true;
            }
        }

        foreach (var slot in groups.Values)
        {
            var crossing = Crossing(slot);
            if (crossing == null) continue;
            // OBLIGED, and not merely inheriting. An undischarged abstract slot must be filled by an instantiable
            // type; a slot that already has an implementation is only this type's problem where this type OVERRIDES
            // it — and "overrides it" is decided by the signature the override would physically state, not by the
            // member's name and parameter count, which cannot tell `Take(List<int?>)` from an unrelated
            // `Take(string)` sibling the author actually wrote.
            if (!((mustFillAbstract && !slot.Implemented) || Declares(to, slot, refs))) continue;
            throw RefuseSlot(file, Str(to["name"]) ?? "<type>", slot.Owner, slot.Name, crossing.Value.Where,
                crossing.Value.Type);
        }
    }

    sealed class Slot
    {
        public string Name;
        public string Owner;
        public int Arity;
        public TypeNode[] Params;
        public TypeNode Ret;
        public bool Implemented;
    }

    // The virtual slots a referenced type DECLARES, in this pass's vocabulary, read off its OPEN definition. Cached
    // by name AND generic arity — which is what selects the definition, so a generic type and a same-named
    // non-generic sibling are different entries — because the graph above is walked once per deriving type and a
    // common supertype sits on very many of them; without the cache the stdlib build reflects the same BCL
    // interface thousands of times.
    //
    // EVERY VIRTUAL MEMBER IS A SLOT, accessors included — a C# `List<int?> Items { get; }` is a virtual `get_Items`
    // carrying IsSpecialName, and so are `add_E`/`remove_E` for an event and `get_Item` for an indexer. Skipping
    // special names left a Kotlin property override emitting the mismatched slot and dying at load exactly as a
    // method override did, and the same is true of the other two.
    //
    // AN EXPLICIT INTERFACE IMPLEMENTATION DISCHARGES ITS SLOT. Reflection names one
    // `<Namespace>.<Interface>.<Member>`, so it lands under a key of its own and the interface's abstract member
    // stayed marked unimplemented — which made a Kotlin class deriving from a .NET class that already implements the
    // crossing interface owe a slot it does not. It is recorded under BOTH names: its own, and the member name the
    // interface declares.
    //
    // The OWNER is the member's DECLARING type, not the supertype the walk happened to reach it through: reflection
    // hands a class its inherited members too, and the message must name the type that states the slot.
    static readonly Dictionary<string, Slot[]> ReflectedSlotCache = new(StringComparer.Ordinal);

    static Slot[] ReflectedSlots(TypeNode.Fqn spec)
    {
        var cacheKey = spec.Name + "`" + (spec.Args?.Length ?? 0);
        if (ReflectedSlotCache.TryGetValue(cacheKey, out var cached)) return cached;
        var result = new List<Slot>();
        var open = ClrMemberResolution.ResolveOwnerType(spec);
        MethodInfo[] members = Array.Empty<MethodInfo>();
        if (open != null)
            try { members = open.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
            catch (NotSupportedException) { }
        foreach (var m in members)
        {
            if (!m.IsVirtual) continue;
            try
            {
                var slot = new Slot
                {
                    Name = m.Name,
                    Owner = DeclaringName(m) ?? spec.Name,
                    Arity = m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0,
                    Params = m.GetParameters().Select(p => ClrMemberResolution.MemberSigOf(p.ParameterType)).ToArray(),
                    Ret = m.ReturnType == typeof(void) ? null : ClrMemberResolution.MemberSigOf(m.ReturnType),
                    Implemented = !m.IsAbstract,
                };
                result.Add(slot);
                // The interface member an explicit implementation fills. Its own name is qualified, so without this
                // the interface's abstract declaration never meets the body that discharges it.
                var dot = slot.Name.LastIndexOf('.');
                if (!m.IsAbstract && dot > 0)
                    result.Add(new Slot
                    {
                        Name = slot.Name[(dot + 1)..], Owner = slot.Owner, Arity = slot.Arity,
                        Params = slot.Params, Ret = slot.Ret, Implemented = true,
                    });
            }
            catch (Exception e) when (e is NotSupportedException or TypeLoadException or FileNotFoundException) { }
        }
        return ReflectedSlotCache[cacheKey] = result.ToArray();
    }

    static string DeclaringName(MethodInfo m)
    {
        try
        {
            return m.DeclaringType != null
                   && ClrMemberResolution.MemberSigOf(m.DeclaringType) is TypeNode.Fqn f
                ? f.Name : null;
        }
        catch (Exception e) when (e is NotSupportedException or TypeLoadException or FileNotFoundException)
        {
            return null;
        }
    }

    // The first position of this slot the erasure moves, if any — the one the message names.
    static (string Where, TypeNode Type)? Crossing(Slot slot)
    {
        for (var i = 0; i < slot.Params.Length; i++)
            if (NullableGenericErasure.ErasureWouldMove(slot.Params[i]))
                return ("parameter " + i, slot.Params[i]);
        if (slot.Ret != null && NullableGenericErasure.ErasureWouldMove(slot.Ret)) return ("return", slot.Ret);
        return null;
    }

    // Does this type declare a BODY that fills this slot? The declaration cannot equal the slot — the crossing
    // position is precisely where it cannot — so it is compared against the slot's ERASED IMAGE, which is what a
    // Kotlin declaration filling it physically states. `NullableGenericErasure.ErasedLoweredSlot` is that image, and
    // it is the identity at every position the erasure leaves alone, so an untouched parameter still has to match
    // exactly and a sibling overload of the same name and arity is not mistaken for this one. Where the image
    // coincides with a slot some sibling states OUTRIGHT, the two are told apart by the erasure's own record —
    // see below.
    //
    // A BODY, and not merely a declaration: Kotlin re-declares an inherited abstract member on the deriving
    // interface as a fake override, so `interface KI : ITake` carries a `Take` of its own. That states the slot
    // again and fills nothing. It is told apart by the body being EMPTY — kotc emits `body: []` for a fake override
    // and carries no `abstract` flag on it, while a body the author wrote has at least its own return, so even an
    // `override fun Put(xs: List<Int?>) {}` (which IS unfillable, and is refused) has a statement in it.
    //
    // The RETURN is deliberately not compared, and it needs no record either. Kotlin lets an override narrow it,
    // and a narrowed return is a different pass's business (`CovariantInterfaceReturnBridge`); name, method
    // generic arity and the whole parameter vector identify the CLR slot on their own. The parameter comparison
    // below has a second job the return has no counterpart for — telling this slot's body from a sibling that
    // states the same physical signature — and a RETURN-position crossing cannot pose that question at all: two
    // members with one name, one arity and one parameter vector differing only in return type are two CLR slots
    // but ONE Kotlin declaration, which the frontend rejects ("return type is not a subtype of the return type of
    // the overridden member"). So no accepted program reaches here with a return whose owner is in doubt, and
    // asking the record about it could only drop a refusal that is owed.
    //
    // WHICH SOURCE SLOT A BODY BELONGS TO IS NOT A PHYSICAL QUESTION, and asking it physically is how a sibling
    // came to answer for the crossing. The image of `Take(List<int?>)` is `Take(List<object>)`, which a sibling may
    // declare FOR REAL — and then two different Kotlin overrides, `Take(xs: List<Int?>)` and
    // `Take(ys: List<Any?>)`, state that one physical signature. Only the first is the crossing's; the second fills
    // the sibling and owes it nothing. The two are told apart by the fact this erasure itself recorded on the
    // declaration: at a position it MOVED, the parameter carries the PRE-ERASURE Kotlin type on
    // `[KotlinNullableGeneric]`. So a body is this slot's only where, at every PARAMETER this slot crosses at, it
    // records the type THIS slot states (the return cannot pose the question — see above).
    //
    // AND THE RECORD IS READ, NOT COUNTED. Presence alone is the same conflation one level in: `List<Boolean?>` is
    // recorded exactly as `List<Int?>` is and erases to the same `List<object>`, so a body that legitimately fills a
    // DotKt supertype's `Take(List<Boolean?>)` would answer for the foreign `Take(List<int?>)` slot it never
    // mentions — refusing a program that has a perfectly good lowering.
    //
    // Deciding it by "some other slot already states that signature" instead let ANY body of that shape off, which
    // is the silent miscompile the refusal exists to prevent: the CLR binds the emitted body to the `object` slot
    // and a call through `Take(List<int?>)` runs the BASE implementation.
    static bool Declares(JsonObject to, Slot slot, ReferenceMetadataIndex refs)
    {
        if (to["methods"] is not JsonArray methods) return false;
        // The reflected slot reader uses document type spelling while current-format declarations may already carry
        // an exact metadata TypeDef name. Compare both through the reference index's authoritative physical identity;
        // stripping arity here would merge legal nested/same-flattened-arity collisions, which is the defect #505
        // removes.
        var want = slot.Params.Select(p => PhysicalNorm(NullableGenericErasure.ErasedLoweredSlot(p), refs)).ToArray();
        var moved = slot.Params.Select(NullableGenericErasure.ErasureWouldMove).ToArray();
        foreach (var m in methods.OfType<JsonObject>())
        {
            if (Bool(m["static"]) || Bool(m["abstract"]) || Str(m["name"]) != slot.Name) continue;
            if (m["body"] is not JsonArray body || body.Count == 0) continue;
            if (((m["typeParams"] as JsonArray)?.Count ?? 0) != slot.Arity) continue;
            if (m["params"] is not JsonArray ps || ps.Count != want.Length) continue;
            var ok = true;
            for (var i = 0; i < ps.Count && ok; i++)
            {
                var po = ps[i] as JsonObject;
                ok = TypeJson.Read(po?["type"]) is TypeNode t && PhysicalNorm(t, refs) == want[i]
                     && (!moved[i] || StatesSlot(po, slot.Params[i], refs));
            }
            if (ok) return true;
        }
        return false;
    }

    // Does this declaration slot record THIS crossing's pre-erasure type? The erasure says what it moved on the
    // round-trip channel it writes for exactly that purpose — the raw stash while it is still a key, and the
    // `[KotlinNullableGeneric]` attribute once RoundtripMetadata has minted it. Both are read because this check
    // runs after the minting in a ref/app build, while the runtime build mints nothing and reaches here with the
    // stash still on the slot (`CheckImplementedSlots` is what consumes it).
    static bool StatesSlot(JsonObject decl, TypeNode slotType, ReferenceMetadataIndex refs)
        => decl != null && PreErasureTypeOf(decl) is TypeNode pre
           && SameSlot(pre, slotType, argument: false, refs.Aliases);

    // The recorded pre-erasure type of a declaration slot, from whichever of the two forms of the one record this
    // build has reached: the stash, or the attribute this compilation minted from it. The attribute is matched by
    // its exact FQN — the one RoundtripMetadata stamps — rather than by how the name ends, so no other assembly's
    // similarly-named attribute can answer for it.
    static TypeNode PreErasureTypeOf(JsonObject decl)
    {
        if ((decl["nullableGeneric"] as JsonValue)?.TryGetValue<string>(out var stash) == true)
            return TypeNode.Parse(stash);
        if (decl["attrs"] is not JsonArray attrs) return null;
        foreach (var a in attrs.OfType<JsonObject>())
            if (TypeJson.Read(a["attr"]) is TypeNode.Fqn { Args: null } f
                && f.Name == RoundtripMetadata.AKNullableGen
                && a["args"] is JsonArray args && args.Count >= 2
                && Str((args[0] as JsonObject)?["value"]) is string version
                && Str((args[1] as JsonObject)?["bytes"]) is string payload)
                return TypeJson.Read(BirCarrier.DecodeBody(version, Convert.FromBase64String(payload)));
        return null;
    }

    // Is the recorded pre-erasure type the one this foreign slot states? ONLY THE MOVED POSITIONS ARE COMPARED,
    // because they are the only ones with anything left to say: every position the erasure left alone survived
    // physically and was matched exactly by the caller, so re-asking about it can only add ways to disagree.
    //
    // The record states the KOTLIN type the author wrote (`List<Int?>`) and the foreign declaration the CLR one
    // (`List<Nullable<int32>>`). At a moved position the two name the same type through the stdlib's own
    // `@ClrTypeAlias` — `kotlin.Int` IS `System.Int32` — which is the same map every other Kotlin-to-CLR name
    // decision in this layer reads, so no second correspondence is invented here. Everywhere else the walk only
    // needs the two SHAPES to correspond, and where they do not it asks whether anything below moved at all: if
    // nothing did, there was nothing there to tell one slot from another.
    //
    // Positions are read off `NullableGenericErasure.Erase`: an Fqn's arguments, an array's element and a
    // delegate's RETURN are arguments; a byref referent, a nullable's inner and a delegate's PARAMETERS are slots.
    static bool SameSlot(TypeNode carrier, TypeNode slot, bool argument, IReadOnlyDictionary<string, string> aliases)
    {
        carrier = Bare(carrier);
        slot = Bare(slot);
        // THE MOVED POSITION — the one the physical comparison could not see, and so the one that must agree.
        if (argument && slot is TypeNode.Nullable sn)
            return carrier is TypeNode.Nullable cn && PreKey(cn.Of, aliases) == PreKey(sn.Of, aliases);
        switch (carrier, slot)
        {
            case (TypeNode.Fqn { Args: { } ca }, TypeNode.Fqn { Args: { } sa }) when ca.Length == sa.Length:
                for (var i = 0; i < ca.Length; i++) if (!SameSlot(ca[i], sa[i], true, aliases)) return false;
                return true;
            case (TypeNode.Array c, TypeNode.Array s):
                return SameSlot(c.Elem, s.Elem, true, aliases);
            case (TypeNode.ByRef c, TypeNode.ByRef s):
                return SameSlot(c.Of, s.Of, false, aliases);
            case (TypeNode.Nullable c, TypeNode.Nullable s):
                return SameSlot(c.Of, s.Of, false, aliases);
            case (TypeNode.Fn c, TypeNode.Fn s) when FnSlots(c).Length == FnSlots(s).Length:
                if (!SameSlot(c.Ret, s.Ret, true, aliases)) return false;
                TypeNode[] cs = FnSlots(c), ss = FnSlots(s);
                for (var i = 0; i < cs.Length; i++) if (!SameSlot(cs[i], ss[i], false, aliases)) return false;
                return true;
            default:
                return !(argument
                    ? NullableGenericErasure.ErasureWouldMoveArgument(slot)
                    : NullableGenericErasure.ErasureWouldMove(slot));
        }
    }

    // An NRT-OBLIVIOUS wrapper is an annotation on a type rather than a position in it.
    static TypeNode Bare(TypeNode t) => t is TypeNode.Oblivious o ? Bare(o.Of) : t;

    // A function type's SLOT positions, in the order the delegate states them: the contexts lead, then the
    // receiver, then the declared parameters — which is what `[KotlinContextFunctionType]`/
    // `[KotlinExtensionFunctionType]` say about the physical delegate. `Erase` treats all three alike, so a walk
    // that compared only `Params` desynchronized against a foreign `Func<…>` the moment either was present.
    static TypeNode[] FnSlots(TypeNode.Fn fn)
    {
        var slots = new List<TypeNode>();
        if (fn.Ctx != null) slots.AddRange(fn.Ctx);
        if (fn.Recv != null) slots.Add(fn.Recv);
        slots.AddRange(fn.Params);
        return slots.ToArray();
    }

    // One spelling for a PRE-ERASURE name, so a Kotlin record and a CLR declaration compare: the @ClrTypeAlias
    // index on top of the same top-type normalization the physical comparison uses.
    static string PreKey(TypeNode t, IReadOnlyDictionary<string, string> aliases) => Norm(Alias(t, aliases));

    static TypeNode Alias(TypeNode t, IReadOnlyDictionary<string, string> aliases) => t switch
    {
        TypeNode.Oblivious o => Alias(o.Of, aliases),
        TypeNode.Fqn f => new TypeNode.Fqn(aliases.TryGetValue(f.Name, out var bcl) ? bcl : f.Name,
            f.Args?.Select(a => Alias(a, aliases)).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Alias(a.Elem, aliases)),
        TypeNode.Nullable n => new TypeNode.Nullable(Alias(n.Of, aliases)),
        TypeNode.ByRef b => new TypeNode.ByRef(Alias(b.Of, aliases)),
        _ => t,
    };

    // One spelling per CLR type, so a reflected declaration and a lowered Kotlin one compare. They agree everywhere
    // except the top type, which reflection names `System.Object` and the lowering names `object`, and the NRT
    // OBLIVIOUS wrapper, which is an annotation on a type rather than a type.
    static string Norm(TypeNode t) => SupertypeGraph.TypeKey(Canon(t));

    static string PhysicalNorm(TypeNode t, ReferenceMetadataIndex refs) =>
        SupertypeGraph.TypeKey(Exact(Canon(t), refs));

    static TypeNode Exact(TypeNode t, ReferenceMetadataIndex refs) => t switch
    {
        TypeNode.Fqn f => new TypeNode.Fqn(
            refs.ExactReflectedOwner(f.Name, f.Args?.Length ?? 0),
            f.Args?.Select(a => Exact(a, refs)).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Exact(a.Elem, refs)),
        TypeNode.Nullable n => new TypeNode.Nullable(Exact(n.Of, refs)),
        TypeNode.ByRef b => new TypeNode.ByRef(Exact(b.Of, refs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Exact(o.Of, refs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Exact(fn.Ret, refs),
            fn.Params.Select(p => Exact(p, refs)).ToArray(),
            fn.Recv == null ? null : Exact(fn.Recv, refs), fn.Clr,
            fn.Ctx?.Select(p => Exact(p, refs)).ToArray()),
        _ => t,
    };

    static TypeNode Canon(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => Canon(o.Of),
        TypeNode.Fqn { Name: "System.Object", Args: null } => new TypeNode.Fqn("object"),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name, args.Select(Canon).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Canon(a.Elem)),
        TypeNode.Nullable n => new TypeNode.Nullable(Canon(n.Of)),
        TypeNode.ByRef b => new TypeNode.ByRef(Canon(b.Of)),
        _ => t,
    };

    static InvalidOperationException RefuseSlot(string file, string type, string owner, string member, string slot,
        TypeNode t)
        => new(
            $"bir2cir: {file}: '{type}' derives from '{owner}', whose member '{member}' declares '{Render(t)}' at its "
            + $"{slot} — a slot NO Kotlin expression inhabits. A nullable value type inside a generic argument, an "
            + "array element or a delegate return is System.Object in Kotlin, so the Kotlin method filling "
            + "this slot would receive a 'List<object>' where the declaration says 'List<Nullable<Int32>>' — "
            + "unrelated invariant reified generics that no conversion relates. Emitting the declaration's own "
            + "signature would not help: no Kotlin type states that position, so the body could not name the value "
            + "it is handed and the mismatch would move from load time into the body. Change the .NET surface "
            + "(a slot whose argument is object-typed, or whose element is not a nullable value type), or implement "
            + "this interface on the .NET side.");


    static void Walk(JsonNode node, string file)
    {
        switch (node)
        {
            case JsonObject obj:
                // THE STAMPED DECLARATION IS THE TRIGGER, not a list of node kinds. `resolvedMemberParams`/`resolvedMemberReturn` exist on
                // exactly the nodes ClrMemberResolution resolved against a .NET member — including an accessor-backed
                // external `field`, whose KIND is Kotlin's too — so keying on them is keyed on the fact itself and
                // cannot drift from where the stamping happens. The reference is that stamp now; `resolvedMemberParams` was.
                if (obj["memberRef"] != null || obj[ClrMemberResolution.ResolvedMemberReturnKey] != null) CheckCall(obj, file);
                // `resolvedMemberReturn` is a pass-to-pass fact and must not reach CIR: the emitter consumes the reference and
                // knows nothing of this one.
                obj.Remove(ClrMemberResolution.ResolvedMemberReturnKey);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, file);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, file);
                break;
        }
    }


    /// <summary>
    /// A reference spells a value type's nullability the way METADATA does — `System.Nullable`1&lt;V&gt;` — while the
    /// erasure question is asked of the document's `nullable` wrapper. Same fact, two vocabularies; this is where
    /// they meet, so this is where the bridge belongs rather than inside the erasure rule.
    /// </summary>
    static TypeNode AsDocumentNullable(TypeNode t) => t switch
    {
        TypeNode.Fqn { Args: { Length: 1 } a } f when IsNullableName(f.Name) =>
            new TypeNode.Nullable(AsDocumentNullable(a[0])),
        // A lowered function type read back as its delegate. The document calls it `fn`, and the erasure rule is
        // positional: a function type's parameters and return are method slots, an ordinary generic's arguments
        // are storage. Restoring the shape is what keeps the question the same one `resolvedMemberParams` used to ask.
        TypeNode.Fqn { Args: { Length: > 0 } fa } f when BirTypeLowering.IsLoweredFunctionType(StripArity(f.Name)) =>
            // An Action has no return: every argument is a parameter. A Func's last argument is its return.
            StripArity(f.Name).EndsWith("Action", StringComparison.Ordinal)
                ? new TypeNode.Fn(false, UnitReturn, fa.Select(AsDocumentNullable).ToArray())
                : new TypeNode.Fn(false, AsDocumentNullable(fa[^1]),
                    fa.Take(fa.Length - 1).Select(AsDocumentNullable).ToArray()),
        TypeNode.Fqn { Args: not null } f =>
            new TypeNode.Fqn(f.Name, f.Args.Select(AsDocumentNullable).ToArray()),
        TypeNode.Array arr => arr.SzArray
            ? new TypeNode.Array(AsDocumentNullable(arr.Elem))
            : TypeNode.Array.General(AsDocumentNullable(arr.Elem), arr.Rank),
        TypeNode.ByRef b => new TypeNode.ByRef(AsDocumentNullable(b.Of)),
        _ => t,
    };

    static readonly TypeNode UnitReturn = new TypeNode.Fqn("kotlin.Unit");

    static string StripArity(string s) { var i = s.IndexOf('`'); return i >= 0 ? s[..i] : s; }

    static bool IsNullableName(string name) =>
        name is "System.Nullable" or "System.Nullable`1";

    static void CheckCall(JsonObject call, string file)
    {
        // A call names its member in `method`; a property/field access names it in `name`; a `newClr` names none.
        var member = Str(call["method"]) ?? Str(call["name"]) ?? ".ctor";
        // The owner key differs by node: a call and a property access name it in `type`, a bound method reference in
        // `clrType`, an accessor-backed field in `ownerType`. The message must name the member the author wrote.
        var owner = (TypeJson.Read(call["type"]) ?? TypeJson.Read(call["clrType"]) ?? TypeJson.Read(call["ownerType"]))
            is TypeNode.Fqn f ? f.Name : "<unknown>";
        // The parameter vector comes off the resolved reference — the declaration's own, which is the whole point:
        // the node's argument types are the caller's Kotlin view and would hide the crossing.
        if (call["memberRef"] is JsonObject reference && reference["parameterTypes"] is JsonArray sig)
            for (var i = 0; i < sig.Count; i++)
                if (TypeJson.Read(sig[i]) is TypeNode p && NullableGenericErasure.ErasureWouldMove(AsDocumentNullable(p)))
                    throw Refuse(file, owner, member, "parameter " + i, AsDocumentNullable(p));
        // The RETURN is read off the stamped FOREIGN declaration, never off the node's own `ret`: that one is the
        // caller's Kotlin view and has already been erased as a Kotlin slot, so it says `List<object>` for a member
        // declaring `List<int?>` and the crossing would be invisible.
        if (TypeJson.Read(call[ClrMemberResolution.ResolvedMemberReturnKey]) is TypeNode ret
            && NullableGenericErasure.ErasureWouldMove(ret))
            throw Refuse(file, owner, member, "return", ret);
    }

    // WHAT THE MESSAGE MAY OFFER is only what actually works. Constructing the .NET type by hand does NOT: a Kotlin
    // `System.Collections.Generic.List<Int?>()` erases its own argument the same way and builds a `List<object>`, so
    // there is no expression in the language whose physical type is `List<Nullable<Int32>>`. Naming that as a remedy
    // sends the author around a loop that ends where it started, so the refusal names the two things that do move:
    // a different .NET surface, or keeping the value on the .NET side of the boundary.
    static InvalidOperationException Refuse(string file, string owner, string member, string slot, TypeNode t)
        => new(
            $"bir2cir: {file}: the .NET member '{owner}.{member}' declares '{Render(t)}' at its {slot}, which NO "
            + "Kotlin expression inhabits. A nullable value type inside a generic argument, an array element or a "
            + "delegate return is System.Object in Kotlin, so a Kotlin 'List<Int?>' is an "
            + "IReadOnlyList<object> and is not a List<Nullable<Int32>> — unrelated invariant reified generics that "
            + "no conversion relates, and constructing the .NET type from Kotlin erases its argument the same way. "
            + "Change the .NET surface (an overload whose argument is object-typed, or whose element is not a "
            + "nullable value type), or build and pass the value entirely on the .NET side.");


    static string Render(TypeNode t) => t switch
    {
        TypeNode.Nullable n => "System.Nullable<" + Render(n.Of) + ">",
        TypeNode.Oblivious o => Render(o.Of),
        TypeNode.Array a => Render(a.Elem) + "[]",
        TypeNode.ByRef b => "ref " + Render(b.Of),
        TypeNode.Fqn { Args: { } args } fa => fa.Name + "<" + string.Join(", ", args.Select(Render)) + ">",
        TypeNode.Fqn f => f.Name,
        TypeNode.Fn fn => "(" + string.Join(", ", fn.Params.Select(Render)) + ") -> " + Render(fn.Ret),
        _ => t.ToString(),
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
