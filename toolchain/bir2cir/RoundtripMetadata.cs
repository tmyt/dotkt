using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

// ROUNDTRIP-METADATA GENERATION (#71 S2). bir2cir GENERATES every Kotlin round-trip metadata attribute — as ordinary
// CIR `attrs`/`retAttrs` entries {attr:fqn,args:[…]} + the embedded attribute-class DEFS — and ilemit only STAMPS them
// dumbly through its generic BuildCab/ConstArgValue path. This is the "ilemit knows NO Kotlin" endgame: the whole
// [Kotlin*]/[Nullable]/[NullableContext] generation block + EnsureKotlinAttrs + every Apply* was deleted from ilemit;
// the Kotlin-semantic DECISION (which modifier -> which attribute, the flags bit-pack, the scalar-vs-array Nullable
// collapse) now lives HERE, the Kotlin<->CLR layer.
//
// Runs in the ref (metadata) + app builds — the two that carry the round-trip surface — and is SKIPPED in the runtime
// build (`!SubstituteStdlibBuild`), the gate that REPLACES ilemit's deleted `_stripMetadata`. Reads the already-
// materialized facts (post-BirTypeLowering): mods(infix/operator/suspend/inline/fun/sealed), suspendBridge,
// retNullableFlags/nullableFlags (DeclNullableFlags), retSuspendFnType/suspendFnType (BirTypeLowering's suspend-fn-type
// erasure records the pre-erasure shape as this fact), readOnly, and the S1 `inlineBir` carrier string.
//
// The embedded attribute-class defs (the DotKt.Runtime.CompilerServices.* set + System.Runtime.CompilerServices.Nullable{,Context})
// are emitted ONCE per assembly as a DEDICATED synthetic CIR file (SynthDefsFile) — `internal sealed : System.Attribute`
// with base-chaining ctors — so ilemit defines them like any type (no EnsureKotlinAttrs). NullableAttribute carries the
// csc DUAL ctor: (byte) scalar + (byte[]) nested; the two overloads are disambiguated by BuildCab's runtime-type match.
//
// ATTR ORDER (per emitted member) reproduces ilemit's old stamp order verbatim, so a metadata dump stays equivalent:
//   type:   [NullableContext, …user, KotlinFileClass?/KotlinFunInterface?, KotlinSealed?, KotlinValue?]
//   method: [ …user, KotlinFunction?, KotlinInline? ]      ret: [ Nullable?, KotlinSuspendFunctionType?, KotlinExtensionFunctionType?, KotlinNothing? ]
//   param:  [ Nullable?, KotlinSuspendFunctionType?, …user, KotlinExtensionFunctionType? ]
//   field:  [ Nullable?, KotlinReadOnly?, KotlinSuspendFunctionType?, …user, KotlinExtensionFunctionType? ]   (#47 Nullable)
//   prop:   [ Nullable?, KotlinSuspendFunctionType?, …user, KotlinExtensionFunctionType? ]                    (#47 Nullable)
static class RoundtripMetadata
{
    const string Ns = "DotKt.Runtime.CompilerServices.";
    const string ClrNs = "System.Runtime.CompilerServices.";
    const string AKFunction     = Ns + "KotlinFunctionAttribute";
    const string AKFileClass    = Ns + "KotlinFileClassAttribute";
    const string AKInline       = Ns + "KotlinInlineAttribute";
    const string AKReadOnly     = Ns + "KotlinReadOnlyAttribute";
    const string AKFunInterface = Ns + "KotlinFunInterfaceAttribute";
    const string AKSealed       = Ns + "KotlinSealedAttribute";
    const string AKValue        = Ns + "KotlinValueAttribute";
    const string AKSuspendFn    = Ns + "KotlinSuspendFunctionTypeAttribute";
    const string AKExtFn        = Ns + "KotlinExtensionFunctionTypeAttribute";
    const string AKNothing      = Ns + "KotlinNothingAttribute";
    const string AKNullableGen  = Ns + "KotlinNullableGenericAttribute";
    const string AKCollIdentity = Ns + "KotlinCollectionIdentityAttribute";
    const string ANullable      = ClrNs + "NullableAttribute";
    const string ANullableCtx   = ClrNs + "NullableContextAttribute";

