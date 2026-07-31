using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE ERASURE INVARIANT (#86). For any declaration slot `s`, `physical(s) = Erase(declaredKotlinType(s))`, where
// `Erase` maps `Nullable(Tv)` to `System.Object` recursively, at
// EVERY position with no exceptions: method return, method param, constructor param, field, property, body local,
// generic type-argument, array element, function-type return and parameter, and call signature alike. `object` is the
// only uniform CLR storage that carries a real null for BOTH a reference and a value instantiation of an unconstrained
// `T`, and the CLR's own `Nullable<V>` boxing collapse makes it the spec-defined boxed form (`box` of an empty
// `Nullable<V>` IS a null reference; `unbox.any Nullable<V>` accepts null). The pre-erasure Kotlin TypeNode rides
// `[KotlinNullableGeneric]` so the Kotlin surface survives the trip.
//
// USES of a slot are typed `Subst(Erase(declaredKotlinType(s)), typeArgs)` and NEVER `Erase(Subst(...))` — that is
// NullableTvErasureCallRealign's job, on both the read and the write axis. This pass owns only the declaration axis.
// Runs in every build so ref.dll, rt.dll, and the app's view of their signatures agree.
static class NullableGenericErasure
{
    // The #120 reify-back locals this file's CollapseReifiedArrayVars kept as a bare `!T[]` chain, by node identity.
    // They are the ONE local divergence from the uniform erasure: the allocation is a genuine `newarr !T` and the
    // whole chain (slot, `stelem`/`ldelem` token, trailing `as Array<T>`) is collapsed to match it. The use-axis
    // realign must therefore NOT re-derive their slot from `arrayOfNulls`' declared `Array<T?>`, which would widen the
    // slot to `object[]` over a `!T[]` allocation. Dies together with representation C when `Array<X?>` becomes
    // canonically `object[]` (#86 D2).
    public static readonly HashSet<JsonObject> ReifiedArrayVars = new();

    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        if (root is not JsonObject o) return;
        ReifiedArrayVars.Clear();
        // #18/#147/#86 ROUND-TRIP RECORD (runs BEFORE the erasure below): capture each declaration slot whose type
        // carries a `Nullable(Tv)` anywhere — at the HEAD (`fun <T> f(x: T?)`, `T?` returns, `T?` fields/properties)
        // or NESTED in a constructed generic / array / byref / function type (`Holder<T?>` / `(T?)->R`) — for method
        // and constructor params, returns, fields, and properties. The erasure turns that slot into `object`, which
        // dll2klib cannot infer back. Keep the pre-erasure TypeNode opaque until RoundtripMetadata stamps
        // [KotlinNullableGeneric] on that exact CLR declaration slot.
        RecordNullableGenericSlots(o, isValue);
        ApplyRec(o);
        // NESTED / STANDALONE nullable-generic TYPE-ARG erasure (FIX 1 part-2). A `T?` that kotc left as the
        // inline token `nullable:gp:T` — nested in a `clrg:Owner[...]` arg list (e.g.
        // `clrg:System.Collections.Generic.IEnumerable[nullable:gp:T]`) or standalone as a param/field type —
        // has the SAME value-type-null fault as the return case: `nullable:gp:T` lowers to `Nullable<T>`, invalid
        // for an unconstrained (reference-allowed) T. Erase every such token to `object` (the boxed/erased nullable
        // rep that carries a real null), everywhere a type token appears (params, returns, fields, `sig`). ilemit
        // must NEVER see `nullable:gp:` — this fully consumes it, exactly as NullableFuncReturnErasure consumes the
        // `func:nullable:` returns (which this pass deliberately leaves for that twin — see EraseNullableGpToken).
        EraseNullableGpAllStrings(o);
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
                    RecordNullableGenericParams(mo["params"], isValue);
                }
        RecordNullableGenericCtorParams(o["ctors"], isValue);
        RecordNullableGenericDecls(o["fields"], isValue);
        RecordNullableGenericDecls(o["properties"], isValue);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) RecordNullableGenericSlots(to, isValue);
    }

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
        if (TypeJson.Read(decl[typeKey]) is not TypeNode t || !HasRestorableNullableTv(t)) return;
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

    // True iff `t` carries a `Nullable(Tv)` reachable through a CLR-representable declaration shape. A non-suspend `Fn`
    // is a real delegate in CIR, so dll2klib can walk its Invoke signature in parallel with the recorded Kotlin fn node.
    // A suspend fn is excluded: BirTypeLowering erases the whole value to object and its distinct suspend-fn carrier owns
    // restoration, so there is no physical delegate shape for this carrier to align with.
    static bool HasRestorableNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => true,
        TypeNode.Nullable n => HasRestorableNullableTv(n.Of),
        TypeNode.Fqn { Args: { } args } => args.Any(HasRestorableNullableTv),
        TypeNode.Array a => HasRestorableNullableTv(a.Elem),
        TypeNode.ByRef b => HasRestorableNullableTv(b.Of),
        TypeNode.Fn { Suspend: false } fn =>
            HasRestorableNullableTv(fn.Ret)
            || fn.Params.Any(HasRestorableNullableTv)
            || (fn.Recv != null && HasRestorableNullableTv(fn.Recv)),
        _ => false,   // suspend Fn / bare Fqn / Tv / Oblivious: no restorable nested Nullable(Tv)
    };

    static void ApplyRec(JsonObject o)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods) ApplyToMethod(m);
        // A field/property whose slot is `T?` becomes the reference `object` slot, so a value instantiation holds a
        // real null rather than `default(T)`. Its ACCESSORS need no separate treatment: `get_x`'s return and
        // `set_x`'s parameter are declaration slots of the same declared type, erased by ApplyToMethod and the
        // blanket sweep on their own, which keeps the property row and both accessors coherent by construction.
        EraseNullableGpDecls(o["fields"]);
        EraseNullableGpDecls(o["properties"]);
        // #120 REIFIED-ARRAY reify-back idiom. Runs BEFORE the blanket EraseNullableGpAllStrings sweep (Apply, after
        // ApplyRec) so the kept chain is already bare `!T` when the sweep (a no-op on bare tv) runs. See there.
        if (o["methods"] is JsonArray msArrays)
            foreach (var m in msArrays)
                if (m is JsonObject mo) CollapseReifiedArrayVars(mo);
        // FOREACH-OVER-NULLABLE-GENERIC-SOURCE erasure, and NOT YET DELETABLE (#86). The loop variable of a
        // `forEachInline` over an object-erased `Iterable<T?>` source is a slot like any other, so the uniform rule
        // covers the ERASURE half. Its second half cannot move to the use axis yet: the loop var has to be RE-NARROWED
        // where it flows into a value-typed consumer, and the narrowing target is the PRE-erasure element type, which
        // exists only here — the blanket sweep below consumes it, and by the time the use axis runs both the slot and
        // the element token read `object`. The consumer is a REFERENCED collection member whose declared parameter is
        // not readable either. It dies with `DeriveKnownReceiverReturn` and the collection/iterator owner tables, in
        // the cross-module carrier-read step, and for the same reason.
        EraseForEachOverNullableGpSource(o);
        // Nested types (a generic class' member methods / fields) carry their own declaration lists.
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) ApplyRec(to);
    }

    // Wrap each argument of a `callInstance` to an erased setter (`set_X` in `setters`) in a `cast`->`object`, so ilemit
    // boxes a value/generic-param arg into the erased `object` param even when it can't resolve the re-anchored generic
    // method's param types. A `null`/already-reference arg becomes a redundant `castclass object` (valid, no box).
        // Record the get_/set_ accessor names of every nullable generic-parameter PROPERTY (`type:"gp:T"` + `nullable:true`)
    // — captured BEFORE EraseNullableGpDecls rewrites the property type to `object` (the `gp:` test would then miss).
        // Retype a local `var x: gp:T = <call to an erased getter>()` slot to `object`, so the object value read from the
    // now-`object` getter is held in a reference local until an explicit `as T` re-narrows it. Only the direct
    // reader-local pattern (init is a callInstance to a getter in `getters`); other uses re-narrow via their own cast.
        // Retype the `retType` of every `callInstance` reading an erased getter (`get_X` in `getters`) to `object`, so
    // the CIR call node agrees with the getter's now-`object` return. Without this, a stale `retType:"gp:T"` makes
    // ilemit coerce (unbox.any) the object result to the value type at the call — and a wrapping `as T` cast then
    // unbox.any's the already-unboxed value AGAIN, NREing. Retyping to `object` leaves a single narrow (the `as T`).
        // GENERAL body-local twin of EraseNullableGpDecls: retype a NULL-INITIALIZED `k=="var"` local marked `type:"gp:T"`
    // + sibling `nullable:true` to `object`. The local counterpart of the field/property erasure — a value-type-nullable
    // accumulator local (`var single: T? = null` in Sequence.single{}) must hold a genuine null in a reference slot,
    // with value stores boxing and the read boundary (`single as T`) re-narrowing (unbox.any/castclass).
    //
    // WHY GATE ON A NULL-CONST INIT (not the bare marker). kotc stamps `nullable:true` on EVERY value-type-nullable `gp:`
    // local, INCLUDING compiler-synthesized safe-call receiver temps (`tmp0_safe_receiver` for `transform(x)?.let{…}` in
    // mapNotNullTo). Those temps init from an object-returning call and are read IMPLICITLY (`?.`/`.let`) with no explicit
    // `as T`, so erasing them to `object` corrupts the unbox (mapNotNull -> garbage; collmore NEW-FAIL). The `var x: T? =
    // null` accumulator idiom — the case that genuinely needs a surviving null — always inits to a null const and is read
    // through an explicit `as T`; keying on the null-const init selects exactly that idiom and excludes the synthetic
    // temps. (The `forEachInline` loop var over a nullable-generic SOURCE — filterNotNullTo's `for (element in
    // this: Iterable<T?>)` — is a DISTINCT axis needing a value-type-nullable COLLECTION receiver conversion the call
    // sites lack; broad erasure there corrupts hashCode/collectionToArray iterations. Left to the collection
    // dual-representation track — NOT erased here.)
        // True when a var initializer is a `Map`/`MutableMap` `.get(key)` call — its Kotlin result is `V?`, which
    // MemberCallSubstitution rewrites to the erased nullable-generic `clrMapGet<K,V>: object` (a present value boxes,
    // a missing key is a genuine `null`). A `var value: gp:V nullable:true = get(key)` slot (getOrPut's explicit
    // `val value = get(key)`, unlike getOrElse's `?:`-synthesized `object` subject) must therefore be an `object`
    // slot — else the object init is stored raw into a `!!V` slot and the `value == null` check never sees the null
    // (getOrPut on `MutableMap<K,primitive>` silently returned 0 and never inserted). The read boundary re-narrows:
    // `objEq(value, null)` reads it as `object`; the `else value` branch (cond typed `gp:V`) unbox.any's it back
    // (EmitNullableCoerced). Gated on the `overrides` marker (owner Map/MutableMap, member `get`), so it never hits
    // the safe-call receiver temps RetypeNullableGpVars deliberately excludes (those init from a `transform(x)` invoke).
        // True when a var initializer is a `delegateInvoke` whose function-type RETURN is a nullable generic (`(…) -> V?`,
    // `{t:nullable,of:{t:tv}}`). NullableFuncReturnErasure lowers such a delegate's `Invoke` return to `object` (the one
    // rep a value/reference instantiation agree on), so a local receiving it must be an `object` slot too — covering BOTH
    //   * a genuine `val computed = remappingFunction(…)` accumulator read through an explicit null-check + `as V`
    //     (clrMapMerge's remove-on-null path; il-mapmerge), AND
    //   * a kotc-synthesized safe-call receiver temp `val tmpN_safe_receiver = transform(x)` for `transform(x)?.let{…}`
    //     (mapNotNullTo; il-collmore) — pre-#48 this WAS an `object` slot (the blanket `nullable:gp:` sweep erased it);
    //     leaving it a bare value `V` made bir2cir insert an eager `cast<V>(…:object)` that unbox.any-NREs on a null
    //     transform result. The alias reader chain (`__inlN = tmp; __lamN = __inl`) re-narrows at the value consumer.
    // These are the delegate-invoke initializers — the safe-call temps that init from a plain callInstance/callStatic
    // (a genuine `foo?.bar` receiver read implicitly) are NOT matched here and lower to the bare `gp:V` as before.
        // True when a var initializer is the null literal (`{k:"const", value:null}`) — the `T? = null` accumulator idiom.
    // A JSON null property surfaces as a C# null JsonNode, so a `const` whose `value` node is null IS the null literal.
        // BUG-1 Part B: for each method, find a `forEachInline` whose SOURCE is a param typed as a nullable-generic
    // collection (`...[nullable:gp:X]`) and whose loop-var `elem` is the bare `gp:X`; erase the loop-var to `object`
    // (so the iteration yields boxed/null objects, not an unbox.any that NREs on a null value element) and re-narrow
    // the loop var wherever it flows into a call arg back to the original `gp:X` (unbox.any at the value consumer).
            // Wrap every reference to the (now-`object`) loop var `lv` that appears as a CALL argument in a `cast`->`origElem`
    // (the Tv), so a value-type consumer unbox.any's the boxed element. The null-check use (`objEq(element, null)`) is
    // NOT a call arg and is correctly left as `object`.
        // BUG-1 Part B: for each method, find a `forEachInline` whose SOURCE is a param typed as a nullable-generic
    // collection (`...[nullable:gp:X]`) and whose loop-var `elem` is the bare `gp:X`; erase the loop-var to `object`
    // (so the iteration yields boxed/null objects, not an unbox.any that NREs on a null value element) and re-narrow
    // the loop var wherever it flows into a call arg back to the original `gp:X` (unbox.any at the value consumer).
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

    // A field/property whose slot is a nullable generic parameter (`type:"gp:T"` + sibling `nullable:true`) -> the
    // reference `object` slot. Only the boolean-marked `gp:` form; the inline `nullable:gp:T` form (should it appear
    // on a decl `type`) is caught by the blanket EraseNullableGpAllStrings sweep.
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
    static void EraseNullableGpAllStrings(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) obj[key] = TypeJson.Write(EraseNullableTv(tn));
                    else EraseNullableGpAllStrings(child);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) arr[i] = TypeJson.Write(EraseNullableTv(tn));
                    else EraseNullableGpAllStrings(child);
                }
                break;
        }
    }

    // #120: the "allocate a fresh reified array, fill it, cast it back to a non-null `Array<T>`" idiom —
    //   val result = arrayOfNulls<T>(n)          // (or `Array(n){...}` -> a newArray* node): a genuine `newarr !T`
    //   for (...) result[i] = ...
    //   return result as Array<T>
    // (`Array<T>.plus`/`plusElement`/`Collection.toTypedArray`). kotc declares `result: Array<T?>` (Array(Nullable(Tv)))
    // and ArrayConstructionLowering stamps its `arraySet`/`arrayGet` `elem` as Nullable(Tv). The blanket
    // EraseNullableGpAllStrings sweep would object-erase both to `object[]` / `object`, but the allocation stays
    // `newarr !T` (MemberCallSubstitution's LOAD-BEARING exact-reified path) and the trailing `result as Array<T>` needs
    // a real `T[]` — an `object[]` slot / `stelem object` over a reified `T[]` corrupts a value-type instantiation (int
    // slots read back as garbage; `arrayOf(1,2,3).plus(4)` printed random ints). Collapse this ONE fresh-local chain to
    // bare `!T` (the var slot, its own `newArray*` elem, and every `arraySet`/`arrayGet` on it) so var-slot / newarr /
    // stelem / ldelem / cast all agree; the later sweep + ReferenceNullableStrip are then no-ops on it.
    //
    // PRODUCER + CONSUMER gated (chain consistency), NOT node-kind — a producer-blind gate reintroduces value-type
    // miscompiles (#120 review): a var whose init is NOT a direct fresh allocation (a `cond`/param alias — RingBuffer.
    // toArray's `if (..) copyOf(..) else array as Array<T?>`), or which is NOT consumed by a bare `Array<T>` cast
    // (`copyOf(newSize)`'s `return result` into its object-erased `Array<T?>` RETURN), genuinely flows an `object[]` and
    // is LEFT for the blanket sweep to object-erase — keeping `!T` there would `stelem !T` over an `object[]`. Likewise
    // an `arraySet` on a cast/param operand (terminateCollectionToArray's `(array as Array<T?>)[i] = null`) is untouched:
    // its operand is not a fresh-local kept var.
    static void CollapseReifiedArrayVars(JsonObject mo)
    {
        if (mo["body"] is not JsonNode body) return;
        // (1) fresh reified-array locals: init is a direct `newArray*`/`arrayOfNulls`, declared type Array(Nullable(Tv)).
        var fresh = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        CollectFreshReifiedVars(body, fresh);
        if (fresh.Count == 0) return;
        // (2) keep only those consumed by a bare `Array<Tv>` cast (the reify-back idiom) — excludes `copyOf(newSize)`.
        var kept = new HashSet<string>(StringComparer.Ordinal);
        CollectBareArrayCastLocals(body, fresh.Keys, kept);
        if (kept.Count == 0) return;
        // (3) collapse the kept chain to bare `!T`.
        foreach (var name in kept)
        {
            var v = fresh[name];
            ReifiedArrayVars.Add(v);
            if (TypeJson.Read(v["type"]) is TypeNode.Array { Elem: TypeNode.Nullable { Of: TypeNode.Tv tv } })
                v["type"] = TypeJson.Write(new TypeNode.Array(tv));
            if (v["init"] is JsonObject vi
                && (vi["k"] as JsonValue)?.GetValue<string>() is "newArray" or "newArraySized" or "newArrayInit"
                && TypeJson.Read(vi["elem"]) is TypeNode.Nullable { Of: TypeNode.Tv itv })
                vi["elem"] = TypeJson.Write(itv);
        }
        CollapseKeptArrayOps(body, kept);
    }

    // A body-local `var name: Array<T?>` (Array(Nullable(Tv))) whose init is a genuine `newarr !T` producer — a
    // `newArray`/`newArraySized`/`newArrayInit` node, or the `arrayOfNulls<T>(size)` factory callStatic (which
    // MemberCallSubstitution later lowers to `newArraySized`). NOT a `cond` / cast / other call.
    static void CollectFreshReifiedVars(JsonNode node, Dictionary<string, JsonObject> fresh)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.GetValue<string>() == "var"
                    && (obj["name"] as JsonValue)?.TryGetValue<string>(out var nm) == true
                    && TypeJson.Read(obj["type"]) is TypeNode.Array { Elem: TypeNode.Nullable { Of: TypeNode.Tv } }
                    && IsFreshReifiedAlloc(obj["init"]))
                    fresh[nm] = obj;
                foreach (var kv in obj) CollectFreshReifiedVars(kv.Value, fresh);
                break;
            case JsonArray arr:
                foreach (var it in arr) CollectFreshReifiedVars(it, fresh);
                break;
        }
    }

    static bool IsFreshReifiedAlloc(JsonNode init)
    {
        if (init is not JsonObject io) return false;
        var k = (io["k"] as JsonValue)?.GetValue<string>();
        if (k is "newArray" or "newArraySized" or "newArrayInit") return true;
        return k == "callStatic" && (io["method"] as JsonValue)?.GetValue<string>() == "arrayOfNulls";
    }

    // Names in `candidates` that flow into a bare `cast`->`Array<Tv>` (`result as Array<T>` — the reify-back consumer
    // that requires a real `T[]`). A fresh reified local with NO such cast (its value is used as `Array<T?>`, e.g.
    // returned into an object-erased `Array<T?>` return) is excluded — object-erasure is correct for it.
    static void CollectBareArrayCastLocals(JsonNode node, IEnumerable<string> candidates, HashSet<string> kept)
    {
        var cand = candidates as HashSet<string> ?? new HashSet<string>(candidates, StringComparer.Ordinal);
        void Rec(JsonNode n)
        {
            switch (n)
            {
                case JsonObject obj:
                    if ((obj["k"] as JsonValue)?.GetValue<string>() == "cast"
                        && TypeJson.Read(obj["type"]) is TypeNode.Array { Elem: TypeNode.Tv }
                        && obj["e"] is JsonObject e
                        && (e["k"] as JsonValue)?.GetValue<string>() == "local"
                        && (e["name"] as JsonValue)?.TryGetValue<string>(out var en) == true && cand.Contains(en))
                        kept.Add(en);
                    foreach (var kv in obj) Rec(kv.Value);
                    break;
                case JsonArray arr:
                    foreach (var it in arr) Rec(it);
                    break;
            }
        }
        Rec(node);
    }

    // Collapse the `elem` of every `arraySet`/`arrayGet` whose array operand is a kept fresh-local (`Nullable(Tv)`->`Tv`),
    // so the stelem/ldelem token agrees with the now-`!T[]` slot + the `newarr !T` allocation.
    static void CollapseKeptArrayOps(JsonNode node, HashSet<string> kept)
    {
        switch (node)
        {
            case JsonObject obj:
                var k = (obj["k"] as JsonValue)?.GetValue<string>();
                if ((k == "arraySet" || k == "arrayGet")
                    && obj["array"] is JsonObject a
                    && (a["k"] as JsonValue)?.GetValue<string>() == "local"
                    && (a["name"] as JsonValue)?.TryGetValue<string>(out var an) == true && kept.Contains(an)
                    && TypeJson.Read(obj["elem"]) is TypeNode.Nullable { Of: TypeNode.Tv tv })
                    obj["elem"] = TypeJson.Write(tv);
                foreach (var kv in obj) CollapseKeptArrayOps(kv.Value, kept);
                break;
            case JsonArray arr:
                foreach (var it in arr) CollapseKeptArrayOps(it, kept);
                break;
        }
    }

    // `Erase`: replace every `Nullable(Tv)` (a value-type-nullable type variable) with `object`, recursively and at
    // every position. A function type's RETURN obeys the same rule as its params and receiver — `Fn.Ret` had a verbatim
    // carve-out that handed a top-level `T?` return to NullableFuncReturnErasure, which is one Kotlin type constructor
    // with two owners and, before #142 narrowed it, produced a `newDelegate.funcType.ret` inconsistent with the lifted
    // lambda's own signature (ilverify DelegateCtor "Unrecognized arguments"). NullableFuncReturnErasure keeps only its
    // CONCRETE-value-inner half (`(T) -> Int?`), a different family that this rule does not reach.
    internal static TypeNode EraseNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => new TypeNode.Fqn("object"),
        TypeNode.Nullable n => new TypeNode.Nullable(EraseNullableTv(n.Of)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(EraseNullableTv).ToArray()),
        TypeNode.Array a => new TypeNode.Array(EraseNullableTv(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(EraseNullableTv(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, EraseNullableTv(fn.Ret),
            fn.Params.Select(EraseNullableTv).ToArray(),
            fn.Recv == null ? null : EraseNullableTv(fn.Recv)),
        _ => t,
    };

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
