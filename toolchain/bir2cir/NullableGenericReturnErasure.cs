using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Erase a nullable generic-parameter return (`fun <T> …(): T?`, kotc-lowered to `ret=gp:X` + `retNullable=true`)
// to a `System.Object` return — the only CLR representation of a generic `T?` that can carry a real null for a
// VALUE-type instantiation. The method body's `ldnull` (null case) then stays a genuine null; value returns are
// boxed by ilemit's return/cond emitters; and the CALL boundary (ilemit) converts the object back to the caller's
// statically-known Nullable<V> (unbox.any) or reference type (castclass). Runs in EVERY build so the ref.dll and
// rt.dll signatures — and the app's view of them — agree. A no-op for a method that is not a nullable-generic return.
static class NullableGenericReturnErasure
{
    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o) return;
        // #18 ROUND-TRIP RECORD (runs BEFORE the erasure below): capture the PRE-erasure return type of every method
        // whose return is a CONSTRUCTED generic / array / byref carrying a NESTED `Nullable(Tv)` (`fun <T> …(): Holder<T?>`
        // — the atomicArrayOfNulls shape). The erasure turns that arg into `object` (`Holder<object>`), which facadegen
        // then CANNOT read back — it degrades the whole re-imported factory/member return to `Any?`, hiding every member
        // of the generic result. Stash the pre-erasure node so RoundtripMetadata stamps it as [KotlinNullableGeneric] and
        // facadegen restores `Holder<T?>`. See RecordNullableGenericRets.
        RecordNullableGenericRets(o);
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

    // #18: for every method whose return type carries a NESTED `Nullable(Tv)` (a `Holder<T?>` / `Array<T?>` / `Ref<T?>` —
    // NOT a bare top-level `T?`, which is a different object-erasure axis), record the PRE-erasure return TypeNode on the
    // method as the OPAQUE JSON STRING `nullableGenericRet`. Stored as a STRING (not a `{t:…}` type node) so the later
    // ReferenceNullableStrip / BirTypeLowering passes — which walk and rewrite structured type slots — leave it untouched;
    // RoundtripMetadata reads it back at stamp time and carrier-encodes it into [KotlinNullableGeneric]. Recurses into
    // nested types so a member of a generic class (`AtomicArray<T>.get(): AtomicRef<T?>`) is covered as well.
    static void RecordNullableGenericRets(JsonObject o)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo
                    && TypeJson.Read(mo["ret"]) is TypeNode ret
                    && ret is not TypeNode.Nullable { Of: TypeNode.Tv }   // exclude the bare top-level `T?` return
                    && HasRestorableNullableTv(ret))                      // a nested `Nullable(Tv)` facadegen can walk back
                    mo["nullableGenericRet"] = TypeNode.ToJson(ret);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) RecordNullableGenericRets(to);
    }

    // True iff `t` carries a `Nullable(Tv)` reachable purely through Fqn-args / Array / ByRef / Nullable — i.e. a shape
    // facadegen's RestoreNullableGeneric can walk in parallel with the emitted IL type. A `Nullable(Tv)` nested INSIDE an
    // `Fn` (a `(T?) -> R` delegate return arg) is DELIBERATELY excluded: facadegen restores delegate internals via its
    // own NRT-threaded MapTFn path (and NullableFuncReturnErasure owns the func-return erasure), so recording it would
    // strand the fn-shaped return on a non-NRT fallback. Records exactly the constructed-generic / array / byref returns.
    static bool HasRestorableNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => true,
        TypeNode.Nullable n => HasRestorableNullableTv(n.Of),
        TypeNode.Fqn { Args: { } args } => args.Any(HasRestorableNullableTv),
        TypeNode.Array a => HasRestorableNullableTv(a.Elem),
        TypeNode.ByRef b => HasRestorableNullableTv(b.Of),
        _ => false,   // Fn / bare Fqn / Tv / Oblivious: no restorable nested Nullable(Tv)
    };

    static void ApplyRec(JsonObject o)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods) ApplyToMethod(m);
        // FIELD / PROPERTY nullable-generic erasure (FIX 1 part-1). kotc marks a nullable-generic field/property
        // slot with a SEPARATE `"nullable":true` boolean next to `"type":"gp:T"` (a bare `gp:T` slot silently drops
        // the `?`, so a value-type instantiation stores default(T)=0 instead of a real null). Rewrite the `type` to
        // `object` so the slot becomes a reference slot holding a genuine null; ilemit boxes the value store and the
        // read boundary re-narrows (unbox.any / castclass), mirroring the return-erasure boundary handling.
        //
        // ACCESSOR + READER consistency (bundle-6 BUG-1: value-type `asSequence().filter{}` InvalidProgram). The
        // erased-to-`object` property must ALSO drag its ACCESSOR methods to `object` — otherwise `get_nextItem():gp:T`
        // reads the object field and returns an unboxed gp:T (invalid), and `set_nextItem(null)` pushes ldnull into a
        // value-type gp:T param slot (invalid). ilemit boxes a value arg into an object param and unbox.any's an
        // `as T` cast, but it does NOT unbox object->gp:T on a bare store/return. So we (a) retype the getter return
        // and setter param to `object`, and (b) retype any local `var` initialized from that getter to `object`, so
        // the trailing `result as T` (already present: `return result as T`) performs the single unbox.any. The
        // property METADATA row was already erased to object above, keeping row/getter/setter coherent (ilverify-clean).
        var getters = new HashSet<string>(StringComparer.Ordinal);
        var setters = new HashSet<string>(StringComparer.Ordinal);
        CollectNullableAccessors(o["properties"], getters, setters);
        EraseNullableGpDecls(o["fields"]);
        EraseNullableGpDecls(o["properties"]);
        // GENERAL body-local nullable-generic erasure (bundle-6 value-type-nullable LOCAL, the twin of the field/property
        // pass above). kotc marks a `var single: T? = null` value-type-nullable accumulator LOCAL with a sibling
        // `"nullable":true` next to `"type":"gp:T"`. Left as-is, the value-type `T` slot holds a null → the trailing
        // `single as T` unbox.any NREs (Sequence.single{}'s terminal). RetypeNullableGpVars erases the slot to `object`
        // (a real null survives; value stores box; the `as T` read re-narrows) — see there for why it gates on a
        // null-const init (to skip kotc's synthetic safe-call temps, whose implicit reads would corrupt).
        if (o["methods"] is JsonArray msLocals)
            foreach (var m in msLocals)
                if (m is JsonObject mo) RetypeNullableGpVars(mo["body"]);
        // #120 REIFIED-ARRAY reify-back idiom. Runs BEFORE the blanket EraseNullableGpAllStrings sweep (Apply, after
        // ApplyRec) so the kept chain is already bare `!T` when the sweep (a no-op on bare tv) runs. See there.
        if (o["methods"] is JsonArray msArrays)
            foreach (var m in msArrays)
                if (m is JsonObject mo) CollapseReifiedArrayVars(mo);
        // FOREACH-OVER-NULLABLE-GENERIC-SOURCE erasure (bundle-6 BUG-1, value-type filterNotNull). A stdlib method
        // whose extension receiver is `Iterable<T?>` (kotc token `@kotlin.collections.Iterable[nullable:gp:T]`, erased
        // by the EraseNullableGpAllStrings sweep below to `IEnumerable<object>`) iterates it with a `forEachInline`
        // whose loop-var `elem` is the bare `gp:T`. When T is instantiated with a VALUE type, storing the object
        // `Current` (the typed enumerator is unavailable — ilemit falls back to the non-generic enumerator + Unbox_Any
        // for a `gp:T` elem) into the value slot unbox.any's a null element -> NRE (filterNotNullTo). Erase the loop-var
        // to `object` (the object enumerator yields object; a null survives), and re-narrow the loop var where it flows
        // into a value-typed call arg (clrCollAdd's `gp:T` param) via a `cast`->`gp:T` (unbox.any for value, castclass
        // for ref). The RECEIVER-side boxing (a value-type collection is NOT covariantly IEnumerable<object> on the CLR)
        // is the call-site's job (ValueTypeNullableCollectionArg). This is the loop-var twin of EraseNullableGpDecls.
        EraseForEachOverNullableGpSource(o);
        if ((getters.Count > 0 || setters.Count > 0) && o["methods"] is JsonArray ms2)
        {
            foreach (var m in ms2)
                if (m is JsonObject mo && (mo["name"] as JsonValue)?.GetValue<string>() is string nm)
                {
                    if (getters.Contains(nm)) mo["ret"] = TypeJson.Fqn("object");
                    if (setters.Contains(nm) && mo["params"] is JsonArray ps)
                        foreach (var p in ps)
                            if (p is JsonObject po && TypeJson.Read(po["type"]) is TypeNode.Tv or TypeNode.Nullable { Of: TypeNode.Tv })
                                po["type"] = TypeJson.Fqn("object");
                }
            if (getters.Count > 0)
                foreach (var m in ms2)
                    if (m is JsonObject mo) RetypeGetterReaderVars(mo["body"], getters);
            // Re-narrow the CALL-NODE `retType` of every read of an erased getter to `object`. kotc stamped the
            // call node with the property's declared (nullable-generic) return `gp:T`; the getter now RETURNS
            // `object`, so a stale `gp:T` retType makes ilemit insert a coercion unbox.any right after the call —
            // and when the read is ALSO wrapped in an explicit `as T` cast (`nextValue as T`, the common
            // `T?`-property reader), the cast unbox.any's AGAIN → a DOUBLE `unbox.any !T` that NREs on the
            // second (the first already produced a bare value, not a boxed reference). This is the reader twin of
            // the `mo["ret"]="object"` accessor erasure above: the call node's retType must agree with the
            // callee's (now-object) return so exactly ONE narrow (the source `as T`) survives. (SequenceBuilder
            // `next()`'s `nextValue as T` on a VALUE element was the symptom — a cold-sequence NRE.)
            if (getters.Count > 0)
                foreach (var m in ms2)
                    if (m is JsonObject mo) RetypeErasedGetterCalls(mo["body"], getters);
            // Force the value->object box at each CALL to an erased setter. ilemit cannot read the param types off a
            // TypeBuilder-re-anchored generic self-call (`set_nextItem` on `dotkt_obj146[gp:T]`), so its arg-coercion
            // silently skips the box: a `gp:T` value arg lands on the stack unboxed where the now-`object` param wants a
            // reference -> InvalidProgram in calcNext. Wrapping the arg in an explicit `cast`->object boxes it from the
            // SOURCE type (ilemit's cast emitter boxes a value/generic-param source), independent of param-type lookup.
            if (setters.Count > 0)
                foreach (var m in ms2)
                    if (m is JsonObject mo) WrapErasedSetterArgs(mo["body"], setters);
        }
        // Nested types (a generic class' member methods / fields) carry their own declaration lists.
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) ApplyRec(to);
    }

    // Wrap each argument of a `callInstance` to an erased setter (`set_X` in `setters`) in a `cast`->`object`, so ilemit
    // boxes a value/generic-param arg into the erased `object` param even when it can't resolve the re-anchored generic
    // method's param types. A `null`/already-reference arg becomes a redundant `castclass object` (valid, no box).
    static void WrapErasedSetterArgs(JsonNode node, HashSet<string> setters)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "callInstance"
                    && (obj["method"] as JsonValue)?.TryGetValue<string>(out var mn) == true && setters.Contains(mn)
                    && obj["args"] is JsonArray a)
                    for (var i = 0; i < a.Count; i++)
                        if (a[i] is JsonObject arg && (arg["k"] as JsonValue)?.GetValue<string>() != "cast")
                            a[i] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("object"), ["e"] = arg.DeepClone() };
                foreach (var kv in obj) WrapErasedSetterArgs(kv.Value, setters);
                break;
            case JsonArray arr:
                foreach (var it in arr) WrapErasedSetterArgs(it, setters);
                break;
        }
    }

    // Record the get_/set_ accessor names of every nullable generic-parameter PROPERTY (`type:"gp:T"` + `nullable:true`)
    // — captured BEFORE EraseNullableGpDecls rewrites the property type to `object` (the `gp:` test would then miss).
    static void CollectNullableAccessors(JsonNode arr, HashSet<string> getters, HashSet<string> setters)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            // #37/#48: a nullable generic-parameter property is the TYPE NODE `{t:nullable,of:{t:tv}}` (was `gp:T` +
            // the retired scalar `nullable` flag). Capture its accessor names BEFORE the type is erased to `object`.
            if (d is JsonObject po
                && TypeJson.Read(po["type"]) is TypeNode.Nullable { Of: TypeNode.Tv })
            {
                if ((po["get"] as JsonValue)?.TryGetValue<string>(out var g) == true && g != null) getters.Add(g);
                if ((po["set"] as JsonValue)?.TryGetValue<string>(out var s) == true && s != null) setters.Add(s);
            }
    }

    // Retype a local `var x: gp:T = <call to an erased getter>()` slot to `object`, so the object value read from the
    // now-`object` getter is held in a reference local until an explicit `as T` re-narrows it. Only the direct
    // reader-local pattern (init is a callInstance to a getter in `getters`); other uses re-narrow via their own cast.
    static void RetypeGetterReaderVars(JsonNode node, HashSet<string> getters)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "var"
                    && TypeJson.Read(obj["type"]) is TypeNode.Tv or TypeNode.Nullable { Of: TypeNode.Tv }
                    && obj["init"] is JsonObject init
                    && (init["k"] as JsonValue)?.TryGetValue<string>(out var ik) == true && ik == "callInstance"
                    && (init["method"] as JsonValue)?.TryGetValue<string>(out var im) == true && getters.Contains(im))
                    obj["type"] = TypeJson.Fqn("object");
                foreach (var kv in obj) RetypeGetterReaderVars(kv.Value, getters);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeGetterReaderVars(it, getters);
                break;
        }
    }

    // Retype the `retType` of every `callInstance` reading an erased getter (`get_X` in `getters`) to `object`, so
    // the CIR call node agrees with the getter's now-`object` return. Without this, a stale `retType:"gp:T"` makes
    // ilemit coerce (unbox.any) the object result to the value type at the call — and a wrapping `as T` cast then
    // unbox.any's the already-unboxed value AGAIN, NREing. Retyping to `object` leaves a single narrow (the `as T`).
    static void RetypeErasedGetterCalls(JsonNode node, HashSet<string> getters)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "callInstance"
                    && (obj["method"] as JsonValue)?.TryGetValue<string>(out var mn) == true && getters.Contains(mn)
                    && TypeJson.Read(obj["ret"]) is TypeNode.Tv or TypeNode.Nullable { Of: TypeNode.Tv })
                    obj["ret"] = TypeJson.Fqn("object");
                foreach (var kv in obj) RetypeErasedGetterCalls(kv.Value, getters);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeErasedGetterCalls(it, getters);
                break;
        }
    }

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
    static void RetypeNullableGpVars(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                // #37/#48: a value-type-nullable accumulator local is `{t:nullable,of:{t:tv}}` (was `gp:T` + the retired
                // scalar `nullable` flag). The blanket EraseNullableGpAllStrings sweep deliberately SKIPS body-local var
                // type slots (it can no longer tell an accumulator from a safe-call temp — both are now identical nodes),
                // so this init-gated pass OWNS them: erase to `object` ONLY the null-const / Map.get idiom (the case that
                // genuinely needs a surviving null), leaving safe-call temps to lower to the bare `gp:T` (see the WHY-GATE
                // note above) — the surviving safe-call temp's `{t:nullable,of:{t:tv}}` is stripped to bare `gp:T` by
                // BirTypeLowering (an unconstrained tv is reference-treated), preserving the old bare-`gp:T` behavior.
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "var"
                    && TypeJson.Read(obj["type"]) is TypeNode.Nullable { Of: TypeNode.Tv }
                    && (IsNullConstInit(obj["init"]) || IsNullableGenericMapGet(obj["init"]) || IsNullableFuncReturnInvoke(obj["init"])))
                    obj["type"] = TypeJson.Fqn("object");
                foreach (var kv in obj) RetypeNullableGpVars(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeNullableGpVars(it);
                break;
        }
    }

    // True when a var initializer is a `Map`/`MutableMap` `.get(key)` call — its Kotlin result is `V?`, which
    // MemberCallSubstitution rewrites to the erased nullable-generic `clrMapGet<K,V>: object` (a present value boxes,
    // a missing key is a genuine `null`). A `var value: gp:V nullable:true = get(key)` slot (getOrPut's explicit
    // `val value = get(key)`, unlike getOrElse's `?:`-synthesized `object` subject) must therefore be an `object`
    // slot — else the object init is stored raw into a `!!V` slot and the `value == null` check never sees the null
    // (getOrPut on `MutableMap<K,primitive>` silently returned 0 and never inserted). The read boundary re-narrows:
    // `objEq(value, null)` reads it as `object`; the `else value` branch (cond typed `gp:V`) unbox.any's it back
    // (EmitNullableCoerced). Gated on the `overrides` marker (owner Map/MutableMap, member `get`), so it never hits
    // the safe-call receiver temps RetypeNullableGpVars deliberately excludes (those init from a `transform(x)` invoke).
    static bool IsNullableGenericMapGet(JsonNode init)
    {
        if (init is not JsonObject io) return false;
        if ((io["k"] as JsonValue)?.TryGetValue<string>(out var ik) != true || ik != "callInstance") return false;
        if (io["overrides"] is not JsonArray ovs) return false;
        foreach (var ov in ovs)
            if (ov is JsonObject oo
                && (oo["member"] as JsonValue)?.TryGetValue<string>(out var mem) == true && mem == "get"
                && TypeJson.OwnerName(oo["owner"]) is string own
                && (own == "kotlin.collections.Map" || own == "kotlin.collections.MutableMap"))
                return true;
        return false;
    }

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
    static bool IsNullableFuncReturnInvoke(JsonNode init)
    {
        if (init is not JsonObject io) return false;
        if ((io["k"] as JsonValue)?.TryGetValue<string>(out var ik) != true || ik != "delegateInvoke") return false;
        return TypeJson.Read(io["funcType"]) is TypeNode.Fn { Ret: TypeNode.Nullable { Of: TypeNode.Tv } };
    }

    // True when a var initializer is the null literal (`{k:"const", value:null}`) — the `T? = null` accumulator idiom.
    // A JSON null property surfaces as a C# null JsonNode, so a `const` whose `value` node is null IS the null literal.
    static bool IsNullConstInit(JsonNode init) =>
        init is JsonObject io
        && (io["k"] as JsonValue)?.TryGetValue<string>(out var ik) == true && ik == "const"
        && io.ContainsKey("value") && io["value"] is null;

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
    // value-type-nullable type variable `T?`) erases to `object` wherever it sits (a clrg-nested type-arg / field /
    // standalone-param), the same value-type-null fault as the return case. Mirrors NullableFuncReturnErasure.
    static void EraseNullableGpAllStrings(JsonNode node, bool inParams = false)
    {
        switch (node)
        {
            case JsonObject obj:
                // #37/#48 (Codex-confirmed Option A): a body-local `var`'s TOP-LEVEL `{t:nullable,of:{t:tv}}` type slot
                // is NOT erased here — under the unified type-node encoding a safe-call receiver temp and a genuine
                // accumulator are IDENTICAL nodes, and the init-gated RetypeNullableGpVars (which already ran) owns that
                // discrimination. NESTED nullable-tv (a `var x: List<T?>` generic arg — a value-instantiation lifeline)
                // is still erased. Every non-var / structural position (fields, returns, generic args, call sigs)
                // keeps the uniform erasure.
                var isVar = (obj["k"] as JsonValue)?.GetValue<string>() == "var";
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    if (isVar && key == "type" && TypeJson.Read(child) is TypeNode.Nullable { Of: TypeNode.Tv }) continue;
                    // A declaration PARAM's TOP-LEVEL `T?` (`{t:nullable,of:{t:tv}}`) is NOT erased to `object` here (#37/#48
                    // round-trip): kept as `Nullable(Tv)`, DeclNullableFlags stamps its NRT byte [2] and BirTypeLowering
                    // strips it to the bare generic-param `T` + a `NullableAttribute(2)`. This preserves the type-param
                    // IDENTITY in the emitted signature so facadegen reconstructs `x: T?` (not the T-less `Any?` that made
                    // `T` uninferable — roundtrip-generic `orDefault<T>(x: T?, …)`). Mirrors the pre-#48 bare-`gp:T`+flag
                    // param (the JVM-idiom object-erasure applied to inline `nullable:gp:` returns/locals, not to params).
                    // NESTED nullable-tv in a param (`Iterable<T?>`) still erases via EraseNullableTv (the Fqn recursion).
                    if (inParams && key == "type" && TypeJson.Read(child) is TypeNode.Nullable { Of: TypeNode.Tv }) continue;
                    // A call's `sig` is a STRUCTURED TypeNode array (#37 m3b), so its `nullable:gp:X` (Nullable(Tv))
                    // elements erase to `object` for free via the array-recursion below (EraseNullableTv) — DEF and CALL
                    // sigs stay in agreement structurally, no sig-string special case needed.
                    if (TypeJson.Read(child) is TypeNode tn) obj[key] = TypeJson.Write(EraseNullableTv(tn));
                    else EraseNullableGpAllStrings(child, inParams: key == "params");
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    // A `params` element's OWN top-level nullable-tv is preserved (handled in the JsonObject case via
                    // `inParams`); its nested nullable-tv still erases. Non-param arrays (`sig`, generic args) erase fully.
                    if (inParams && child is JsonObject) EraseNullableGpAllStrings(child, inParams: true);
                    else if (TypeJson.Read(child) is TypeNode tn) arr[i] = TypeJson.Write(EraseNullableTv(tn));
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

    // Replace every `Nullable(Tv)` (a value-type-nullable type variable) with `object`, recursively. LEAVES a func
    // return that is a TOP-LEVEL `T?` (`Fn.Ret` = `Nullable(Tv)`) for NullableFuncReturnErasure (erasing it here would
    // blind that pass); a func param/receiver nullable-tv — and a NESTED nullable-tv inside a CONSTRUCTED-generic return
    // (`AtomicRef<T?>`, a `Fqn` with a `Nullable(Tv)` arg) — is erased. #142: the old blanket `fn.Ret` verbatim carve-out
    // let `AtomicRef<Nullable(Tv)>` survive in the `newDelegate.funcType.ret` position ONLY (a `Fqn`, so NullableFunc-
    // ReturnErasure — which only fires on a top-level `Nullable(Tv)` ret — skips it too), while the SAME type erased to
    // `AtomicRef<object>` in the method-return/array-elem positions; ReferenceNullableStrip then stripped the surviving
    // `Nullable(Tv)` arg to a bare `tv`, leaving the funcType.ret `AtomicRef<!T>` internally inconsistent with the
    // `__lambda0` method signature `AtomicRef<object>` → ilverify DelegateCtor "Unrecognized arguments". Narrowing the
    // carve-out to the top-level `Nullable(Tv)` return makes funcType / method-signature / array-elem agree end-to-end.
    internal static TypeNode EraseNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => new TypeNode.Fqn("object"),
        TypeNode.Nullable n => new TypeNode.Nullable(EraseNullableTv(n.Of)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(EraseNullableTv).ToArray()),
        TypeNode.Array a => new TypeNode.Array(EraseNullableTv(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(EraseNullableTv(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            fn.Ret is TypeNode.Nullable { Of: TypeNode.Tv } ? fn.Ret : EraseNullableTv(fn.Ret),
            fn.Params.Select(EraseNullableTv).ToArray(),
            fn.Recv == null ? null : EraseNullableTv(fn.Recv)),
        _ => t,
    };

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

    static void ApplyToMethod(JsonNode m)
    {
        if (m is not JsonObject mo) return;
        // #37/#48: the nullable generic return is the TYPE NODE `{t:nullable,of:{t:tv}}` (was a bare `gp:X` ret + a
        // retired scalar `retNullable` flag). Erase it to `object` — the only CLR rep of a generic `T?` that carries a
        // real null for a value-type instantiation.
        if (TypeJson.Read(mo["ret"]) is not TypeNode.Nullable { Of: TypeNode.Tv gp }) return;
        mo["ret"] = TypeJson.Fqn("object");
        // A return-value expression whose STATIC type is the (now-erased) `gp:X` must also flow as object so its
        // null/value coercion targets object: a `return (cond typed gp:X)` (if-empty-null-else-elem) and a
        // `return (delegating call retType=gp:X)` (find -> firstOrNull) both become object end-to-end.
        RetypeReturns(mo["body"], gp);
    }

    static void RetypeReturns(JsonNode node, TypeNode.Tv gp)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "return"
                    && obj["value"] is JsonObject v)
                {
                    if (TypeJson.Read(v["type"]) is TypeNode.Tv vt && vt == gp) v["type"] = TypeJson.Fqn("object");
                    if (TypeJson.Read(v["ret"]) is TypeNode.Tv vr && vr == gp) v["ret"] = TypeJson.Fqn("object");
                }
                foreach (var kv in obj) RetypeReturns(kv.Value, gp);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeReturns(it, gp);
                break;
        }
    }
}