    // ---------------------------------------------------------------------------------------------------------------
    // STAMP one lowered CIR file: add `attrs`/`retAttrs` entries derived from the decls' round-trip facts.
    // ---------------------------------------------------------------------------------------------------------------
    public static void Stamp(JsonNode root)
    {
        if (root is not JsonObject o) return;
        // File-class markers ride the ROOT's attrs (ilemit reads root.attrs onto the file-class TypeBuilder). Harmless
        // if the file declares no top-level funs/fields (then no file-class TB exists and the attrs are never read).
        Prepend(o, ByteMarker(ANullableCtx, 1));   // [NullableContext(1)] — the per-type non-null NRT default.
        Append(o, Marker(AKFileClass));            // [KotlinFileClass]
        StampMethods(o["methods"]);
        // Top-level file-class `val`/`var` static fields carry NO [KotlinReadOnly] (the old file-class field path
        // stamped only [KotlinSuspendFunctionType]; a `val`'s read-only-ness rode the CLR property, not the field).
        StampFields(o["fields"], topLevel: true);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) StampType(to);
    }

    static void StampType(JsonObject to)
    {
        // A real CLR enum lowers to an EnumBuilder (ilemit ti.TB == null) — it carries NO round-trip metadata (a
        // [NullableContext] stamp would NRE on the null builder). A rich enum is a `kind:"class"` singleton and IS stamped.
        if ((to["kind"] as JsonValue)?.GetValue<string>() == "enum") return;
        Prepend(to, ByteMarker(ANullableCtx, 1));                     // [NullableContext(1)]
        if (ModFlag(to, "fun")) Append(to, Marker(AKFunInterface));  // `fun interface` (SAM)
        if (ModFlag(to, "sealed")) Append(to, Marker(AKSealed));     // `sealed` class/interface
        // `value`/inline class (`IrClass.isValue`). The 2.4.0 frontend no longer materializes @kotlin.jvm.JvmInline
        // into the IR, so this [KotlinValue] marker is the ROUND-TRIP carrier of value-ness that ReferenceMetadataIndex
        // reads back off the ref/rt DLL to drive the single-field erase-to-underlying lowering.
        if (ModFlag(to, "value")) Append(to, Marker(AKValue));       // `value` class (@JvmInline)
        StampMethods(to["methods"]);
        StampFields(to["fields"]);
        StampProps(to["properties"]);
        if (to["ctors"] is JsonArray ctors)
            foreach (var c in ctors) if (c is JsonObject co) StampParams(co["params"]);
        if (to["types"] is JsonArray nested)
            foreach (var t in nested) if (t is JsonObject nto) StampType(nto);
    }

    static void StampMethods(JsonNode methods)
    {
        if (methods is not JsonArray a) return;
        foreach (var m in a) if (m is JsonObject mo) StampMethod(mo);
    }

    static void StampMethod(JsonObject mo)
    {
        // [KotlinFunction(flags)] — Kotlin modifiers with no .NET analog. suspendBridge is the bir2cir-synthesized
        // Task<R> bridge that IS the suspend fun's CLR ABI (facadegen must see it as `suspend fun`).
        int flags = 0;
        if (ModFlag(mo, "infix")) flags |= 1;
        if (ModFlag(mo, "operator")) flags |= 2;
        if (ModFlag(mo, "suspend")) flags |= 4;
        if ((mo["suspendBridge"] as JsonValue)?.GetValue<bool>() == true) flags |= 4;
        if (flags != 0) Append(mo, Marker(AKFunction, IntArg(flags)));
        // [KotlinInline(version, bytes)] — the S1 raw-BIR carrier, stamped verbatim (inlineBir is already the base64
        // of BirCarrier.EncodeBody). Only for an inline fn that actually stashed a carrier.
        if (ModFlag(mo, "inline") && (mo["inlineBir"] as JsonValue)?.GetValue<string>() is string ib)
            Append(mo, Marker(AKInline, StringArg(BirCarrier.JsonV1), BytesArg(ib)));

        // Return-position attrs ride `retAttrs` (ilemit stamps them on DefineParameter(0)). Order: [Nullable, SuspendFn,
        // Nothing]. [KotlinNothing] (#133 case3) rides the SAME channel; it goes AFTER the [Nullable] byte so a `Nothing?`
        // return's NRT byte (computed from the pre-erasure type via retNullableFlags, unperturbed here) composes on top —
        // facadegen reads the marker by presence (HasNothingMarker), order-independent, and the Nullable byte separately.
        var ret = new JsonArray();
        if (mo["retNullableFlags"] is JsonArray rnf && NullableAttr(rnf) is JsonObject rna) ret.Add(rna);
        if (mo["retSuspendFnType"] is JsonNode rsf) ret.Add(SuspendFnAttr(rsf));
        // [KotlinExtensionFunctionType] (#145) — a bare marker: a method returning `P.() -> R`. Unlike suspend, the
        // delegate is NOT erased (the receiver rides DelegateParams as the first CLR type arg), so no shape is carried —
        // facadegen reads the marker and moves the delegate's first arg back into the fn's receiver.
        if (HasRecvFn(mo["ret"])) ret.Add(Marker(AKExtFn));
        if ((mo["retNothing"] as JsonValue)?.GetValue<bool>() == true) ret.Add(Marker(AKNothing));
        // [KotlinNullableGeneric(version, bytes)] (#18) — a `fun <T> …(): Holder<T?>` whose nested `Nullable(Tv)` arg
        // NullableGenericReturnErasure object-erased to `Holder<object>`. The carrier holds the PRE-erasure return
        // TypeNode (recorded as the opaque `nullableGenericRet` string) so facadegen restores `Holder<T?>` instead of
        // degrading the re-imported factory/member return to `Any?`. Rides the SAME retAttrs channel as [Nullable]/[Nothing].
        if ((mo["nullableGenericRet"] as JsonValue)?.GetValue<string>() is string ngr) ret.Add(NullableGenAttr(ngr));
        // [KotlinCollectionIdentity(version, bytes)] (#29) — a return that nests a read-only `List/Set/Collection`
        // whose Root-V collapse to `IList`/`ICollection` erased the read-only-vs-mutable identity. Carries the
        // PRE-collapse Kotlin TypeNode (recorded as the opaque `collIdentityRet` string) so facadegen restores
        // `List` vs `MutableList` at every nested position. Rides the retAttrs channel like [Nullable]/[Nothing].
        if ((mo["collIdentityRet"] as JsonValue)?.GetValue<string>() is string cir) ret.Add(CollIdentityAttr(cir));
        if (ret.Count > 0) mo["retAttrs"] = ret;

        StampParams(mo["params"]);
    }

    static void StampParams(JsonNode ps)
    {
        if (ps is not JsonArray a) return;
        foreach (var p in a) if (p is JsonObject po)
        {
            // Prepend [Nullable, KotlinSuspendFunctionType] BEFORE any user param attr (ilemit's old order).
            if (po["suspendFnType"] is JsonNode sf) Prepend(po, SuspendFnAttr(sf));
            if (po["nullableFlags"] is JsonArray nf && NullableAttr(nf) is JsonObject na) Prepend(po, na);
            // [KotlinExtensionFunctionType] (#145) — a `block: P.() -> R` param; the bare marker rides after any user
            // attr (order-independent — facadegen reads it by presence). The delegate keeps `P` as its first arg.
            if (HasRecvFn(po["type"])) Append(po, Marker(AKExtFn));
            // [KotlinCollectionIdentity] (#29) — a param nesting a collapsed read-only collection.
            if ((po["collIdentity"] as JsonValue)?.GetValue<string>() is string ci) Append(po, CollIdentityAttr(ci));
        }
    }

    static void StampFields(JsonNode fs, bool topLevel = false)
    {
        if (fs is not JsonArray a) return;
        foreach (var f in a) if (f is JsonObject fo)
        {
            // Prepend [Nullable, KotlinReadOnly, KotlinSuspendFunctionType] (Nullable outermost). [KotlinReadOnly] is
            // INSTANCE-field only — a top-level file-class static field never carried it (byte-equivalence with the old
            // file-class field path). #47: the `nullableFlags` NRT byte (DeclNullableFlags) rides here regardless of
            // topLevel, so a nullable field surfaces as `T?` on re-import (facadegen's FieldTypeN reads it via ApplyNrt).
            if (fo["suspendFnType"] is JsonNode sf) Prepend(fo, SuspendFnAttr(sf));
            if (!topLevel && (fo["readOnly"] as JsonValue)?.GetValue<bool>() == true) Prepend(fo, Marker(AKReadOnly));
            if (fo["nullableFlags"] is JsonArray nf && NullableAttr(nf) is JsonObject na) Prepend(fo, na);
            if (HasRecvFn(fo["type"])) Append(fo, Marker(AKExtFn));   // a `val handler: P.() -> R` field (#145)
            if ((fo["collIdentity"] as JsonValue)?.GetValue<string>() is string ci) Append(fo, CollIdentityAttr(ci));  // #29
        }
    }

    static void StampProps(JsonNode props)
    {
        if (props is not JsonArray a) return;
        foreach (var p in a) if (p is JsonObject po)
        {
            // Prepend [Nullable, KotlinSuspendFunctionType] (Nullable outermost — same order as params). #47: a
            // `val/var x: T?` property carries its NRT byte here (from DeclNullableFlags' nullableFlags); facadegen's
            // PropTypeN reads it via ApplyNrt, so `val text: String?` re-imports nullable instead of degrading to
            // non-null. A `val/var x: suspend (…) -> T` carries the pre-erasure `fn` shape (incl. an extension recv)
            // restored by facadegen's SuspendFnNode.
            if (po["suspendFnType"] is JsonNode sf) Prepend(po, SuspendFnAttr(sf));
            if (po["nullableFlags"] is JsonArray nf && NullableAttr(nf) is JsonObject na) Prepend(po, na);
            if (HasRecvFn(po["type"])) Append(po, Marker(AKExtFn));   // a `val p: P.() -> R` property (#145)
            if ((po["collIdentity"] as JsonValue)?.GetValue<string>() is string ci) Append(po, CollIdentityAttr(ci));  // #29
        }
    }

    // #145 — true iff a `type`/`ret` slot holds a NON-suspend receiver function type (`fn` with a `recv`). bir2cir
    // keeps such a slot as a faithful CLR delegate (LowerFnDelegate preserves `recv`; ilemit's DelegateParams prepends
    // it as the first arg), so — unlike the suspend carrier — there is NO shape to record: the bare marker plus the
    // delegate's own type args fully reconstruct `P.() -> R`. (A SUSPEND receiver fn is erased to `object` and rides the
    // suspendFnType carrier instead, so it never reaches here.)
    static bool HasRecvFn(JsonNode slot) =>
        slot is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s == "fn"
        && o["recv"] is JsonObject
        && !((o["suspend"] as JsonValue)?.GetValue<bool>() == true);

    // ---------------------------------------------------------------------------------------------------------------
    // RUNTIME build: strip the COMPILE-TIME-ONLY carriers from `attrs`/`retAttrs`, KEEPING genuine USER annotations
    // (#47). RoundtripMetadata itself never runs in the rt build, so the attrs present are kotc's verbatim annotations:
    // the @Clr* binding surface (`kotlin.clr.*`) + the round-trip carriers (`DotKt.Runtime.CompilerServices.*` / NRT
    // `System.Runtime.CompilerServices.Nullable{,Context}`) — both compile-time-only, ref-side facts that must NOT ship
    // on DotKt.Stdlib.dll (the never-metadata-read runtime assembly, substituted away at app-emit) — AND the user's own
    // annotations (kotlin.Deprecated / SinceKotlin / InlineOnly / PublishedApi / …). The old strip dropped EVERYTHING,
    // silently losing the user annotations (this bug); now the predicate keeps them and drops only the internal carriers.
    // ---------------------------------------------------------------------------------------------------------------
    public static void StripRuntimeAttrs(JsonNode root)
    {
        if (root is not JsonObject o) return;
        StripAttrs(o, "attrs");
        StripDecls(o["methods"], hasParams: true);
        StripDecls(o["fields"]);
        StripDecls(o["properties"]);
        StripDecls(o["ctors"], hasParams: true);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) StripRuntimeAttrs(to);
    }

    static void StripDecls(JsonNode arr, bool hasParams = false)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a) if (d is JsonObject po)
        {
            StripAttrs(po, "attrs");
            StripAttrs(po, "retAttrs");
            if (hasParams) StripDecls(po["params"]);
        }
    }

    // Remove ONLY the compile-time-only carriers from a decl's `attrs`/`retAttrs` array, leaving user annotations. An
    // array that empties out is removed entirely (byte-equivalence with a decl that never had the key).
    static void StripAttrs(JsonObject decl, string key)
    {
        if (decl[key] is not JsonArray a) return;
        for (int i = a.Count - 1; i >= 0; i--)
            if (a[i] is JsonObject ao && TypeJson.OwnerName(ao["attr"]) is string fqn && IsRuntimeStrippable(fqn))
                a.RemoveAt(i);
        if (a.Count == 0) decl.Remove(key);
    }

    // A compile-time-only carrier (round-trip surface / @Clr* binding / NRT) — dropped from the shipping runtime dll.
    // Everything else survives: the user's own annotations (kotlin.Deprecated/SinceKotlin/PublishedApi/Volatile), and —
    // matching the app/ref builds, which keep them too (kotc serializes every annotation verbatim, no retention filter)
    // — the compiler-internal resolution hints (kotlin.Suppress, kotlin.internal.*). Those hints are inert on the rt.dll
    // (never metadata-read), so keeping them costs nothing and keeps rt consistent with app/ref; a retention-aware filter
    // (SOURCE-retention annotations should ship on NO binary) is a broader downstream policy, deliberately NOT folded here.
    static bool IsRuntimeStrippable(string fqn) =>
        fqn.StartsWith(Ns, StringComparison.Ordinal)          // DotKt.Runtime.CompilerServices.* round-trip carriers
        || fqn.StartsWith("kotlin.clr.", StringComparison.Ordinal)  // @Clr* binding surface (ref-side only)
        || fqn == ANullable || fqn == ANullableCtx;           // NRT [Nullable]/[NullableContext]

    // ---------------------------------------------------------------------------------------------------------------
    // ATTRIBUTE-INSTANCE builders (a CIR `attrs` entry {attr, args:[…]} routed through ilemit's generic BuildCab).
    // ---------------------------------------------------------------------------------------------------------------

    // [Nullable(byte)] scalar for a single reference position, [Nullable(byte[])] nested for a flattened NRT walk —
    // reproducing ilemit's `flags.Length == 1 -> scalar ctor` collapse so the emitted metadata is byte-equivalent.
    static JsonObject NullableAttr(JsonArray flags)
    {
        if (flags.Count == 0) return null;   // defensive (old ApplyNullable returned on empty); unreachable via NullableFlags.Compute
        if (flags.Count == 1)
            return Marker(ANullable, ByteArg((flags[0] as JsonValue)!.GetValue<int>()));
        var bytes = new byte[flags.Count];
        for (int i = 0; i < flags.Count; i++) bytes[i] = (byte)(flags[i] as JsonValue)!.GetValue<int>();
        return Marker(ANullable, BytesArg(Convert.ToBase64String(bytes)));
    }

    // [KotlinSuspendFunctionType(version, bytes)] — the pre-erasure `fn` shape node, carrier-encoded (same envelope as
    // KotlinInline). Encoding the SAME JsonNode ilemit used to Parse-then-ToJsonString keeps the payload bytes equal.
    static JsonObject SuspendFnAttr(JsonNode shape)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, shape.DeepClone());
        return Marker(AKSuspendFn, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    // [KotlinNullableGeneric(version, bytes)] (#18) — the pre-erasure return TypeNode, carrier-encoded (same envelope as
    // KotlinSuspendFunctionType). `nullableGenericRet` was stashed as a canonical TypeNode JSON STRING (opaque to the
    // intervening type-rewriting passes); parse it back to a JsonNode so the carrier payload is the structured node
    // facadegen's TypeNode.Parse reads.
    static JsonObject NullableGenAttr(string typeJson)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, JsonNode.Parse(typeJson));
        return Marker(AKNullableGen, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    // [KotlinCollectionIdentity(version, bytes)] (#29) — the PRE-collapse Kotlin TypeNode, carrier-encoded (same
    // envelope as KotlinNullableGeneric). `collIdentity`/`collIdentityRet` was stashed as a canonical TypeNode JSON
    // STRING (opaque to the intervening type-rewriting passes); parse it back to a JsonNode so the carrier payload is
    // the structured node facadegen's TypeNode.Parse reads to restore `List` vs `MutableList` at each nested position.
    static JsonObject CollIdentityAttr(string typeJson)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, JsonNode.Parse(typeJson));
        return Marker(AKCollIdentity, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    static JsonObject Marker(string attr, params JsonObject[] args)
    {
        var arr = new JsonArray();
        foreach (var a in args) arr.Add(a);
        return new JsonObject { ["attr"] = TypeJson.Fqn(attr), ["args"] = arr };   // `attr` is a structured `{t:fqn}` node (#48)
    }

    static JsonObject ByteMarker(string attr, int v) => Marker(attr, ByteArg(v));

    static JsonObject IntArg(int v) => new() { ["value"] = v, ["type"] = Fqn("System.Int32") };
    static JsonObject ByteArg(int v) => new() { ["value"] = v, ["type"] = Fqn("System.Byte") };
    static JsonObject StringArg(string s) => new() { ["value"] = s, ["type"] = Fqn("System.String") };
    // A `bytes` arg-value kind (base64): ilemit's ConstArgValue decodes it to a real byte[] (mutually exclusive with
    // `value`/`type`). Used for the carrier payloads AND the nested NullableAttribute(byte[]) form.
    static JsonObject BytesArg(string base64) => new() { ["bytes"] = base64 };

    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };

    // ---------------------------------------------------------------------------------------------------------------
    // attrs-array mutation helpers (create on first use).
    // ---------------------------------------------------------------------------------------------------------------
    static void Append(JsonObject decl, JsonObject attr)
    {
        if (decl["attrs"] is not JsonArray a) { a = new JsonArray(); decl["attrs"] = a; }
        a.Add(attr);
    }

    static void Prepend(JsonObject decl, JsonObject attr)
    {
        if (decl["attrs"] is not JsonArray a) { a = new JsonArray(); decl["attrs"] = a; }
        a.Insert(0, attr);
    }

    static bool ModFlag(JsonObject obj, string name) =>
        obj["mods"] is JsonObject m && (m[name] as JsonValue)?.GetValue<bool>() == true;

    // ---------------------------------------------------------------------------------------------------------------
    // The embedded attribute-class defs, emitted ONCE as a dedicated synthetic CIR file. Each is `internal sealed :
    // System.Attribute` with the same ctor overloads ilemit's DefineEmbeddedAttr{,N} used to synthesize. `final:true`
    // -> TypeAttributes.Sealed (matching the old NotPublic|Sealed|Class). Ctor params carry NO name (a named ctor param
    // would mint Param rows the embedded attrs never had); the empty body chains to Attribute()'s protected ctor.
    // ---------------------------------------------------------------------------------------------------------------
    public static JsonObject SynthDefsFile()
    {
        var types = new JsonArray
        {
            AttrClass(AKFunction, Ctor(Param("System.Int32"))),
            AttrClass(AKFileClass, Ctor()),
            AttrClass(AKInline, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKReadOnly, Ctor()),
            AttrClass(AKFunInterface, Ctor()),
            AttrClass(AKSealed, Ctor()),
            AttrClass(AKValue, Ctor()),
            AttrClass(AKSuspendFn, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKExtFn, Ctor()),     // #145 — bare marker: a `P.() -> R` receiver function-type position
            AttrClass(AKNothing, Ctor()),   // #133 case3 — bare marker on a Kotlin `Nothing` return
            AttrClass(AKNullableGen, Ctor(Param("System.String"), Param(ByteArrayType()))),  // #18 — carrier of a pre-erasure `Holder<T?>` return
            AttrClass(AKCollIdentity, Ctor(Param("System.String"), Param(ByteArrayType()))), // #29 — carrier of a pre-collapse `Box<List<T>>` collection identity
            // NullableAttribute — csc's DUAL ctor: (byte) FIRST, (byte[]) SECOND (declaration order preserved so the
            // MethodDef rows and BuildCab's arity fallback stay deterministic).
            AttrClass(ANullable, Ctor(Param("System.Byte")), Ctor(Param(ByteArrayType()))),
            AttrClass(ANullableCtx, Ctor(Param("System.Byte"))),
        };
        return new JsonObject
        {
            ["fileClass"] = "",     // no top-level funs/fields -> ilemit defines no file-class type for this file
            ["hasMain"] = false,
            ["methods"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["types"] = types,
        };
    }

    static JsonObject AttrClass(string fqn, params JsonObject[] ctors)
    {
        var carr = new JsonArray();
        foreach (var c in ctors) carr.Add(c);
        return new JsonObject
        {
            ["name"] = fqn,
            ["kind"] = "class",
            ["vis"] = "internal",
            ["final"] = true,                                // -> TypeAttributes.Sealed
            ["base"] = Fqn("System.Attribute"),
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["methods"] = new JsonArray(),
            ["ctors"] = carr,
        };
    }

    static JsonObject Ctor(params JsonNode[] paramTypes)
    {
        var ps = new JsonArray();
        foreach (var t in paramTypes) ps.Add(t);
        return new JsonObject { ["vis"] = "public", ["params"] = ps, ["body"] = new JsonArray() };
    }

    // A ctor param with a bare CLR type and NO name (byte-equivalence: no Param table row).
    static JsonObject Param(string fqn) => new() { ["type"] = Fqn(fqn) };
    static JsonObject Param(JsonNode type) => new() { ["type"] = type };

    static JsonObject ByteArrayType() => new() { ["t"] = "array", ["elem"] = Fqn("System.Byte") };
}
