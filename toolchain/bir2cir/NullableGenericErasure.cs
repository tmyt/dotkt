using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE ERASURE INVARIANT (#86). For any declaration slot `s`, `physical(s) = Erase(declaredKotlinType(s))`, where
// `Erase` maps `Nullable(Tv)` to `System.Object` recursively, at EVERY position: method return, method param,
// constructor param, field, property, body local, generic type-argument, array element, function-type return and
// parameter, and call signature alike. `object` is the
// only uniform CLR storage that carries a real null for BOTH a reference and a value instantiation of an unconstrained
// `T`, and the CLR's own `Nullable<V>` boxing collapse makes it the spec-defined boxed form (`box` of an empty
// `Nullable<V>` IS a null reference; `unbox.any Nullable<V>` accepts null). The pre-erasure Kotlin TypeNode rides
// `[KotlinNullableGeneric]` so the Kotlin surface survives the trip.
//
// THE ARRAY ELEMENT IS ERASED ONE STEP FURTHER (#86 D2), and it is the only position where the erasure is not
// transparent: `object[]` and `Nullable<int32>[]` are UNRELATED CLR types — array compatibility requires
// reference-compatible elements (ECMA-335 I.8.7.1) — so an open `Array<T?>` erased to `object[]` and a concrete
// `Array<Int?>` left as `Nullable<int32>[]` could never meet. `Array<X?>` is therefore canonically `object[]`
// whenever `X` MAY be a value type: an open `Tv` (some instantiation is a struct) or a value `Fqn` (this one is).
// `Array<String?>` keeps `string[]`, `Array<Int>` keeps `int32[]`, and `IntArray` is untouched. The user-visible
// consequences are recorded in `docs/dotkt-semantics.md` §9c-bis.
//
// USES of a slot are typed `Subst(Erase(declaredKotlinType(s)), typeArgs)` and NEVER `Erase(Subst(...))` — that is
// NullableTvErasureCallRealign's job, on both the read and the write axis. This pass owns only the declaration axis.
// Runs in every build so ref.dll, rt.dll, and the app's view of their signatures agree.
static class NullableGenericErasure
{
    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        if (root is not JsonObject o) return;
        // #18/#147/#86 ROUND-TRIP RECORD (runs BEFORE the erasure below): capture each declaration slot whose type
        // carries a `Nullable(Tv)` anywhere — at the HEAD (`fun <T> f(x: T?)`, `T?` returns, `T?` fields/properties)
        // or NESTED in a constructed generic / array / byref / function type (`Holder<T?>` / `(T?)->R`) — for method
        // and constructor params, returns, fields, and properties. The erasure turns that slot into `object`, which
        // dll2klib cannot infer back. Keep the pre-erasure TypeNode opaque until RoundtripMetadata stamps
        // [KotlinNullableGeneric] on that exact CLR declaration slot.
        RecordNullableGenericSlots(o, isValue);
        RecordSuspendFnShapes(o);
        ApplyRec(o, isValue);
        // The blanket type-slot sweep: every REMAINING `Nullable(Tv)` anywhere in the tree — nested in a constructed
        // generic's argument list, an array element, a byref referent, a function type's return or parameter, a
        // standalone param/field/local slot, a call `sig` element — becomes `object`. `Nullable(Tv)` lowers to
        // `Nullable<T>`, which is not even expressible for an unconstrained (reference-allowed) `T`, so ilemit must
        // NEVER see one; this sweep is what makes that true, function-type returns included.
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
            foreach (var t in types) if (t is JsonObject to) RecordNullableGenericSlots(to, isValue);
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
    //   * an ARRAY whose element is a nullable POSSIBLY-VALUE type (#86 D2) — `Array<Int?>` physically `object[]`.
    //     Without the carrier a re-consuming reader sees only `object[]` and restores `Array<Any?>`, which is a
    //     DIFFERENT Kotlin type: a consumer passing its own `Array<Int?>` would then fail to type-check.
    // A non-suspend `Fn` is a real delegate in CIR, so dll2klib can walk its Invoke signature in parallel with the
    // recorded Kotlin fn node. A suspend fn is excluded: BirTypeLowering erases the whole value to object and its
    // distinct suspend-fn carrier owns restoration, so there is no physical delegate shape for this carrier to align with.
    static bool HasRestorableNullableTv(TypeNode t, Func<string, bool> isValue) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => true,
        TypeNode.Nullable n => HasRestorableNullableTv(n.Of, isValue),
        TypeNode.Fqn { Args: { } args } => args.Any(a => HasRestorableNullableTv(a, isValue)),
        TypeNode.Array a => IsNullableMaybeValue(a.Elem, isValue) || HasRestorableNullableTv(a.Elem, isValue),
        TypeNode.ByRef b => HasRestorableNullableTv(b.Of, isValue),
        TypeNode.Fn { Suspend: false } fn =>
            HasRestorableNullableTv(fn.Ret, isValue)
            || fn.Params.Any(p => HasRestorableNullableTv(p, isValue))
            || (fn.Recv != null && HasRestorableNullableTv(fn.Recv, isValue)),
        _ => false,   // suspend Fn / bare Fqn / Tv / Oblivious: no restorable nested Nullable(Tv)
    };

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

    // Blanket type-slot sweep applying EraseNullableTv to every structured Type in the tree — a `Nullable(Tv)` (a
    // value-type-nullable type variable `T?`) erases to `object` wherever it sits, with NO positional exception: a
    // declaration param, a constructor param, a body-local `var` slot, a clrg-nested type-arg, a field, an array
    // element, a call `sig` element. That uniformity IS the invariant (#86): one position kept back is a second
    // physical representation of one Kotlin type, and the two never meet at a value instantiation.
    //
    // A call's `sig` is a STRUCTURED TypeNode array (#37 m3b), so its `Nullable(Tv)` elements erase for free through
    // the same recursion — DEF and CALL sigs stay in agreement structurally, no sig-string special case needed.
    //
    // An ARRAY NODE's `elem` is the one type slot the recursion cannot classify from its own shape: it is an array
    // ELEMENT written without the enclosing `Array`, so `Nullable(Int)` there means `Nullable<int32>[]` and must obey
    // D2's element rule rather than the ordinary one. The node kind says which `elem` those are — a `newList`/`newSet`/
    // `forEachInline` `elem` is a COLLECTION element (`List<Int?>` really is `List<Nullable<int32>>`) and is left alone.
    static void EraseNullableGpAllStrings(JsonNode node, Func<string, bool> isValue)
    {
        switch (node)
        {
            case JsonObject obj:
                var retSlotErased = false;
                var arrayNode = ArrayElemKinds.Contains(Str(obj["k"]));
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn)
                    {
                        var erased = arrayNode && key == "elem"
                            ? EraseArrayElem(tn, isValue)
                            : EraseNullableTv(tn, isValue);
                        if ((key == "ret" || key == "dynRet") && !erased.Equals(tn)) retSlotErased = true;
                        obj[key] = TypeJson.Write(erased);
                    }
                    else EraseNullableGpAllStrings(child, isValue);
                }
                if (retSlotErased) DropStaleSty(obj);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) arr[i] = TypeJson.Write(EraseNullableTv(tn, isValue));
                    else EraseNullableGpAllStrings(child, isValue);
                }
                break;
        }
    }

    // The node kinds whose `elem` is an ARRAY element — an allocation's `newarr` token, a `ldelem`/`stelem` token, and
    // the element a `for (x in arr)` binds. All of them must name the same type as the array they operate on.
    //
    // `newArray` is NOT one of them, and the exclusion is the rule rather than an exception: at this point in the
    // pipeline a `newArray` is a kotc VARARG PACK, an array built for one call and named by that call's own
    // instantiation (`f<Int?>(1, null)` packs a `!!0[]` at `T = Nullable<int32>`), not a value that has to inhabit a
    // declared `Array<X?>` slot. The pack follows the callee — RealignArgs retypes it against the parameter — while
    // canonicalizing it here would state `object[]` where the callee's `!!0[]` is not one. The `newArray` that IS a
    // real `Array<X?>` value comes from the `arrayOf` FACTORY, which MemberCallSubstitution builds later and
    // canonicalizes itself.
    static readonly HashSet<string> ArrayElemKinds = new(StringComparer.Ordinal)
    {
        "newArraySized", "newArrayInit", "arrayGet", "arraySet", "forArray",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;

    // `Erase`: replace every `Nullable(Tv)` (a value-type-nullable type variable) with `object`, recursively and at
    // every position. A function type's RETURN obeys the same rule as its params and receiver — `Fn.Ret` had a verbatim
    // carve-out that handed a top-level `T?` return to NullableFuncReturnErasure, which is one Kotlin type constructor
    // with two owners and, before #142 narrowed it, produced a `newDelegate.funcType.ret` inconsistent with the lifted
    // lambda's own signature (ilverify DelegateCtor "Unrecognized arguments"). NullableFuncReturnErasure keeps only its
    // CONCRETE-value-inner half (`(T) -> Int?`), a different family that this rule does not reach.
    //
    // The ARRAY ELEMENT is the one position that erases FURTHER than the general rule (#86 D2) — see the file header.
    internal static TypeNode EraseNullableTv(TypeNode t, Func<string, bool> isValue) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => new TypeNode.Fqn("object"),
        TypeNode.Nullable n => new TypeNode.Nullable(EraseNullableTv(n.Of, isValue)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(a => EraseNullableTv(a, isValue)).ToArray()),
        TypeNode.Array a => new TypeNode.Array(EraseArrayElem(a.Elem, isValue)),
        TypeNode.ByRef b => new TypeNode.ByRef(EraseNullableTv(b.Of, isValue)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, EraseNullableTv(fn.Ret, isValue),
            fn.Params.Select(p => EraseNullableTv(p, isValue)).ToArray(),
            fn.Recv == null ? null : EraseNullableTv(fn.Recv, isValue)),
        _ => t,
    };

    // `Erase` at an ARRAY ELEMENT (#86 D2). A nullable POSSIBLY-VALUE element is `object`, so `Array<T?>`,
    // `Array<Int?>` and `Array<Boolean?>` are all `object[]` and meet each other. Anything else erases normally:
    // `Array<String?>` stays `string[]` (the `?` rides the NRT byte), `Array<Int>` stays `int32[]`.
    internal static TypeNode EraseArrayElem(TypeNode elem, Func<string, bool> isValue)
        => IsNullableMaybeValue(elem, isValue) ? new TypeNode.Fqn("object") : EraseNullableTv(elem, isValue);

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
