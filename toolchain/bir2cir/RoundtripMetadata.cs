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
// The 10 attribute-class defs (8 DotKt.Runtime.CompilerServices.* + System.Runtime.CompilerServices.Nullable{,Context})
// are emitted ONCE per assembly as a DEDICATED synthetic CIR file (SynthDefsFile) — `internal sealed : System.Attribute`
// with base-chaining ctors — so ilemit defines them like any type (no EnsureKotlinAttrs). NullableAttribute carries the
// csc DUAL ctor: (byte) scalar + (byte[]) nested; the two overloads are disambiguated by BuildCab's runtime-type match.
//
// ATTR ORDER (per emitted member) reproduces ilemit's old stamp order verbatim, so a metadata dump stays equivalent:
//   type:   [NullableContext, …user, KotlinFileClass?/KotlinFunInterface?, KotlinSealed?, KotlinValue?]
//   method: [ …user, KotlinFunction?, KotlinInline? ]      ret: [ Nullable?, KotlinSuspendFunctionType?, KotlinNothing? ]
//   param:  [ Nullable?, KotlinSuspendFunctionType?, …user ]
//   field:  [ KotlinReadOnly?, KotlinSuspendFunctionType?, …user ]
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
    const string AKNothing      = Ns + "KotlinNothingAttribute";
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
        if ((mo["retNothing"] as JsonValue)?.GetValue<bool>() == true) ret.Add(Marker(AKNothing));
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
        }
    }

    static void StampFields(JsonNode fs, bool topLevel = false)
    {
        if (fs is not JsonArray a) return;
        foreach (var f in a) if (f is JsonObject fo)
        {
            // Prepend [KotlinReadOnly, KotlinSuspendFunctionType]. [KotlinReadOnly] is INSTANCE-field only — a top-level
            // file-class static field never carried it (byte-equivalence with the old file-class field path).
            if (fo["suspendFnType"] is JsonNode sf) Prepend(fo, SuspendFnAttr(sf));
            if (!topLevel && (fo["readOnly"] as JsonValue)?.GetValue<bool>() == true) Prepend(fo, Marker(AKReadOnly));
        }
    }

    static void StampProps(JsonNode props)
    {
        if (props is not JsonArray a) return;
        // A `val/var x: suspend (…) -> T` property carries the pre-erasure shape; ilemit stamped only [KotlinSuspendFunctionType]
        // on properties (never [Nullable]), so reproduce exactly that.
        foreach (var p in a) if (p is JsonObject po && po["suspendFnType"] is JsonNode sf)
            Prepend(po, SuspendFnAttr(sf));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // RUNTIME build: strip EVERY applied `attrs`/`retAttrs` (this is what ilemit's deleted `_stripMetadata` did — its
    // `if (_stripMetadata) continue` skipped the whole pass-4 block, dropping the kotc user annotations kotlin.Deprecated/
    // SinceKotlin/InlineOnly/… too, and its param-attr gate dropped [ClrRefArgument]). RoundtripMetadata itself never
    // runs in the rt build, so there are no round-trip attrs to strip — only kotc's verbatim user annotations. Keeps
    // DotKt.Stdlib.dll (the shipping runtime assembly, never metadata-read) lean and byte-equivalent to the old strip.
    // ---------------------------------------------------------------------------------------------------------------
    public static void StripRuntimeAttrs(JsonNode root)
    {
        if (root is not JsonObject o) return;
        o.Remove("attrs");
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
            po.Remove("attrs");
            po.Remove("retAttrs");
            if (hasParams) StripDecls(po["params"]);
        }
    }

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

    static JsonObject Marker(string attr, params JsonObject[] args)
    {
        var arr = new JsonArray();
        foreach (var a in args) arr.Add(a);
        return new JsonObject { ["attr"] = attr, ["args"] = arr };
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
    // The 10 embedded attribute-class defs, emitted ONCE as a dedicated synthetic CIR file. Each is `internal sealed :
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
            AttrClass(AKNothing, Ctor()),   // #133 case3 — bare marker on a Kotlin `Nothing` return
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
