using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE ERASURE INVARIANT (#86): CARRIER-ARGUMENT ERASURE. For any declaration slot `s`,
// `physical(s) = Erase(declaredKotlinType(s))`, and `Erase` is POSITIONAL — one rule, read off where the type sits:
//
//   * A DIRECT slot keeps the CLR-native form. A concrete `V?` is `System.Nullable<V>` at a method return, a method
//     or constructor parameter, a field, a property, a body local and a `ref` referent alike. Only the open
//     `Nullable(Tv)` erases here, because `Nullable<T>` is not even expressible for an unconstrained `T` and a bare
//     `!T` slot collapses a null to `default(T)`.
//   * An ARGUMENT to a CLR-REIFIED CONSTRUCTION — a generic type's type argument, a generic method's type argument,
//     an array element, a delegate's return — is `System.Object` whenever the Kotlin type there is `X?` for a
//     possibly-value `X`: an open `Tv` (some instantiation is a struct) or a value `Fqn` (this one is).
//     So `List<Int?>` is `IReadOnlyList<object>`, `Box<Int?>` is `Box<object>`, `f<Int?>()` instantiates at `object`,
//     `Comparable<Int?>` is `Comparable<object>`, `Array<Int?>` is `object[]` and `(Int) -> Int?` is
//     `Func<int32, object>`. References keep their normal CLR representation: `List<String?>` is
//     `IReadOnlyList<string>` and `Array<String?>` is `string[]`, the `?` riding the NRT byte.
//
// A DELEGATE PARAMETER IS THE ONE ARGUMENT POSITION THAT KEEPS THE DECLARED FORM for a CONCRETE `V?`, so
// `(Int?) -> String` is `Func<Nullable<int32>, string>` while `(T?) -> String` is `Func<object, string>` at every
// instantiation. That is not the general rule and is recorded as such in `docs/dotkt-semantics.md` §9c-bis: a
// delegate's target may be a member the AUTHOR declared (`::handle`, `expr::member`), whose slots are its Kotlin
// surface and are not this pass's to move, and ECMA-335 II.14.6 admits no `Func<object, …>` over a target declaring
// `Nullable<int32>`. Closing it needs a synthesized FORWARDER at those two reference forms — a static one for
// `::fn`, a capture class for `expr::member` — after which the parameter joins the rule with the rest.
//
// WHY THE TWO POSITIONS DIFFER. The Kotlin type system's contract for an unconstrained `T?` is a runtime null for
// EVERY `T` — more than a reified CLR generic argument expresses — so a generic position has to box; `object` is the
// only uniform CLR storage that carries a real null for both a reference and a value instantiation, and the CLR's own `Nullable<V>` boxing
// collapse makes it the spec-defined boxed form (`box` of an empty `Nullable<V>` IS a null reference; `unbox.any
// Nullable<V>` accepts null). A reified construction is INVARIANT, so an open `G<T?>` erased to `G<object>` and a
// concrete `G<Int?>` left as `G<Nullable<int32>>` could never meet — the same unrelated pair array compatibility
// forbids for elements (ECMA-335 I.8.7.1). A SCALAR slot has no such meeting to arrange: nothing is reified over it,
// so it keeps the CLR-native `Nullable<V>`, which is what a C# caller expects to see and what interop is written in.
//
// EVERY type variable qualifies, whatever its bound. `fun <T : CharSequence> f(xs: List<T?>)` has no value
// instantiation and still erases, because the rule is uniform rather than bound-consulting: one physical form per
// declaration, decided without resolving where each bound leads.
//
// A declaration BOUND ELSEWHERE is authoritative and is NOT re-erased: a `memberSig`, and the `argTypes` a `clr*`
// node carries under another name, state what a .NET member really declares, so a genuine `List<int?>` parameter
// keeps its `Nullable<int32>` argument. Kotlin's canonical `G<object>` does not inhabit it, and that crossing is
// REFUSED by ForeignNullableGenericCrossing rather than silently adapted.
//
// The pre-erasure Kotlin TypeNode rides `[KotlinNullableGeneric]` so the Kotlin surface survives the trip; the
// user-visible consequences are recorded in `docs/dotkt-semantics.md` §9c-bis.
//
// USES of a slot are typed `Subst(Erase(declaredKotlinType(s)), typeArgs)` and NEVER `Erase(Subst(...))` — that is
// NullableTvErasureCallRealign's job, on both the read and the write axis. This pass owns only the declaration axis.
// Runs in every build so ref.dll, rt.dll, and the app's view of their signatures agree.
static class NullableGenericErasure
{
    // WHERE a type sits, which is the whole of the erasure rule.
    internal enum Pos
    {
        // A direct CLR slot: a concrete `V?` stays `System.Nullable<V>`.
        Slot,
        // An argument to a reified construction — a type argument, an array element, a delegate component. A
        // possibly-value `X?` is `System.Object` here.
        Argument,
        // A declaration BOUND ELSEWHERE, whose slots this pass does not get to move: a resolved .NET member's
        // `memberSig`, and the `funcType` of a delegate over a member that is already declared. Only the
        // inexpressible open `Nullable(Tv)` is rewritten; a concrete `V?` the target really declares is never
        // restated, because moving the delegate without moving the target is what leaves the two incompatible.
        Bound,
    }

    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        if (root is not JsonObject o) return;
        // #18/#147/#86 ROUND-TRIP RECORD (runs BEFORE the erasure below): capture each declaration slot the erasure
        // rewrites — a `Nullable(Tv)` at the HEAD (`fun <T> f(x: T?)`, `T?` returns, `T?` fields/properties), and any
        // possibly-value `X?` in a reified ARGUMENT (`List<Int?>`, `Holder<T?>`, `Array<Int?>`, `(Int?) -> R`) — for
        // method and constructor params, returns, fields, and properties. The erasure turns that position into
        // `object`, which dll2klib cannot infer back. Keep the pre-erasure TypeNode opaque until RoundtripMetadata
        // stamps [KotlinNullableGeneric] on that exact CLR declaration slot.
        RecordNullableGenericSlots(o, isValue);
        RecordSuspendFnShapes(o);
        ApplyRec(o, isValue);
        // The blanket type-slot sweep: every REMAINING position the rule rewrites, anywhere in the tree — a
        // `Nullable(Tv)` in a standalone param/field/local slot or a call `sig` element, and a possibly-value `X?`
        // nested in a constructed generic's argument list, an array element, a function type's return or parameter,
        // or a call's own `typeArgs`. `Nullable(Tv)` lowers to `Nullable<T>`, which is not even expressible for an
        // unconstrained (reference-allowed) `T`, so ilemit must NEVER see one; this sweep is what makes that true.
        EraseNullableGpAllStrings(o, isValue);
    }

    // Record the PRE-erasure TypeNode on every declaration slot carrying a `Nullable(Tv)`, at the head or nested.
    // Return slots use the `nullableGenericRet` hand-off; params/fields/properties use `nullableGeneric` on the slot
    // itself. These are opaque JSON STRINGS (not structured type slots), so ReferenceNullableStrip / BirTypeLowering
    // leave them untouched until RoundtripMetadata carrier-encodes them.
    static void RecordNullableGenericSlots(JsonObject o, Func<string, bool> isValue)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo)
                {
                    RecordNullableGenericSlot(mo, "ret", "nullableGenericRet", "retNullableFlags", isValue);
                    // A SUSPEND declaration's Kotlin result rides `suspendRet`, not `ret` — its `ret` is the cold
                    // entry's. The Task bridge that BECOMES its public ABI is built fresh later, so it cannot inherit
                    // a carrier from a declaration it replaces; it reads this stash instead (SuspendColdLowering).
                    // Recorded unconditionally of the erasure, because the sweep flattens `suspendRet` too.
                    if (TypeJson.Read(mo["suspendRet"]) is TypeNode.Nullable { Of: TypeNode.Tv } sr)
                        mo["nullableGenericSuspendRet"] = TypeNode.ToJson(sr);
                    RecordNullableGenericParams(mo["params"], isValue);
                }
        RecordNullableGenericCtorParams(o["ctors"], isValue);
        RecordNullableGenericDecls(o["fields"], isValue);
        RecordNullableGenericDecls(o["properties"], isValue);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to)
                {
                    RecordSupertypes(to, isValue);
                    RecordNullableGenericSlots(to, isValue);
                }
    }

    // The key the pre-erasure SUPERTYPE list is stashed under, for RoundtripMetadata's `[KotlinSupertypes]` carrier.
    //
    // A SUPERTYPE ARGUMENT IS KOTLIN SOURCE, NOT AN INTERNAL SHAPE. `class E : Sink<Int?>` erases its edge to
    // `Sink<object>`, and a separately compiled consumer that re-imports `E` sees `Sink<Any?>` — so `val s:
    // Sink<Int?> = E()` stops compiling. No member carrier can restore that: the members' own slots are exact, and
    // what was lost is the identity of the EDGE. Kotlin source compatibility is the one thing an internal decision
    // may not spend, so the pre-erasure edge travels with the type.
    //
    // The payload is the same opaque TypeNode form every other carrier uses — a `{base, interfaces, bounds}` object
    // of pre-erasure nodes — so no new encoding is introduced; `bounds` carries each type parameter's own upper
    // bounds, which erase for exactly the same reason and are lost the same way.
    internal const string SupertypesPre = "nullableGenericSupertypesPre";

    static void RecordSupertypes(JsonObject to, Func<string, bool> isValue)
    {
        var pre = new JsonObject();
        var moved = false;
        if (TypeJson.Read(to["base"]) is TypeNode b && !Erase(b, Pos.Slot, isValue).Equals(b))
        {
            pre["base"] = to["base"].DeepClone();
            moved = true;
        }
        if (to["interfaces"] is JsonArray ifs
            && ifs.Any(i => TypeJson.Read(i) is TypeNode t && !Erase(t, Pos.Slot, isValue).Equals(t)))
        {
            pre["interfaces"] = ifs.DeepClone();
            moved = true;
        }
        if (to["typeParams"] is JsonArray tps)
        {
            var bounds = new JsonObject();
            for (var i = 0; i < tps.Count; i++)
            {
                if (tps[i] is not JsonObject tp || tp["bound"] is not JsonNode bn) continue;
                if (TypeJson.Read(bn) is not TypeNode bt || Erase(bt, Pos.Slot, isValue).Equals(bt)) continue;
                bounds[i.ToString()] = bn.DeepClone();
            }
            if (bounds.Count > 0) { pre["bounds"] = bounds; moved = true; }
        }
        if (!moved) return;
        to[SupertypesPre] = pre.ToJsonString();
        if (to["types"] is JsonArray nested)
            foreach (var n in nested) if (n is JsonObject nto) RecordSupertypes(nto, isValue);
    }

    // The keys the pre-erasure SUSPEND function shape is stashed under, for BirTypeLowering's suspend-fn carrier.
    internal const string SuspendFnPre = "suspendFnTypePre";
    internal const string RetSuspendFnPre = "retSuspendFnTypePre";

    // A `suspend (…) -> T?` slot has TWO facts to preserve and only one of them is this pass's. The whole VALUE erases
    // to `object` at lowering — a suspend lambda is a Continuation state machine, not a delegate — so the arg/return
    // shape is carried by the DEDICATED `[KotlinSuspendFunctionType]` carrier, which is why `HasRestorableNullableTv`
    // excludes a suspend `Fn` from the nullable-generic carrier: there is no physical delegate for that carrier to
    // align with, and two carriers on one slot would disagree.
    //
    // But that dedicated carrier is built LATER, in BirTypeLowering, off the slot as it stands — and by then the sweep
    // below has erased the `Nullable(Tv)` inside it, so the carrier faithfully records `suspend () -> object` and a
    // consumer re-imports exactly that. So the pre-erasure shape is stashed here, as an opaque string the intervening
    // passes leave alone, and BirTypeLowering builds the carrier from it instead. The exclusion above is unchanged:
    // this does not add a second carrier, it makes the one carrier truthful.
    static void RecordSuspendFnShapes(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                Stash(obj, "type", SuspendFnPre);
                Stash(obj, "ret", RetSuspendFnPre);
                foreach (var kv in obj.ToList()) if (kv.Value != null) RecordSuspendFnShapes(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) RecordSuspendFnShapes(it);
                break;
        }

        static void Stash(JsonObject obj, string typeKey, string factKey)
        {
            if (obj.ContainsKey(factKey)) return;
            if (TypeJson.Read(obj[typeKey]) is TypeNode.Fn { Suspend: true } fn && HasNullableTv(fn))
                obj[factKey] = TypeNode.ToJson(fn);
        }
    }

    static bool HasNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => true,
        TypeNode.Nullable n => HasNullableTv(n.Of),
        TypeNode.Oblivious o => HasNullableTv(o.Of),
        TypeNode.Fqn { Args: { } args } => args.Any(HasNullableTv),
        TypeNode.Array a => HasNullableTv(a.Elem),
        TypeNode.ByRef b => HasNullableTv(b.Of),
        TypeNode.Fn fn => HasNullableTv(fn.Ret) || fn.Params.Any(HasNullableTv)
                          || (fn.Recv != null && HasNullableTv(fn.Recv)),
        _ => false,
    };

    static void RecordNullableGenericCtorParams(JsonNode node, Func<string, bool> isValue)
    {
        if (node is not JsonArray a) return;
        foreach (var item in a)
            if (item is JsonObject ctor) RecordNullableGenericParams(ctor["params"], isValue);
    }

    static void RecordNullableGenericParams(JsonNode node, Func<string, bool> isValue)
    {
        if (node is not JsonArray a) return;
        foreach (var item in a)
            if (item is JsonObject p) RecordNullableGenericSlot(p, "type", "nullableGeneric", "nullableFlags", isValue);
    }

    static void RecordNullableGenericDecls(JsonNode node, Func<string, bool> isValue)
    {
        if (node is not JsonArray a) return;
        foreach (var item in a)
            if (item is JsonObject d) RecordNullableGenericSlot(d, "type", "nullableGeneric", "nullableFlags", isValue);
    }

    static void RecordNullableGenericSlot(JsonObject decl, string typeKey, string factKey, string flagsKey,
        Func<string, bool> isValue)
    {
        if (TypeJson.Read(decl[typeKey]) is not TypeNode t || !HasRestorableNullableTv(t, isValue)) return;
        decl[factKey] = TypeNode.ToJson(t);
        // THE NRT BYTE OF AN OBJECT-ERASED HEAD IS COMPUTED HERE, FROM THE PRE-ERASURE TYPE (#86). dll2klib splits
        // restoration in two: the carrier owns the INNER tree but is read through StripOuterNullability, so a
        // `Nullable(Tv)` carrier arrives as the bare `Tv` and only the slot's NRT byte can put the `?` back
        // (dll2klib ApplyOuterNullability). DeclNullableFlags cannot supply it — it runs after this erasure and would
        // walk `object`, whose non-null default stamps nothing at all, and the slot re-imports as a non-null `Any`.
        // Only the HEAD position needs the pre-stamp: a nested `Nullable(Tv)` leaves the head intact, so the ordinary
        // post-erasure byte walk still describes it exactly. Rides DeclNullableFlags' never-overwrite contract.
        if (t is TypeNode.Nullable { Of: TypeNode.Tv } && NullableFlags.Compute(t, isValue) is JsonArray f)
            decl[flagsKey] = f;
    }

    // True iff `t` carries a position the erasure rewrites and dll2klib cannot infer back. Two of them:
    //   * a `Nullable(Tv)` anywhere reachable through a CLR-representable declaration shape; and
    //   * a REIFIED ARGUMENT that is a nullable POSSIBLY-VALUE type — `List<Int?>` physically `IReadOnlyList<object>`,
    //     `Box<Int?>` physically `Box<object>`, `Array<Int?>` physically `object[]`. Without the carrier a
    //     re-consuming reader sees only the `object` argument and restores `List<Any?>`, which is a DIFFERENT Kotlin
    //     type: a consumer passing its own `List<Int?>` would then fail to type-check.
    // The HEAD is deliberately not one of them: a direct `Int?` slot keeps its `Nullable<int32>` and needs no carrier
    // to be read back.
    // A non-suspend `Fn` is a real delegate in CIR, so dll2klib can walk its Invoke signature in parallel with the
    // recorded Kotlin fn node. A suspend fn is excluded: BirTypeLowering erases the whole value to object and its
    // distinct suspend-fn carrier owns restoration, so there is no physical delegate shape for this carrier to align with.
    static bool HasRestorableNullableTv(TypeNode t, Func<string, bool> isValue) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => true,
        TypeNode.Nullable n => HasRestorableNullableTv(n.Of, isValue),
        TypeNode.Fqn { Args: { } args } => args.Any(a => ErasedArgument(a, isValue)),
        TypeNode.Array a => ErasedArgument(a.Elem, isValue),
        TypeNode.ByRef b => HasRestorableNullableTv(b.Of, isValue),
        TypeNode.Oblivious o => HasRestorableNullableTv(o.Of, isValue),
        // A delegate's RETURN is an argument position; its PARAMETERS keep the declared form, so they need a carrier
        // only where a slot would — for the open `Nullable(Tv)` no CLR slot expresses.
        TypeNode.Fn { Suspend: false } fn =>
            ErasedArgument(fn.Ret, isValue)
            || fn.Params.Any(p => HasRestorableNullableTv(p, isValue))
            || (fn.Recv != null && HasRestorableNullableTv(fn.Recv, isValue)),
        _ => false,   // suspend Fn / bare Fqn / Tv: nothing the erasure rewrites
    };

    // One reified ARGUMENT the erasure rewrites: either it is itself the possibly-value `X?` that becomes `object`,
    // or it contains one deeper down (`List<List<Int?>>`).
    static bool ErasedArgument(TypeNode t, Func<string, bool> isValue)
        => IsNullableMaybeValue(t, isValue) || HasRestorableNullableTv(t, isValue);

    static void ApplyRec(JsonObject o, Func<string, bool> isValue)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods) ApplyToMethod(m);
        // A field/property whose slot is `T?` becomes the reference `object` slot, so a value instantiation holds a
        // real null rather than `default(T)`. Its ACCESSORS need no separate treatment: `get_x`'s return and
        // `set_x`'s parameter are declaration slots of the same declared type, erased by ApplyToMethod and the
        // blanket sweep on their own, which keeps the property row and both accessors coherent by construction.
        EraseNullableGpDecls(o["fields"]);
        EraseNullableGpDecls(o["properties"]);
        // FOREACH-OVER-NULLABLE-GENERIC-SOURCE erasure, and NOT deletable — RE-MEASURED against the cross-module
        // carrier read (#86 D1), which did subsume the hardcoded receiver-return table beside it but does NOT subsume
        // this. Deleting it breaks `filterNotNullTo` at a value element with an InvalidProgramException inside the
        // stdlib's own body, exactly as it did before the reader existed.
        //
        // The reason is a MISSING TYPE, not a missing declaration. The loop variable of a `forEachInline` over an
        // object-erased `Iterable<T?>` source is a slot like any other, so the uniform rule covers the ERASURE half;
        // the other half is a RE-NARROWING where that variable flows into a value-typed consumer, and its target is
        // the PRE-erasure element type. That type exists only HERE — the blanket sweep below consumes it, and by the
        // time the use axis runs, the slot and the element token both read `object`, so there is nothing left for the
        // use axis to narrow BACK to. Reading the callee's declaration cannot supply it either: the callee's parameter
        // is `Subst(Erase(decl))`, which is the erased form by construction. Erasing the loop element and re-narrowing
        // it at its consumers is therefore one atomic decision, and it stays in the pass that still holds both halves.
        EraseForEachOverNullableGpSource(o);
        // Nested types (a generic class' member methods / fields) carry their own declaration lists.
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) ApplyRec(to, isValue);
    }

    // For each method, find a `forEachInline` whose SOURCE is a parameter typed as a nullable-generic collection
    // (`Iterable<T?>`) and whose loop-var `elem` is that `T`; erase the loop var to `object` — the iteration yields
    // boxed elements, and a null one must survive rather than being unbox.any'd into a value slot — and re-narrow the
    // loop var back to `T` wherever it flows into a call argument, which is where a value consumer needs it unboxed.
    // The null-check use (`objEq(element, null)`) is not a call argument and correctly stays `object`.
    static void EraseForEachOverNullableGpSource(JsonObject o)
    {
        if (o["methods"] is not JsonArray methods) return;
        foreach (var m in methods)
        {
            if (m is not JsonObject mo || mo["params"] is not JsonArray ps) continue;
            // param name -> the element type-var Tv of a `…<T?>` (Nullable(Tv)) collection param.
            var nullableSrc = new Dictionary<string, TypeNode.Tv>(StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po
                    && (po["name"] as JsonValue)?.TryGetValue<string>(out var pn) == true
                    && TypeJson.Read(po["type"]) is TypeNode pt && ExtractNullableTv(pt) is TypeNode.Tv tp)
                    nullableSrc[pn] = tp;
            if (nullableSrc.Count > 0) ErodeForEach(mo["body"], nullableSrc);
        }
    }

    static void ErodeForEach(JsonNode node, Dictionary<string, TypeNode.Tv> nullableSrc)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "forEachInline"
                    && obj["src"] is JsonObject src
                    && (src["k"] as JsonValue)?.TryGetValue<string>(out var sk) == true && sk == "local"
                    && (src["name"] as JsonValue)?.TryGetValue<string>(out var sn) == true
                    && nullableSrc.TryGetValue(sn, out var tp)
                    // #37/#48: the loop `elem` is the nullable element `T?` = `{t:nullable,of:{t:tv}}` (pre-#48 kotc emitted
                    // a BARE `gp:T` here, the `?` riding a retired scalar flag). Match BOTH shapes — unwrap a Nullable(Tv)
                    // wrapper to its Tv — else the loop-var re-narrow never fires and the blanket sweep still erases `elem`
                    // to `object`, leaving `clrCollAdd(dst, element:object)` with no unbox.any -> InvalidProgram on a VALUE
                    // element instantiation (il-chunk `List<Int?>.filterNotNull()`).
                    && TypeJson.Read(obj["elem"]) is TypeNode elemT
                    && ((elemT as TypeNode.Tv) ?? ((elemT as TypeNode.Nullable)?.Of as TypeNode.Tv)) is TypeNode.Tv el && el == tp
                    && (obj["var"] as JsonValue)?.TryGetValue<string>(out var lv) == true)
                {
                    obj["elem"] = TypeJson.Fqn("object");
                    RenarrowLoopVarArgs(obj["body"], lv, el);
                }
                foreach (var kv in obj) ErodeForEach(kv.Value, nullableSrc);
                break;
            case JsonArray arr:
                foreach (var it in arr) ErodeForEach(it, nullableSrc);
                break;
        }
    }

    // Wrap every reference to the (now-`object`) loop var `lv` that appears as a CALL argument in a `cast`->`origElem`
    // (the Tv), so a value-type consumer unbox.any's the boxed element. The null-check use (`objEq(element, null)`) is
    // NOT a call arg and is correctly left as `object`.
    static void RenarrowLoopVarArgs(JsonNode node, string lv, TypeNode.Tv origElem)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["args"] is JsonArray a)
                    for (var i = 0; i < a.Count; i++)
                        if (a[i] is JsonObject ai
                            && (ai["k"] as JsonValue)?.TryGetValue<string>(out var ak) == true && ak == "local"
                            && (ai["name"] as JsonValue)?.TryGetValue<string>(out var an) == true && an == lv)
                            a[i] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(origElem), ["e"] = ai.DeepClone() };
                foreach (var kv in obj) RenarrowLoopVarArgs(kv.Value, lv, origElem);
                break;
            case JsonArray arr:
                foreach (var it in arr) RenarrowLoopVarArgs(it, lv, origElem);
                break;
        }
    }

    // The Tv of a Nullable(Tv) somewhere in a type (a nullable-generic collection element `…<T?>`), else null.
    static TypeNode.Tv ExtractNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv tv } => tv,
        TypeNode.Nullable n => ExtractNullableTv(n.Of),
        TypeNode.Fqn { Args: { } args } => args.Select(ExtractNullableTv).FirstOrDefault(x => x != null),
        TypeNode.Array a => ExtractNullableTv(a.Elem),
        TypeNode.ByRef b => ExtractNullableTv(b.Of),
        _ => null,
    };

    // A field/property whose slot is a HEAD `Nullable(Tv)` -> the reference `object` slot, so a value instantiation
    // holds a genuine null rather than `default(T)`. A NESTED one is reached by the blanket sweep instead.
    static void EraseNullableGpDecls(JsonNode arr)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            // #37/#48: a nullable generic-parameter field/property is `{t:nullable,of:{t:tv}}` (was `gp:T` + the retired
            // scalar `nullable` flag) -> the reference `object` slot (a value-type instantiation then holds a real null).
            if (d is JsonObject fo
                && TypeJson.Read(fo["type"]) is TypeNode.Nullable { Of: TypeNode.Tv })
                fo["type"] = TypeJson.Fqn("object");
    }

    // Blanket type-slot sweep applying `Erase` to every structured Type in the tree, each at ITS OWN position: an
    // open `Nullable(Tv)` erases wherever it sits, and a concrete possibly-value `X?` erases in every reified
    // ARGUMENT — a type argument, an array element, a delegate component. That uniformity IS the invariant (#86):
    // one argument position kept back is a second physical representation of one Kotlin type, and two instantiations
    // of an invariant reified generic never meet.
    //
    // A call's `sig`/`argTypes` is a STRUCTURED TypeNode array (#37 m3b) of the callee's PARAMETER SLOTS, so its
    // elements erase at the slot position through the same recursion — DEF and CALL sigs stay in agreement
    // structurally, no sig-string special case needed. A `typeArgs` vector is the opposite: those entries ARE the
    // arguments of a reified instantiation, so `listOf<Int?>` instantiates at `object` FROM THE START rather than
    // being built at `Nullable<int32>` and reconciled afterwards (two unrelated invariant generics no cast joins).
    //
    // A `memberSig` and the `argTypes` a `clr*` node carries under another name both state a declaration made
    // ELSEWHERE, and are never restated in Kotlin's vocabulary — see EraseBound.
    //
    // An `elem` is the one type slot the recursion cannot classify from its own shape: it is an ELEMENT written
    // without its container, so the node KIND says which container it belongs to. An array's or a collection
    // iteration's element is a reified argument; a `nullableValue`/`byrefLoad` `elem` names the `V` of a
    // `Nullable<V>` or a `ref` referent, which are slots.
    static void EraseNullableGpAllStrings(JsonNode node, Func<string, bool> isValue, Pos pos = Pos.Slot)
    {
        switch (node)
        {
            case JsonObject obj:
                var retSlotErased = false;
                var k = Str(obj["k"]);
                var elemPos = ArgumentElemKinds.Contains(k) ? Pos.Argument : Pos.Slot;
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    var keyPos = key switch
                    {
                        "elem" => elemPos,
                        "typeArgs" => Pos.Argument,
                        // `memberSig` is always a resolved .NET declaration; `argTypes` is the SAME vector under
                        // another name once a call is `clr*`-bound (NetInteropBinding writes the callee's declared
                        // signature there), while on a Kotlin `new` it is the caller's own substituted view.
                        "memberSig" => Pos.Bound,
                        "argTypes" when ClrBoundNode.IsAny(k) => Pos.Bound,
                        _ => pos,
                    };
                    if (TypeJson.Read(child) is TypeNode tn)
                    {
                        var erased = Erase(tn, keyPos, isValue);
                        if ((key == "ret" || key == "dynRet") && !erased.Equals(tn)) retSlotErased = true;
                        obj[key] = TypeJson.Write(erased);
                    }
                    else EraseNullableGpAllStrings(child, isValue, keyPos);
                }
                if (retSlotErased) DropStaleSty(obj);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) arr[i] = TypeJson.Write(Erase(tn, pos, isValue));
                    else EraseNullableGpAllStrings(child, isValue, pos);
                }
                break;
        }
    }

    // The node kinds whose `elem` is a REIFIED ARGUMENT — an array allocation's `newarr` token, a `ldelem`/`stelem`
    // token, the element a `for (x in arr)` binds, the element an inline iteration over a collection yields, and a
    // vararg pack's element. All of them must name the same type as the array or collection they operate on, and that
    // container's own argument is `object` for a possibly-value `X?`.
    //
    // A vararg pack (`newArray`) is one of them BECAUSE the call it is built for now instantiates at `object` too:
    // `f<Int?>(1, null)` canonicalizes its `typeArgs` above, so the callee's `!!0[]` parameter IS an `object[]` and a
    // pack that kept `Nullable<int32>` elements would be the one array the call cannot accept.
    //
    // A COLLECTION construction's element (`newList`/`newSet`, and `newMap`'s `keyType`/`valType`) is a reified
    // argument too and is absent for one reason: MemberCallSubstitution BUILDS those nodes long after this sweep,
    // from the call's own `typeArgs` — which this sweep has already canonicalized — so they arrive at `object`
    // rather than being erased into it. Adding them here would be listing a kind this pass never sees.
    static readonly HashSet<string> ArgumentElemKinds = new(StringComparer.Ordinal)
    {
        "newArray", "newArraySized", "newArrayInit", "arrayGet", "arraySet", "forArray",
        "forEachInline", "forIn", "spreadConcat",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;

    // `Erase` at a DIRECT SLOT — a method return, a parameter, a field, a property, a local, a `ref` referent. The
    // concrete `V?` keeps its CLR-native `System.Nullable<V>`; only the open `Nullable(Tv)`, which is inexpressible,
    // becomes `object`. Whatever the slot CONTAINS is erased at its own position, so a `List<Int?>` parameter still
    // becomes an `IReadOnlyList<object>`.
    internal static TypeNode EraseNullableTv(TypeNode t, Func<string, bool> isValue) => Erase(t, Pos.Slot, isValue);

    // `Erase` at an ARGUMENT to a reified construction — a generic type argument, a generic method type argument, an
    // array element, a delegate component. A nullable POSSIBLY-VALUE type is `object` here, so `List<T?>`,
    // `List<Int?>` and `List<Boolean?>` all become `IReadOnlyList<object>` and meet each other, and `Array<T?>`,
    // `Array<Int?>` and `Array<Boolean?>` are all `object[]`. Anything else erases normally: `List<String?>` stays
    // `IReadOnlyList<string>` and `Array<String?>` stays `string[]` (the `?` rides the NRT byte).
    internal static TypeNode EraseArgument(TypeNode t, Func<string, bool> isValue) => Erase(t, Pos.Argument, isValue);

    // A declaration bound elsewhere. Only the open `Nullable(Tv)` — which no CLR slot can hold for an unconstrained
    // type variable — is rewritten; a concrete `Nullable<V>` the target really declares stands.
    internal static TypeNode EraseBound(TypeNode t, Func<string, bool> isValue) => Erase(t, Pos.Bound, isValue);

    // THE ONE RULE. `Nullable(Tv)` is `object` wherever it sits, because no CLR slot expresses it; a concrete
    // possibly-value `V?` is `object` in an ARGUMENT position and `Nullable<V>` in a slot. Everything else recurses,
    // with each child visited at ITS position: an Fqn's arguments, an array's element and a delegate's
    // parameters/return/receiver are arguments; a `ref` referent and a nullable's inner are slots.
    internal static TypeNode Erase(TypeNode t, Pos pos, Func<string, bool> isValue)
    {
        if (t is TypeNode.Nullable { Of: TypeNode.Tv }) return new TypeNode.Fqn("object");
        if (pos == Pos.Argument && IsNullableMaybeValue(t, isValue)) return new TypeNode.Fqn("object");
        // A BOUND subtree stays bound all the way down: a `List<int?>` parameter's argument is what the target
        // declares, not a position this compiler gets to canonicalize.
        var inner = pos == Pos.Bound ? Pos.Bound : Pos.Argument;
        var slot = pos == Pos.Bound ? Pos.Bound : Pos.Slot;
        return t switch
        {
            TypeNode.Nullable n => new TypeNode.Nullable(Erase(n.Of, slot, isValue)),
            // An NRT-OBLIVIOUS `T!` is a pure nullability ANNOTATION, not a container — BirTypeLowering lowers
            // straight through it and propagates the position with it — so its inner keeps THIS node's position.
            TypeNode.Oblivious o => new TypeNode.Oblivious(Erase(o.Of, pos, isValue)),
            TypeNode.Fqn { Args: null } f => f,
            TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(a => Erase(a, inner, isValue)).ToArray()),
            TypeNode.Array a => new TypeNode.Array(Erase(a.Elem, inner, isValue)),
            TypeNode.ByRef b => new TypeNode.ByRef(Erase(b.Of, slot, isValue)),
            // A delegate's RETURN follows the argument rule; its PARAMETERS keep the declared form. See the header:
            // the two differ only because a callable reference to a DECLARED member has no forwarder yet.
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Erase(fn.Ret, inner, isValue),
                fn.Params.Select(p => Erase(p, slot, isValue)).ToArray(),
                fn.Recv == null ? null : Erase(fn.Recv, slot, isValue)),
            _ => t,
        };
    }

    // THE OTHER DIRECTION OF THE SAME POSITIONAL RULE: does this LOWERED declaration put a `System.Nullable<V>`
    // where `Erase` would have put an `object`? That is exactly the shape no Kotlin type inhabits, and
    // ForeignNullableGenericCrossing refuses a .NET member that declares one.
    //
    // It lives beside `Erase` and its arms are read against `Erase`'s, position for position, because the two ARE one
    // statement and stating them in two files is how they drift apart. They did: a delegate PARAMETER is `Pos.Slot`
    // here (a concrete `V?` keeps its `Nullable<V>`, see the header), and a copy of this walk that called it an
    // argument refused a `Func<int?, string>` parameter Kotlin inhabits exactly.
    //
    // The HEAD is not a crossing in either arm: a direct `Nullable<V>` parameter or return IS what a Kotlin scalar
    // `Int?` is, and it crosses without any adaptation at all. That includes a `Nullable<!!0>` on a `T : struct` .NET
    // generic, which a Kotlin `T?` inhabits at every instantiation.
    internal static bool ErasureWouldMove(TypeNode lowered) => AtSlot(lowered);

    // `Erase` at Pos.Slot: the head is not moved; an Fqn's arguments, an array's element and a delegate's RETURN are
    // arguments; a byref referent, a nullable's inner and a delegate's PARAMETERS are slots.
    static bool AtSlot(TypeNode t) => t switch
    {
        TypeNode.Fqn { Args: { } args } => args.Any(AtArgument),
        TypeNode.Array a => AtArgument(a.Elem),
        TypeNode.ByRef b => AtSlot(b.Of),
        TypeNode.Nullable n => AtSlot(n.Of),
        TypeNode.Oblivious o => AtSlot(o.Of),
        TypeNode.Fn fn => AtArgument(fn.Ret) || fn.Params.Any(AtSlot)
                          || (fn.Recv != null && AtSlot(fn.Recv)),
        _ => false,
    };

    // `Erase` at Pos.Argument: a `Nullable` node HERE is the position that moves. By this point the tree is lowered,
    // so every surviving `Nullable` is a real `System.Nullable<V>` over a value type — BirTypeLowering strips each
    // reference `?` before then. An NRT-OBLIVIOUS wrapper is a pure annotation and is looked through, so a
    // `[MaybeNull] List<int?>` is the same crossing as a plain one.
    static bool AtArgument(TypeNode t) => t switch
    {
        TypeNode.Nullable => true,
        TypeNode.Oblivious o => AtArgument(o.Of),
        _ => AtSlot(t),
    };

    // `X?` where `X` may be a value type: an open type variable, or a concrete value FQN. A reference `X` is excluded —
    // its `?` is not a physical difference on the CLR.
    //
    // EVERY type variable qualifies, whatever its bound. `fun <T : CharSequence> f(xs: Array<T?>)` has no value
    // instantiation and still erases, because the rule is uniform rather than bound-consulting: one physical form per
    // declaration, decided without resolving where each bound leads. The cost is a `CharSequence`-bounded array that
    // boxes for nothing; the alternative is a slot whose representation depends on a bound the reader has to chase,
    // and two `Array<T?>` declarations that cannot meet when one is bounded and one is not.
    internal static bool IsNullableMaybeValue(TypeNode t, Func<string, bool> isValue)
        => t is TypeNode.Nullable n && MayBeValue(n.Of, isValue);

    // A CONSTRUCTED name is classified like any other: `KeyValuePair<K,V>` and `ArraySegment<T>` are structs, and the
    // oracle strips generic arity to answer. Matching only the argument-less shape left every constructed BCL struct
    // classified as a reference, so `Array<KeyValuePair<K,V>?>` stayed a `Nullable<KVP>[]` while the open `Array<T?>`
    // it has to meet was `object[]` — the unrelated pair this whole decision exists to delete, and it segfaulted the
    // process rather than failing loudly.
    static bool MayBeValue(TypeNode t, Func<string, bool> isValue) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Fqn f => isValue?.Invoke(f.Name) == true,
        TypeNode.Oblivious o => MayBeValue(o.Of, isValue),
        _ => false,
    };

    // Spec §2.7 — a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`. This pass changes what a
    // call PHYSICALLY produces: `Slot<T?>` and `Slot<object>` are unrelated invariant reified generics, which is the
    // whole reason the erasure exists, while the frontend stamp still names the INSTANTIATED pre-erasure type
    // (`Slot<String>`). The stamp is read FIRST by every deriver (bir-common/NodeType.cs), so leaving it declares a
    // spill slot or a state-machine field at a type the value does not have — invalid IL, the same fault the use-axis
    // realign was fixed for. It cannot be REWRITTEN from here: the erased result is the UNinstantiated declared shape,
    // not this call site's instantiation, so the stamp is DROPPED — but only where the erasure actually invalidated
    // it, which is `DropStampIfStale`'s whole job. (The #305 chokepoint found these sites.)
    static void DropStaleSty(JsonObject obj) => NodeType.DropStampIfStale(obj);

    // A nullable generic RETURN `{t:nullable,of:{t:tv}}` -> `object`, the only CLR rep of a generic `T?` that carries
    // a real null for a value-type instantiation. The `return` statements in the body are NOT rewritten here: a
    // `return` is a use position like any other, and the use axis reconciles the returned value against this
    // (now-erased) slot — see NullableTvErasureCallRealign's write half.
    static void ApplyToMethod(JsonNode m)
    {
        if (m is not JsonObject mo) return;
        if (TypeJson.Read(mo["ret"]) is not TypeNode.Nullable { Of: TypeNode.Tv }) return;
        mo["ret"] = TypeJson.Fqn("object");
    }
}
