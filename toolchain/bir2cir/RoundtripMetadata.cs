using System;
using System.Collections.Generic;
using System.Linq;
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
// The DotKt.Runtime.CompilerServices.* carrier defs are emitted ONCE per assembly as a DEDICATED synthetic CIR file
// (SynthDefsFile). The standard NullableAttribute/NullableContextAttribute types are target-BCL declarations and each
// application is marked external; duplicating those TypeDefs in every output would create a second CLR authority.
//
// ATTR ORDER (per emitted member) keeps the established order and inserts each newer carrier beside its slot peers:
//   type:   [NullableContext, …user, KotlinFileClass?/KotlinFunInterface?, KotlinSealed?, KotlinValue?]
//   method: [ …user, KotlinFunction?, KotlinInline? ]      ret: [ Nullable?, KotlinSuspendFunctionType?, KotlinExtensionFunctionType?, KotlinNothing? ]
//   param:  [ Nullable?, KotlinSuspendFunctionType?, KotlinNullableGeneric?, …user, KotlinExtensionFunctionType?,
//             KotlinContextParameter? ]
//   field:  [ Nullable?, KotlinReadOnly?, KotlinSuspendFunctionType?, KotlinNullableGeneric?, …user,
//             KotlinExtensionFunctionType? ]
//   prop:   [ Nullable?, KotlinSuspendFunctionType?, KotlinNullableGeneric?, …user, KotlinExtensionFunctionType? ]
static class RoundtripMetadata
{
    const string Ns = "DotKt.Runtime.CompilerServices.";
    const string ClrNs = "System.Runtime.CompilerServices.";
    const string AKFunction     = Ns + "KotlinFunctionAttribute";
    const string AKFileClass    = Ns + "KotlinFileClassAttribute";
    const string AKInline       = Ns + "KotlinInlineAttribute";
    const string AKReadOnly     = Ns + "KotlinReadOnlyAttribute";
    const string AKLateinit     = Ns + "KotlinLateinitAttribute";
    const string AKFunInterface = Ns + "KotlinFunInterfaceAttribute";
    const string AKSealed       = Ns + "KotlinSealedAttribute";
    const string AKValue        = Ns + "KotlinValueAttribute";
    const string AKObject       = Ns + "KotlinObjectAttribute";
    const string AKInner        = Ns + "KotlinInnerAttribute";
    const string AKRichEnum     = Ns + "KotlinRichEnumAttribute";
    const string AKCompanion    = Ns + "KotlinCompanionAttribute";
    const string AKCompanionExt = Ns + "KotlinCompanionExtensionAttribute";
    const string AKPropertyAccessor = Ns + "KotlinPropertyAccessorAttribute";
    const string AKSourceMethod = Ns + "KotlinSourceMethodAttribute";
    const string AKDeclarationIdentity = Ns + "KotlinDeclarationIdentityAttribute";
    const string AKConstructorAdapter = Ns + "KotlinConstructorAdapterAttribute";
    internal const string AKPropertyStorage = Ns + "KotlinPropertyStorageAttribute";
    internal const string AKExtensionCore = Ns + "KotlinExtensionCoreAttribute";
    const string AKStaticCarrier = Ns + "KotlinStaticCarrierAttribute";
    const string AKSuspendResult = Ns + "KotlinSuspendResultAttribute";
    const string AKSuspendFn    = Ns + "KotlinSuspendFunctionTypeAttribute";
    const string AKExtFn        = Ns + "KotlinExtensionFunctionTypeAttribute";
    const string AKCtxParam     = Ns + "KotlinContextParameterAttribute";
    const string AKCtxFnType    = Ns + "KotlinContextFunctionTypeAttribute";
    const string AKNothing      = Ns + "KotlinNothingAttribute";
    // The pre-erasure declaration-slot carrier. `internal` because it has a second reader in this assembly:
    // ForeignNullableGenericCrossing decides which slot a body fills by reading the record back off the minted
    // attribute, and matching it by an exactly-named FQN is what keeps that read off any other attribute.
    internal const string AKNullableGen  = Ns + "KotlinNullableGenericAttribute";
    const string AKCollIdentity = Ns + "KotlinCollectionIdentityAttribute";
    const string AKType         = Ns + "KotlinTypeAttribute";
    const string AKSupertypes   = Ns + "KotlinSupertypesAttribute";
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
        // Accessor-routed top-level declarations use the same CLR Property metadata as member properties. Preserve
        // their nullable/suspend/context-function type carriers on the file facade as well; dll2klib reads those
        // attributes from the Property row when rebuilding the package declaration.
        StampProps(o["properties"]);
        if (o["types"] is JsonArray types)
        {
            MaterializeCompanionCarriers(types);
            foreach (var t in types) if (t is JsonObject to) StampType(to);
        }
    }

    // Freeze the logical result while declarations still use Kotlin TypeNodes. Most suspend declarations are replaced
    // by SuspendColdLowering, which authors this fact on their Task bridge directly. Compiler-provided residual
    // declarations (notably inline coroutine intrinsics) retain `mods.suspend`; they need the same current-format fact
    // before BirTypeLowering turns `suspendRet` into CLR vocabulary.
    public static void FreezeSuspendResults(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots) FreezeSuspendResults(root);
    }

    static void FreezeSuspendResults(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
            {
                if (method.ContainsKey("suspendResult") || !ModFlag(method, "suspend")) continue;
                var logical = (method["nullableGenericSuspendRet"] as JsonValue)?.GetValue<string>()
                    ?? method["suspendRet"]?.ToJsonString()
                    ?? throw new InvalidOperationException(
                        $"suspend declaration '{method["name"]}' has no logical result");
                method["suspendResult"] = logical;
            }
        if (obj["types"] is JsonArray types)
            foreach (var type in types) FreezeSuspendResults(type);
    }

    // CompanionRepresentationLowering has already selected and materialized the physical representation. Metadata
    // emission consumes only that explicit hand-off; declaration counts and object flags are not representation facts.
    static void MaterializeCompanionCarriers(JsonArray roots)
    {
        var types = new List<JsonObject>();
        void Collect(JsonArray declarations)
        {
            foreach (var node in declarations)
                if (node is JsonObject type)
                {
                    types.Add(type);
                    if (type["types"] is JsonArray nested) Collect(nested);
                }
        }
        Collect(roots);

        foreach (var type in types)
            if (type["companionCarrier"] is JsonObject carrier)
            {
                if (type["kotlinCompanion"] is not null)
                    throw new InvalidOperationException("companion carrier has an unconsumed semantic association");
                type["kotlinCompanion"] = carrier.DeepClone();
                type.Remove("companionCarrier");
            }
        foreach (var type in types)
            if (type["kotlinCompanion"] is JsonObject fact && fact["kind"] is null)
                throw new InvalidOperationException("semantic companion reached metadata emission without representation lowering");
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
        if (ModFlag(to, "object")) Append(to, Marker(AKObject));     // `object` singleton
        if (ModFlag(to, "inner"))
        {
            var capturedCount = to["capturedTypeParams"] is JsonArray captured ? captured.Count : 0;
            Append(to, Marker(AKInner, IntArg(capturedCount)));
        }
        // [KotlinRichEnum(version, bytes)] — a rich enum is physically a reference class so its entry-specific
        // subclasses can derive from it. The payload is the explicit Kotlin declaration-to-physical-member map;
        // downstream projection must not rediscover enum meaning from field or values()/valueOf() conventions.
        if (to["richEnum"] is JsonObject richEnum)
        {
            Append(to, JsonCarrierAttr(AKRichEnum, richEnum));
            to.Remove("richEnum");
            to.Remove("enumRich");
        }
        // [KotlinCompanion(version, bytes)] (#275) — the association, source name, and bir2cir-resolved physical
        // representation. MaterializeCompanionCarriers above consumes kotc's semantic-only {owner,name} fact.
        if (to["kotlinCompanion"] is JsonObject companion)
        {
            Append(to, JsonCarrierAttr(AKCompanion, companion));
            to.Remove("kotlinCompanion");
        }
        // A non-generic CLR implementation carrier for the one logical static surface of a generic Kotlin owner.
        // The payload names the semantic declaration owner; the attributed TypeDef itself is the physical owner.
        if (to["staticCarrier"] is JsonObject staticCarrier)
        {
            Append(to, JsonCarrierAttr(AKStaticCarrier, staticCarrier));
            to.Remove("staticCarrier");
        }
        // [KotlinType(version, bytes)] — a compiler-synthesized CLR type whose Kotlin surface is a different TypeNode.
        // FBoundStarProjectionErasure uses this on its non-generic existential interface so a downstream reader restores
        // the original G<*> projection rather than exposing the CLR implementation type or degrading it to Any?.
        // [KotlinSupertypes(version, bytes)] (#86) — the type's PRE-ERASURE supertype edges and type-parameter
        // bounds. A supertype argument erases like any other reified argument, and unlike a member slot there is no
        // per-slot attribute to hang the Kotlin type on: the edge itself is what a consumer binds to when it writes
        // `val s: Sink<Int?> = E()`. Same opaque TypeNode payload as every other carrier.
        if ((to[KotlinSupertypesRecord.PreKey] as JsonValue)?.GetValue<string>() is string sup)
        {
            Append(to, Marker(AKSupertypes, StringArg(BirCarrier.JsonV1),
                BytesArg(Convert.ToBase64String(BirCarrier.EncodeBody(BirCarrier.JsonV1, JsonNode.Parse(sup))))));
            to.Remove(KotlinSupertypesRecord.PreKey);
        }
        if ((to["kotlinType"] as JsonValue)?.GetValue<string>() is string kt)
        {
            Append(to, KotlinTypeAttr(kt));
            // This is a bir2cir-only hand-off between lowering passes. Final CIR contains only the fully-authored
            // attribute that ilemit emits 1:1; it must not retain an extra inference/input fact.
            to.Remove("kotlinType");
        }
        StampMethods(to["methods"]);
        StampFields(to["fields"]);
        StampProps(to["properties"]);
        if (to["ctors"] is JsonArray ctors)
            foreach (var c in ctors) if (c is JsonObject co)
            {
                if ((co["aliasCtorAdapter"] as JsonValue)?.GetValue<string>() is string adapter)
                {
                    Append(co, Marker(AKConstructorAdapter,
                        StringArg(BirCarrier.JsonV1), BytesArg(adapter)));
                    co.Remove("aliasCtorAdapter");
                }
                StampParams(co["params"]);
            }
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
        // [KotlinDeclarationIdentity(version, bytes)] (#395) — the exact frontend declaration fingerprint and source spelling.
        // bir2cir may have assigned a different MethodDef name after CLR erasure; dll2klib restores `name`, while a
        // consuming bir2cir binds `id` directly to this physical method without structural overload resolution.
        if ((mo[DeclarationIdentityBinding.Key] as JsonValue)?.TryGetValue<string>(out var declarationId) == true)
        {
            var sourceName = (mo["declarationSourceName"] as JsonValue)?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    $"declaration identity '{declarationId}' on physical method '{mo["name"]}' has no source name");
            var identity = new JsonObject {
                ["id"] = declarationId,
                ["name"] = sourceName,
            };
            if (mo[DeclarationIdentityBinding.SemanticSignatureKey] is JsonObject semanticSignature)
                identity["signature"] = semanticSignature.DeepClone();
            if (mo[ReifiedNullabilityWitnessLowering.IndicesKey] is JsonArray reified)
                identity["reified"] = reified.DeepClone();
            Append(mo, JsonCarrierAttr(AKDeclarationIdentity, identity));
            mo.Remove(DeclarationIdentityBinding.Key);
            mo.Remove("declarationSourceName");
            mo.Remove(DeclarationIdentityBinding.SemanticSignatureKey);
            mo.Remove(ReifiedNullabilityWitnessLowering.IndicesKey);
        }
        // CLR Property rows cannot describe method-generic accessors. The allocator leaves this exact semantic
        // association only on those MethodDefs; turn it into trusted metadata before the hand-off fact disappears.
        StampPropertyAccessorCarrier(mo);
        // [KotlinFunction(flags)] — Kotlin modifiers with no .NET analog. suspendBridge is the bir2cir-synthesized
        // Task<R> bridge that IS the suspend fun's CLR ABI (dll2klib must see it as `suspend fun`).
        int flags = 0;
        if (ModFlag(mo, "infix")) flags |= 1;
        if (ModFlag(mo, "operator")) flags |= 2;
        if (ModFlag(mo, "suspend")) flags |= 4;
        if ((mo["suspendBridge"] as JsonValue)?.GetValue<bool>() == true) flags |= 4;
        if (flags != 0) Append(mo, Marker(AKFunction, IntArg(flags)));
        // [KotlinSuspendResult(version, bytes)] — the frontend-selected logical result of a suspend declaration.
        // The public CLR MethodDef returns Task/Task<T>; consumers must not reconstruct Kotlin meaning from that
        // physical shape. SuspendColdLowering freezes the pre-CLR TypeNode into this opaque hand-off string.
        if ((mo["suspendResult"] as JsonValue)?.TryGetValue<string>(out var suspendResult) == true)
        {
            Append(mo, JsonCarrierAttr(AKSuspendResult, JsonNode.Parse(suspendResult)));
            mo.Remove("suspendResult");
        }
        // [KotlinInline(version, bytes)] — the S1 raw-BIR carrier, stamped verbatim (inlineBir is already the base64
        // of BirCarrier.EncodeBody). Only for an inline fn that actually stashed a carrier.
        if (ModFlag(mo, "inline") && (mo["inlineBir"] as JsonValue)?.GetValue<string>() is string ib)
            Append(mo, Marker(AKInline, StringArg(BirCarrier.JsonV1), BytesArg(ib)));
        // [KotlinCompanionExtension(version, bytes)] — a Kotlin 2.4 `companion fun C.foo()`. The declaration is a
        // receiverless static of the file class physically; the carrier holds the KOTLIN type it is associated with,
        // which is the only thing a consuming module needs to spell `C.foo(...)` again. Opaque to CLR lowering.
        if ((mo["companionReceiver"] as JsonValue)?.GetValue<string>() is string mcr)
        {
            var sourceName = (mo["companionSourceName"] as JsonValue)?.GetValue<string>()
                ?? throw new InvalidOperationException("companion extension method has no source name");
            var kind = (mo["companionMemberKind"] as JsonValue)?.GetValue<string>()
                ?? throw new InvalidOperationException("companion extension method has no member kind");
            Append(mo, JsonCarrierAttr(AKCompanionExt, new JsonObject {
                ["receiver"] = JsonNode.Parse(mcr),
                ["name"] = sourceName,
                ["kind"] = kind,
            }));
            mo.Remove("companionReceiver");
            mo.Remove("companionSourceName");
            mo.Remove("companionMemberKind");
        }

        // Return-position attrs ride `retAttrs` (ilemit stamps them on DefineParameter(0)). Order: [Nullable, SuspendFn,
        // Nothing]. [KotlinNothing] (#133 case3) rides the SAME channel; it goes AFTER the [Nullable] byte so a `Nothing?`
        // return's NRT byte (computed from the pre-erasure type via retNullableFlags, unperturbed here) composes on top —
        // dll2klib reads the marker by presence (HasNothingMarker), order-independent, and the Nullable byte separately.
        var ret = new JsonArray();
        if ((mo["retKotlinType"] as JsonValue)?.GetValue<string>() is string rkt)
        {
            ret.Add(KotlinTypeAttr(rkt));
            mo.Remove("retKotlinType");
        }
        if (mo["retNullableFlags"] is JsonArray rnf && NullableAttr(rnf) is JsonObject rna) ret.Add(rna);
        if (mo["retSuspendFnType"] is JsonNode rsf) ret.Add(SuspendFnAttr(rsf));
        if (TakeInt(mo, "retCtxFnType") is int rctx) ret.Add(Marker(AKCtxFnType, IntArg(rctx)));
        // [KotlinExtensionFunctionType] (#145) — a bare marker: a method returning `P.() -> R`. Unlike suspend, the
        // delegate is NOT erased (the receiver rides DelegateParams as the first CLR type arg), so no shape is carried —
        // dll2klib reads the marker and moves the delegate's first arg back into the fn's receiver.
        if (HasRecvFn(mo["ret"])) ret.Add(Marker(AKExtFn));
        if ((mo["retNothing"] as JsonValue)?.GetValue<bool>() == true) ret.Add(Marker(AKNothing));
        // [KotlinNullableGeneric(version, bytes)] (#18/#147) — a `fun <T> …(): Holder<T?>` whose nested `Nullable(Tv)`
        // arg NullableGenericErasure object-erased to `Holder<object>`. The carrier holds the PRE-erasure return
        // TypeNode (recorded as the opaque `nullableGenericRet` string) so dll2klib restores `Holder<T?>` instead of
        // degrading the re-imported factory/member return to `Any?`. Rides the SAME retAttrs channel as [Nullable]/[Nothing].
        if ((mo["nullableGenericRet"] as JsonValue)?.GetValue<string>() is string ngr)
        {
            ret.Add(NullableGenAttr(ngr));
            mo.Remove("nullableGenericRet");
        }
        // [KotlinCollectionIdentity(version, bytes)] (#29) — a return that nests a read-only `List/Set/Collection`
        // whose Root-V collapse to `IList`/`ICollection` erased the read-only-vs-mutable identity. Carries the
        // PRE-collapse Kotlin TypeNode (recorded as the opaque `collIdentityRet` string) so dll2klib restores
        // `List` vs `MutableList` at every nested position. Rides the retAttrs channel like [Nullable]/[Nothing].
        if ((mo["collIdentityRet"] as JsonValue)?.GetValue<string>() is string cir) ret.Add(CollIdentityAttr(cir));
        if (ret.Count > 0) mo["retAttrs"] = ret;

        StampParams(mo["params"]);
    }

    static void StampPropertyAccessorCarrier(JsonObject method)
    {
        if (method[KotlinPropertyAccessors.MetadataCarrierKey] is not JsonObject propertyAccessor) return;
        if (!HasAttr(method, AKPropertyAccessor))
            Append(method, JsonCarrierAttr(AKPropertyAccessor, propertyAccessor));
        method.Remove(KotlinPropertyAccessors.MetadataCarrierKey);
    }

    static bool HasAttr(JsonObject declaration, string attributeName) =>
        declaration["attrs"] is JsonArray attrs && attrs.OfType<JsonObject>().Any(attribute =>
            TypeJson.OwnerName(attribute["attr"]) == attributeName);

    static void StampParams(JsonNode ps)
    {
        if (ps is not JsonArray a) return;
        foreach (var p in a) if (p is JsonObject po)
        {
            // Prepend declaration-slot carriers before user attrs. Reverse prepend order yields
            // [Nullable, KotlinType?, KotlinSuspendFunctionType?, KotlinNullableGeneric?, ...user].
            if ((po["nullableGeneric"] as JsonValue)?.GetValue<string>() is string ng)
            {
                Prepend(po, NullableGenAttr(ng));
                po.Remove("nullableGeneric");
            }
            if (po["suspendFnType"] is JsonNode sf) Prepend(po, SuspendFnAttr(sf));
            if ((po["kotlinType"] as JsonValue)?.GetValue<string>() is string kt)
            {
                Prepend(po, KotlinTypeAttr(kt));
                po.Remove("kotlinType");
            }
            if (po["nullableFlags"] is JsonArray nf && NullableAttr(nf) is JsonObject na) Prepend(po, na);
            // [KotlinExtensionFunctionType] (#145) — a `block: P.() -> R` param; the bare marker rides after any user
            // attr (order-independent — dll2klib reads it by presence). The delegate keeps `P` as its first arg.
            if (HasRecvFn(po["type"])) Append(po, Marker(AKExtFn));
            // [KotlinContextFunctionType(N)] — this slot's Kotlin TYPE was `context(A…) …`, and N of the delegate's
            // LEADING arguments are those contexts. It rides BESIDE [KotlinExtensionFunctionType] rather than
            // replacing it: `context(A) B.(D) -> E` is both (contexts first, then the receiver). Without it a consumer
            // promotes argument 0 — the CONTEXT — to the restored receiver, and a lambda's `this` binds to the wrong
            // value with no diagnostic.
            if (TakeInt(po, "ctxFnType") is int pctx) Append(po, Marker(AKCtxFnType, IntArg(pctx)));
            // [KotlinContextParameter] — a bare marker on a Kotlin CONTEXT parameter. It is physically an ordinary
            // positional parameter (kotc projects it as one), so without the marker a consuming Kotlin module would
            // restore it as a plain leading value parameter and the callee's SOURCE shape would change at the module
            // boundary. The `mods.context` flag is CONSUMED here (removed) — its whole purpose is this attribute.
            if (ModFlag(po, "context"))
            {
                Append(po, Marker(AKCtxParam));
                if (po["mods"] is JsonObject pmods)
                {
                    pmods.Remove("context");
                    if (pmods.Count == 0) po.Remove("mods");
                }
            }
            // [KotlinCollectionIdentity] (#29) — a param nesting a collapsed read-only collection.
            if ((po["collIdentity"] as JsonValue)?.GetValue<string>() is string ci) Append(po, CollIdentityAttr(ci));
        }
    }

    static void StampFields(JsonNode fs, bool topLevel = false)
    {
        if (fs is not JsonArray a) return;
        foreach (var f in a) if (f is JsonObject fo)
        {
            // Prepend [Nullable, KotlinReadOnly, KotlinType?, KotlinSuspendFunctionType?, KotlinNullableGeneric?].
            // [KotlinReadOnly] is INSTANCE-field only — a top-level file-class static field never carries it.
            if ((fo["nullableGeneric"] as JsonValue)?.GetValue<string>() is string ng)
            {
                Prepend(fo, NullableGenAttr(ng));
                fo.Remove("nullableGeneric");
            }
            if (fo["suspendFnType"] is JsonNode sf) Prepend(fo, SuspendFnAttr(sf));
            if ((fo["kotlinType"] as JsonValue)?.GetValue<string>() is string kt)
            {
                Prepend(fo, KotlinTypeAttr(kt));
                fo.Remove("kotlinType");
            }
            // An ordinary file-facade val is restored from its CLR Property row, so its backing field historically
            // needs no marker. A staged context-parameter companion-extension field is still restored from its
            // carrier field and therefore needs the declaration's val/var fact.
            if ((!topLevel || fo["companionReceiver"] is not null) &&
                (fo["readOnly"] as JsonValue)?.GetValue<bool>() == true)
                Prepend(fo, Marker(AKReadOnly));
            if (fo["nullableFlags"] is JsonArray nf && NullableAttr(nf) is JsonObject na) Prepend(fo, na);
            if (HasRecvFn(fo["type"])) Append(fo, Marker(AKExtFn));
            if (TakeInt(fo, "ctxFnType") is int fctx) Append(fo, Marker(AKCtxFnType, IntArg(fctx)));   // a `val handler: P.() -> R` field (#145)
            if ((fo["collIdentity"] as JsonValue)?.GetValue<string>() is string ci) Append(fo, CollIdentityAttr(ci));  // #29
            // The staged field twin of the method carrier above. Ordinary and generic properties have already
            // consumed these facts into the C# 14 extension-property graph; context cases retain this carrier until
            // their physical lowering lands in the next increment.
            if ((fo["companionReceiver"] as JsonValue)?.GetValue<string>() is string fcr)
            {
                var sourceName = (fo["companionSourceName"] as JsonValue)?.GetValue<string>()
                    ?? throw new InvalidOperationException("companion extension field has no source name");
                var kind = (fo["companionMemberKind"] as JsonValue)?.GetValue<string>()
                    ?? throw new InvalidOperationException("companion extension field has no member kind");
                Append(fo, JsonCarrierAttr(AKCompanionExt, new JsonObject {
                    ["receiver"] = JsonNode.Parse(fcr),
                    ["name"] = sourceName,
                    ["kind"] = kind,
                }));
                fo.Remove("companionReceiver");
                fo.Remove("companionSourceName");
                fo.Remove("companionMemberKind");
            }
            // CLR field metadata has no built-in analogue of Kotlin lateinit. Preserve the trusted declaration fact
            // explicitly so dll2klib can restore IS_LATEINIT without inspecting names, accessors, or method bodies.
            if (TakeBool(fo, "lateinit")) Append(fo, Marker(AKLateinit));
        }
    }

    static void StampProps(JsonNode props)
    {
        if (props is not JsonArray a) return;
        foreach (var p in a) if (p is JsonObject po)
        {
            if ((po["nullableGeneric"] as JsonValue)?.GetValue<string>() is string ng)
            {
                Prepend(po, NullableGenAttr(ng));
                po.Remove("nullableGeneric");
            }
            if ((po["kotlinType"] as JsonValue)?.GetValue<string>() is string kt)
            {
                Prepend(po, KotlinTypeAttr(kt));
                po.Remove("kotlinType");
            }
            // Prepend [Nullable, KotlinSuspendFunctionType] (Nullable outermost — same order as params). #47: a
            // `val/var x: T?` property carries its NRT byte here (from DeclNullableFlags' nullableFlags); dll2klib's
            // PropTypeN reads it via ApplyNrt, so `val text: String?` re-imports nullable instead of degrading to
            // non-null. A `val/var x: suspend (…) -> T` carries the pre-erasure `fn` shape (incl. an extension recv)
            // restored by dll2klib's SuspendFnNode.
            if (po["suspendFnType"] is JsonNode sf) Prepend(po, SuspendFnAttr(sf));
            if (po["nullableFlags"] is JsonArray nf && NullableAttr(nf) is JsonObject na) Prepend(po, na);
            if (HasRecvFn(po["type"])) Append(po, Marker(AKExtFn));   // a `val p: P.() -> R` property (#145)
            if (TakeInt(po, "ctxFnType") is int pctx2) Append(po, Marker(AKCtxFnType, IntArg(pctx2)));
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
        // FBoundStarProjectionErasure's pass-local hand-off is consumed only by Stamp in metadata-bearing builds.
        // The runtime build deliberately emits no round-trip attribute, but must still discard the temporary fact.
        o.Remove("kotlinType");
        o.Remove("retKotlinType");
        o.Remove(KotlinSupertypesRecord.PreKey);
        o.Remove("kotlinCompanion");
        o.Remove("richEnum");
        o.Remove("enumRich");
        StripAttrs(o, "attrs");
        StripDecls(o["methods"], hasParams: true);
        StripDecls(o["fields"]);
        StripDecls(o["properties"]);
        StripDecls(o["ctors"], hasParams: true);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) StripRuntimeAttrs(to);
    }

    // The nullable-generic slot record (`nullableGeneric`/`nullableGenericRet`) is deliberately NOT dropped here,
    // though this build mints nothing from it. It has a second reader that runs after every file is lowered —
    // ForeignNullableGenericCrossing decides which slot a Kotlin body fills by the pre-erasure type the erasure
    // recorded — and dropping it here left that reader with nothing to read in the runtime build, so the whole
    // concrete-override arm of the refusal was blind there. The record is consumed by that reader instead, in every
    // build, and so still reaches no CIR.
    static void StripDecls(JsonNode arr, bool hasParams = false)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a) if (d is JsonObject po)
        {
            po.Remove("kotlinType");
            po.Remove("retKotlinType");
            // A method-generic Kotlin property has no representable CLR Property signature. Metadata-bearing builds
            // consume this exact association into [KotlinPropertyAccessor]; the runtime twin emits no round-trip
            // metadata, so discard the same pass-local hand-off before CIR reaches ilemit.
            po.Remove(KotlinPropertyAccessors.MetadataCarrierKey);
            po.Remove(DeclarationIdentityBinding.Key);
            po.Remove("declarationSourceName");
            po.Remove(DeclarationIdentityBinding.SemanticSignatureKey);
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

    // [KotlinNullableGeneric(version, bytes)] (#18/#147) — a pre-erasure declaration-slot TypeNode, carrier-encoded
    // with the same envelope as KotlinSuspendFunctionType. The slot hand-off was stashed as canonical TypeNode JSON;
    // parse it back so the carrier payload is the structured node dll2klib reads.
    static JsonObject NullableGenAttr(string typeJson)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, JsonNode.Parse(typeJson));
        return Marker(AKNullableGen, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    // [KotlinCollectionIdentity(version, bytes)] (#29) — the PRE-collapse Kotlin TypeNode, carrier-encoded (same
    // envelope as KotlinNullableGeneric). `collIdentity`/`collIdentityRet` was stashed as a canonical TypeNode JSON
    // STRING (opaque to the intervening type-rewriting passes); parse it back to a JsonNode so the carrier payload is
    // the structured node dll2klib's TypeNode.Parse reads to restore `List` vs `MutableList` at each nested position.
    static JsonObject CollIdentityAttr(string typeJson)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, JsonNode.Parse(typeJson));
        return Marker(AKCollIdentity, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    // [KotlinType(version, bytes)] — the complete Kotlin surface TypeNode corresponding to a compiler-synthesized CLR
    // type. The fact is kept opaque through CLR lowering and decoded only by dll2klib.
    static JsonObject KotlinTypeAttr(string typeJson)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, JsonNode.Parse(typeJson));
        return Marker(AKType, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    static JsonObject JsonCarrierAttr(string attr, JsonNode body)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, body.DeepClone());
        return Marker(attr, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    // DeclarationRename owns the source-to-physical decision and records the exact cross-module edge immediately on
    // that MethodDef. Keeping it in attrs lets every later structural pass copy it like other declaration metadata;
    // runtime builds remove it through the ordinary DotKt.Runtime.CompilerServices.* stripping rule.
    internal static void AddSourceMethodIdentity(JsonObject method, string sourceName) =>
        Append(method, JsonCarrierAttr(AKSourceMethod,
            new JsonObject { ["name"] = sourceName }));

    // The standard C# 14 Property graph carries receiver/name/accessor shape but cannot say that a private storage
    // field is Kotlin `const` or `lateinit`. Storage stays on the source file facade so every receiver observes the
    // original one-.cctor initialization order; this narrow edge names that physical owner/slot while dll2klib reads
    // the CLR Literal/[KotlinLateinit] facts from the field instead of duplicating them in another payload.
    internal static void StampPropertyStorage(JsonObject getter, string owner, string fieldName) =>
        Append(getter, JsonCarrierAttr(AKPropertyStorage, new JsonObject {
            ["owner"] = owner,
            ["field"] = fieldName,
        }));

    // A generic C# 14 extension implementation must carry the receiver block's CLR method parameters. Kotlin's
    // declaration does not: its associated classifier is bare and contributes no callable type parameters. This
    // trusted edge points from the standard Roslyn-shaped wrapper to the non-generic-container core that dll2klib and
    // Kotlin call sites use. Names and receiver identity continue to come exclusively from the standard graph.
    internal static void StampExtensionCore(JsonObject wrapper, string coreName) =>
        Append(wrapper, JsonCarrierAttr(AKExtensionCore, new JsonObject { ["name"] = coreName }));

    static JsonObject Marker(string attr, params JsonObject[] args)
    {
        var arr = new JsonArray();
        var argTypes = new JsonArray();
        foreach (var a in args)
        {
            arr.Add(a);
            argTypes.Add(a["bytes"] != null
                ? new JsonObject { ["t"] = "array", ["elem"] = Fqn("System.Byte") }
                : a["type"]?.DeepClone());
        }
        var marker = new JsonObject { ["attr"] = TypeJson.Fqn(attr), ["argTypes"] = argTypes, ["args"] = arr };
        if (attr is ANullable or ANullableCtx)
        {
            marker["attrExternal"] = true;
            // A compile-reference set can contain private compiler-synthesized Nullable* lookalikes in arbitrary
            // libraries. Carry the physical declaration owner so ilemit links the target BCL constructor rather than
            // re-resolving a name that is not globally unique.
            marker["attrAssembly"] = "System.Runtime";
        }
        return marker;   // `attr` is a structured `{t:fqn}` node (#48)
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

    /// Read and REMOVE an int slot fact (`ctxFnType`/`retCtxFnType`, recorded by BirTypeLowering before it folds a
    /// context function type to a physical delegate). Consumed here: it exists only to become the attribute.
    static int? TakeInt(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v || !v.TryGetValue<int>(out var n)) return null;
        o.Remove(key);
        return n;
    }

    static bool TakeBool(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v || !v.TryGetValue<bool>(out var value) || !value) return false;
        o.Remove(key);
        return true;
    }

    static bool ModFlag(JsonObject obj, string name) =>
        obj["mods"] is JsonObject m && (m[name] as JsonValue)?.GetValue<bool>() == true;

    // ---------------------------------------------------------------------------------------------------------------
    // The embedded attribute-class defs, emitted ONCE as a dedicated synthetic CIR file. Each is `internal sealed :
    // System.Attribute` with the same ctor overloads ilemit's DefineEmbeddedAttr{,N} used to synthesize. `final:true`
    // -> TypeAttributes.Sealed (matching the old NotPublic|Sealed|Class); `generated:true` makes ilemit stamp the
    // STANDARD [CompilerGenerated] trust marker. dll2klib accepts DotKt metadata only from carrier definitions bearing
    // that marker in an explicitly marked DotKt assembly, so a C# lookalike with the same full name is inert. Ctor params carry
    // NO name (a named ctor param would mint Param rows the embedded attrs never had); the empty body chains to
    // Attribute()'s protected ctor.
    // ---------------------------------------------------------------------------------------------------------------
    public static JsonObject SynthDefsFile(ReferenceMetadataIndex refs)
    {
        // Resolve the shared base delegation BEFORE any ctor is built, so every synthesized class states the
        // member it delegates to rather than describing it.
        _attributeBaseCtorRef = ClrMemberResolution.ParameterlessBaseCtorRef(refs, "System.Attribute");
        var types = new JsonArray
        {
            AttrClass(AKFunction, Ctor(Param("System.Int32"))),
            AttrClass(AKFileClass, Ctor()),
            AttrClass(AKInline, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKReadOnly, Ctor()),
            AttrClass(AKLateinit, Ctor()),
            AttrClass(AKFunInterface, Ctor()),
            AttrClass(AKSealed, Ctor()),
            AttrClass(AKValue, Ctor()),
            AttrClass(AKObject, Ctor()),
            AttrClass(AKInner, Ctor(Param("System.Int32"))), // source `inner` + leading physical outer slots
            AttrClass(AKRichEnum, Ctor(Param("System.String"), Param(ByteArrayType()))), // explicit rich-enum entry/API map
            AttrClass(AKCompanion, Ctor(Param("System.String"), Param(ByteArrayType()))), // #275 — source companion owner/name/representation
            AttrClass(AKCompanionExt, Ctor(Param("System.String"), Param(ByteArrayType()))), // #382 — a companion extension's associated Kotlin type
            AttrClass(AKPropertyAccessor, Ctor(Param("System.String"), Param(ByteArrayType()))), // method-generic Kotlin property accessor association
            AttrClass(AKSourceMethod, Ctor(Param("System.String"), Param(ByteArrayType()))), // renamed CLR method -> Kotlin source identity
            AttrClass(AKDeclarationIdentity, Ctor(Param("System.String"), Param(ByteArrayType()))), // #395 — frontend callable identity + source name
            AttrClass(AKConstructorAdapter, Ctor(Param("System.String"), Param(ByteArrayType()))), // alias ctor declaration -> terminal physical delegation
            AttrClass(AKPropertyStorage, Ctor(Param("System.String"), Param(ByteArrayType()))), // C# 14 property getter -> Kotlin-only storage facts
            AttrClass(AKExtensionCore, Ctor(Param("System.String"), Param(ByteArrayType()))), // generic C# wrapper -> Kotlin semantic core
            AttrClass(AKStaticCarrier, Ctor(Param("System.String"), Param(ByteArrayType()))), // one physical static surface for a generic Kotlin owner
            AttrClass(AKSuspendResult, Ctor(Param("System.String"), Param(ByteArrayType()))), // logical result of a physical Task suspend MethodDef
            AttrClass(AKSuspendFn, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKExtFn, Ctor()),     // #145 — bare marker: a `P.() -> R` receiver function-type position
            AttrClass(AKCtxParam, Ctor()),  // bare marker: a Kotlin `context(...)` parameter (physically positional)
            AttrClass(AKCtxFnType, Ctor(Param("System.Int32"))),  // how many of a function-type slot's leading args are contexts
            AttrClass(AKNothing, Ctor()),   // #133 case3 — bare marker on a Kotlin `Nothing` return
            AttrClass(AKNullableGen, Ctor(Param("System.String"), Param(ByteArrayType()))),  // #18/#147 — pre-erasure `Holder<T?>` declaration slot
            AttrClass(AKCollIdentity, Ctor(Param("System.String"), Param(ByteArrayType()))), // #29 — carrier of a pre-collapse `Box<List<T>>` collection identity
            AttrClass(AKType, Ctor(Param("System.String"), Param(ByteArrayType()))),         // compiler-synthesized CLR type -> original Kotlin TypeNode
            AttrClass(AKSupertypes, Ctor(Param("System.String"), Param(ByteArrayType()))),   // #86 — pre-erasure supertype edges + type-parameter bounds
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
            ["generated"] = true,                            // -> [CompilerGenerated] provenance marker
            ["base"] = Fqn("System.Attribute"),
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["methods"] = new JsonArray(),
            ["ctors"] = carr,
        };
    }

    // The resolved `System.Attribute()` delegation every synthesized attribute class shares. Resolved ONCE per
    // run: it is the same member for every one of them, and each ctor gets its own copy of the node.
    static JsonNode _attributeBaseCtorRef;

    static JsonObject Ctor(params JsonNode[] paramTypes)
    {
        var ps = new JsonArray();
        foreach (var t in paramTypes) ps.Add(t);
        return new JsonObject
        {
            ["vis"] = "public",
            ["params"] = ps,
            ["baseArgs"] = new JsonArray(),
            ["baseCtorRef"] = _attributeBaseCtorRef?.DeepClone(),
            ["body"] = new JsonArray(),
        };
    }

    // A ctor param with a bare CLR type and NO name (byte-equivalence: no Param table row).
    static JsonObject Param(string fqn) => new() { ["type"] = Fqn(fqn) };
    static JsonObject Param(JsonNode type) => new() { ["type"] = type };

    static JsonObject ByteArrayType() => new() { ["t"] = "array", ["elem"] = Fqn("System.Byte") };
}
