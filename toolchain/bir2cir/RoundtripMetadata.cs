using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Cir;

// ROUNDTRIP-METADATA GENERATION (#71 S2). bir2cir GENERATES every Kotlin round-trip metadata attribute — as ordinary
// CIR `attrs`/`retAttrs` entries {attr:fqn,args:[…]} + the embedded attribute-class DEFS — and ilemit only STAMPS them
// dumbly through its generic BuildCab/ConstArgValue path. This is the "ilemit knows NO Kotlin" endgame: the whole
// [Kotlin*]/[Nullable]/[NullableContext] generation block + EnsureKotlinAttrs + every Apply* was deleted from ilemit;
// the Kotlin-semantic DECISION (which modifier -> which attribute, the flags bit-pack, the scalar-vs-array Nullable
// collapse) now lives HERE, the Kotlin<->CLR layer.
//
// Runs in the ref (metadata) + app builds — the two that carry the round-trip surface — and is SKIPPED in the runtime
// build (`!SubstituteStdlibBuild`), except assembly-level alias declarations, which are encoded at the input
// boundary and survive in both stdlib twins. This gate REPLACES ilemit's deleted `_stripMetadata`. Reads the already-
// materialized facts (post-BirTypeLowering): mods(infix/operator/suspend/inline/fun/sealed), suspendBridge,
// retNullableFlags/nullableFlags (DeclNullableFlags), retSuspendFnType/suspendFnType (BirTypeLowering's suspend-fn-type
// erasure records the pre-erasure shape as this fact), readOnly, and the S1 `inlineBir` carrier string.
//
// The DotKt.Runtime.CompilerServices.* carrier defs are emitted ONCE per assembly as a DEDICATED synthetic CIR file
// (SynthDefsFile). The standard NullableAttribute/NullableContextAttribute types are target-BCL declarations and each
// application is marked external; duplicating those TypeDefs in every output would create a second CLR authority.
//
// Attribute ordering and fact consumption belong to CompleteMetadata in Callables.Source.cs and StampTypeHeaders
// in TypeHeaders.Source.cs. Their direct tests cover the distinct slot-prefix orders and return-vector replacement.
static partial class RoundtripMetadata
{
    const string Ns = "DotKt.Runtime.CompilerServices.";
    const string ClrNs = "System.Runtime.CompilerServices.";
    const string AKFunction     = Ns + "KotlinFunctionAttribute";
    const string SourceLowPriority = "kotlin.internal.LowPriorityInOverloadResolution";
    const string AKFileClass    = Ns + "KotlinFileClassAttribute";
    const string AKInline       = Ns + "KotlinInlineAttribute";
    const string AKReadOnly     = Ns + "KotlinReadOnlyAttribute";
    const string AKLateinit     = Ns + "KotlinLateinitAttribute";
    const string AKLateinitState = Ns + "KotlinLateinitStateAttribute";
    const string AKFunInterface = Ns + "KotlinFunInterfaceAttribute";
    const string AKSealed       = Ns + "KotlinSealedAttribute";
    const string AKValue        = Ns + "KotlinValueAttribute";
    const string AKObject       = Ns + "KotlinObjectAttribute";
    const string AKInner        = Ns + "KotlinInnerAttribute";
    const string AKRichEnum     = Ns + "KotlinRichEnumAttribute";
    const string AKBasicEnum    = Ns + "KotlinBasicEnumAttribute";
    const string AKBasicEnumOrdinal = Ns + "KotlinBasicEnumOrdinalAttribute";
    const string AKCompanion    = Ns + "KotlinCompanionAttribute";
    const string AKCompanionExt = Ns + "KotlinCompanionExtensionAttribute";
    const string AKPropertyAccessor = Ns + "KotlinPropertyAccessorAttribute";
    const string AKSourceMethod = Ns + "KotlinSourceMethodAttribute";
    const string AKInnerConstructorFactory = Ns + "KotlinInnerConstructorFactoryAttribute";
    internal const string AKManagedReferenceContract = Ns + "KotlinManagedReferenceContractAttribute";
    const string AKDeclarationIdentity = Ns + "KotlinDeclarationIdentityAttribute";
    const string AKConstructorAdapter = Ns + "KotlinConstructorAdapterAttribute";
    internal const string AKPropertyStorage = Ns + "KotlinPropertyStorageAttribute";
    internal const string AKExtensionCore = Ns + "KotlinExtensionCoreAttribute";
    const string AKStaticCarrier = Ns + "KotlinStaticCarrierAttribute";
    const string AKSuspendResult = Ns + "KotlinSuspendResultAttribute";
    const string AKSuspendFn    = Ns + "KotlinSuspendFunctionTypeAttribute";
    const string AKExtFn        = Ns + "KotlinExtensionFunctionTypeAttribute";
    const string AKCtxParam     = Ns + "KotlinContextParameterAttribute";
    const string AKExtReceiver  = Ns + "KotlinExtensionReceiverAttribute";
    const string AKCtxFnType    = Ns + "KotlinContextFunctionTypeAttribute";
    const string AKNothing      = Ns + "KotlinNothingAttribute";
    // The pre-erasure declaration-slot carrier. `internal` because it has a second reader in this assembly:
    // ForeignNullableGenericCrossing decides which slot a body fills by reading the record back off the minted
    // attribute, and matching it by an exactly-named FQN is what keeps that read off any other attribute.
    internal const string AKNullableGen  = Ns + "KotlinNullableGenericAttribute";
    const string AKCollIdentity = Ns + "KotlinCollectionIdentityAttribute";
    const string AKType         = Ns + "KotlinTypeAttribute";
    const string AKTypeVisibility = Ns + "KotlinTypeVisibilityAttribute";
    const string AKTypeAlias    = Ns + "KotlinTypeAliasAttribute";
    const string AKSupertypes   = Ns + "KotlinSupertypesAttribute";
    const string AKTypeParameterBounds = Ns + "KotlinTypeParameterBoundsAttribute";
    const string ANullable      = ClrNs + "NullableAttribute";
    const string ANullableCtx   = ClrNs + "NullableContextAttribute";

    // Physical accessibility may be wider than Kotlin declaration visibility for the cross-module inline ABI.
    // Preserve the declaration-owned fact at that representation boundary, before replacing CIR's `vis`.
    public static void PreserveTypeVisibility(JsonObject type) =>
        Append(type, Marker(AKTypeVisibility, StringArg(BirCarrier.JsonV1),
            BytesArg(Convert.ToBase64String(VisibilityContent(type["vis"]!.GetValue<string>())))));

    public static void PreserveCallableVisibility(JsonObject declaration, string visibility) =>
        Append(declaration, Marker(CallableVisibilityLowering.Carrier, StringArg(BirCarrier.JsonV1),
            BytesArg(Convert.ToBase64String(VisibilityContent(visibility)))));

    // ---------------------------------------------------------------------------------------------------------------
    // Validate logical suspend-result facts before physical representation discards their source modifier.
    // ---------------------------------------------------------------------------------------------------------------
    public static void RequireSuspendResults(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots) RequireSuspendResults(root);
    }

    static void RequireSuspendResults(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                if (ModFlag(method, "suspend") &&
                    (method["suspendResult"] as JsonValue)?.TryGetValue<string>(out _) != true)
                    throw new InvalidOperationException(
                        $"suspend declaration '{method["name"]}' did not preserve its logical result");
        if (obj["types"] is JsonArray types)
            foreach (var type in types) RequireSuspendResults(type);
    }

    static bool HasAttr(JsonObject declaration, string attributeName) =>
        declaration["attrs"] is JsonArray attrs && attrs.OfType<JsonObject>().Any(attribute =>
            TypeJson.OwnerName(attribute["attr"]) == attributeName);

    // Copy this source resolution fact independently of unrelated declaration attributes.
    public static void CopySourceOverloadPriority(JsonObject source, JsonObject target)
    {
        if (source["attrs"] is not JsonArray attributes) return;
        foreach (var attribute in attributes.OfType<JsonObject>())
            if (TypeJson.OwnerName(attribute["attr"]) == SourceLowPriority)
                Append(target, (JsonObject)attribute.DeepClone());
    }

    // A compile-time-only carrier (round-trip surface / @Clr* binding / NRT) — dropped from the shipping runtime dll.
    // Everything else survives: the user's own annotations (kotlin.Deprecated/SinceKotlin/PublishedApi/Volatile), and —
    // matching the app/ref builds, which keep them too (kotc serializes every annotation verbatim, no retention filter)
    // — the compiler-internal resolution hints (kotlin.Suppress, kotlin.internal.*). Those hints are inert on the rt.dll
    // (not projected as Kotlin source), so keeping them keeps rt consistent with app/ref; a retention-aware filter
    // (SOURCE-retention annotations should ship on NO binary) is a broader downstream policy, deliberately NOT folded here.
    static bool IsRuntimeStrippable(string fqn) =>
        fqn.StartsWith(Ns, StringComparison.Ordinal)          // DotKt.Runtime.CompilerServices.* round-trip carriers
        || fqn.StartsWith("kotlin.clr.", StringComparison.Ordinal)  // @Clr* binding surface (ref-side only)
        || fqn == ANullable || fqn == ANullableCtx;           // NRT [Nullable]/[NullableContext]

    // ---------------------------------------------------------------------------------------------------------------
    // ATTRIBUTE-INSTANCE builders (a CIR `attrs` entry {attr, args:[…]} routed through ilemit's generic BuildCab).
    // ---------------------------------------------------------------------------------------------------------------

    static JsonObject JsonCarrierAttr(string attr, JsonNode body)
    {
        byte[] content = BirCarrier.EncodeBody(BirCarrier.JsonV1, body.DeepClone());
        return Marker(attr, StringArg(BirCarrier.JsonV1), BytesArg(Convert.ToBase64String(content)));
    }

    // DeclarationRename owns the source-to-physical decision and records the exact cross-module edge immediately on
    // that MethodDef. Keeping it in attrs lets every later structural pass copy it like other declaration metadata;
    // runtime builds remove it through the ordinary DotKt.Runtime.CompilerServices.* stripping rule.
    internal static void AddSourceMethodIdentity(JsonObject method, string sourceName, string sourceDeclaration = null)
    {
        var identity = new JsonObject { ["name"] = sourceName };
        if (sourceDeclaration != null) identity["sourceDeclaration"] = sourceDeclaration;
        Append(method, JsonCarrierAttr(AKSourceMethod, identity));
    }

    internal static void CopySourceMethodIdentity(JsonObject source, JsonObject target)
    {
        if (HasAttr(target, AKSourceMethod) || source["attrs"] is not JsonArray attributes) return;
        foreach (var attribute in attributes.OfType<JsonObject>())
            if (TypeJson.OwnerName(attribute["attr"]) == AKSourceMethod)
                Append(target, (JsonObject)attribute.DeepClone());
    }

    // The standard C# 14 Property graph carries receiver/name/accessor shape but cannot say that a private storage
    // field is Kotlin `const` or `lateinit`. Storage stays on the source file facade so every receiver observes the
    // original one-.cctor initialization order; this narrow edge names that physical owner/slot while dll2klib reads
    // the CLR Literal/[KotlinLateinit] facts from the field instead of duplicating them in another payload.
    internal static void StampPropertyStorage(JsonObject getter, string owner, string fieldName) =>
        Append(getter, Marker(AKPropertyStorage, StringArg(BirCarrier.JsonV1),
            BytesArg(Convert.ToBase64String(PropertyStorageContent(owner, fieldName)))));

    // Lexically legal producer defaults can transport an initialization query into another assembly. Bind the
    // selected Kotlin getter to its private state accessor explicitly; the consumer never reconstructs storage.
    internal static void StampLateinitState(JsonObject getter, string owner, string method, bool isStatic) =>
        Append(getter, Marker(AKLateinitState, StringArg(BirCarrier.JsonV1),
            BytesArg(Convert.ToBase64String(LateinitStateContent(owner, method, isStatic)))));

    // A generic C# 14 extension implementation must carry the receiver block's CLR method parameters. Kotlin's
    // declaration does not: its associated classifier is bare and contributes no callable type parameters. This
    // trusted edge points from the standard Roslyn-shaped wrapper to the non-generic-container core that dll2klib and
    // Kotlin call sites use. Names and receiver identity continue to come exclusively from the standard graph.
    internal static void StampExtensionCore(JsonObject wrapper, string coreName) =>
        Append(wrapper, Marker(AKExtensionCore, StringArg(BirCarrier.JsonV1),
            BytesArg(Convert.ToBase64String(ExtensionCoreContent(coreName)))));

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

    static bool ModFlag(JsonObject obj, string name) =>
        obj["mods"] is JsonObject m && (m[name] as JsonValue)?.GetValue<bool>() == true;

    // ---------------------------------------------------------------------------------------------------------------
    // The embedded attribute-class defs, emitted ONCE as a dedicated synthetic CIR file. Each is `internal sealed :
    // System.Attribute` with the constructor signatures defined by the compiler-metadata carrier ABI. `final:true`
    // maps to TypeAttributes.Sealed; `generated:true` makes ilemit stamp the
    // STANDARD [CompilerGenerated] trust marker. dll2klib accepts DotKt metadata only from carrier definitions bearing
    // that marker in an explicitly marked DotKt assembly, so a C# lookalike with the same full name is inert. Ctor params carry
    // NO name (a named ctor param would mint Param rows the embedded attrs never had); the empty body chains to
    // Attribute()'s protected ctor.
    // ---------------------------------------------------------------------------------------------------------------
    public static CirFileDeclaration SynthDefsFile(ReferenceMetadataIndex refs, bool runtimeOnly = false)
    {
        // Resolve the shared base delegation BEFORE any ctor is built, so every synthesized class states the
        // member it delegates to rather than describing it.
        var baseConstructor = ClrMemberResolution.ParameterlessBaseCtorRef(refs, "System.Attribute");
        CirConstructor Ctor(params CirParameter[] parameters) => new() {
            Vis = "public", Params = parameters, BaseArgs = Array.Empty<ICirExpression>(),
            BaseCtorRef = baseConstructor, Body = [],
        };
        var types = new CirTypeDeclaration[]
        {
            TypeAliasAttributeDefinition(refs, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKFunction, Ctor(Param("System.Int32"))),
            AttrClass(AKFileClass, Ctor()),
            AttrClass(AKInline, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(DefaultBirCarrier.AttributeName, Ctor(Param("System.Int32"), Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKReadOnly, Ctor()),
            AttrClass(AKLateinit, Ctor()),
            AttrClass(AKLateinitState, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKFunInterface, Ctor()),
            AttrClass(AKSealed, Ctor()),
            AttrClass(AKValue, Ctor()),
            AttrClass(AKObject, Ctor()),
            AttrClass(AKInner, Ctor(Param("System.Int32"))), // source `inner` + leading physical outer slots
            AttrClass(AKRichEnum, Ctor(Param("System.String"), Param(ByteArrayType()))), // explicit rich-enum entry/API map
            AttrClass(AKBasicEnum, Ctor(Param("System.String"), Param(ByteArrayType()))), // basic-enum ordered values
            AttrClass(AKBasicEnumOrdinal, Ctor(Param("System.Int32"))), // declaration ordinal on each Kotlin basic-enum literal
            AttrClass(AKCompanion, Ctor(Param("System.String"), Param(ByteArrayType()))), // #275 — source companion owner/name/representation
            AttrClass(AKCompanionExt, Ctor(Param("System.String"), Param(ByteArrayType()))), // #382 — a companion extension's associated Kotlin type
            AttrClass(AKPropertyAccessor, Ctor(Param("System.String"), Param(ByteArrayType()))), // method-generic Kotlin property accessor association
            AttrClass(AKSourceMethod, Ctor(Param("System.String"), Param(ByteArrayType()))), // renamed CLR method -> Kotlin source identity
            AttrClass(AKInnerConstructorFactory, Ctor(Param("System.String"), Param(ByteArrayType()))), // existential outer -> exact inner constructor
            AttrClass(AKManagedReferenceContract, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKDeclarationIdentity, Ctor(Param("System.String"), Param(ByteArrayType()))), // #395 — frontend callable identity + source name
            AttrClass(AKConstructorAdapter, Ctor(Param("System.String"), Param(ByteArrayType()))), // alias ctor declaration -> terminal physical delegation
            AttrClass(AKPropertyStorage, Ctor(Param("System.String"), Param(ByteArrayType()))), // C# 14 property getter -> Kotlin-only storage facts
            AttrClass(AKExtensionCore, Ctor(Param("System.String"), Param(ByteArrayType()))), // generic C# wrapper -> Kotlin semantic core
            AttrClass(AKStaticCarrier, Ctor(Param("System.String"), Param(ByteArrayType()))), // one physical static surface for a generic Kotlin owner
            AttrClass(AKSuspendResult, Ctor(Param("System.String"), Param(ByteArrayType()))), // logical result of a physical Task suspend MethodDef
            AttrClass(AKSuspendFn, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKExtFn, Ctor()),     // #145 — bare marker: a `P.() -> R` receiver function-type position
            AttrClass(AKCtxParam, Ctor()),  // bare marker: a Kotlin `context(...)` parameter (physically positional)
            AttrClass(AKExtReceiver, Ctor()), // bare marker: a Kotlin extension receiver (physically positional)
            AttrClass(AKCtxFnType, Ctor(Param("System.Int32"))),  // how many of a function-type slot's leading args are contexts
            AttrClass(AKNothing, Ctor()),   // #133 case3 — bare marker on a Kotlin `Nothing` return
            AttrClass(AKNullableGen, Ctor(Param("System.String"), Param(ByteArrayType()))),  // #18/#147 — pre-erasure `Holder<T?>` declaration slot
            AttrClass(AKCollIdentity, Ctor(Param("System.String"), Param(ByteArrayType()))), // #29 — carrier of a pre-collapse `Box<List<T>>` collection identity
            AttrClass(AKType, Ctor(Param("System.String"), Param(ByteArrayType()))),         // compiler-synthesized CLR type -> original Kotlin TypeNode
            AttrClass(AKTypeVisibility, Ctor(Param("System.String"), Param(ByteArrayType()))), // source visibility before physical accessibility promotion
            AttrClass(CallableVisibilityLowering.Carrier, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKSupertypes, Ctor(Param("System.String"), Param(ByteArrayType()))),   // #86 — pre-erasure supertype edges + type-parameter bounds
            AttrClass(AKTypeParameterBounds, Ctor(Param("System.String"), Param(ByteArrayType()))), // pre-erasure MethodDef type-parameter bounds
        };
        if (runtimeOnly) types = new[] { TypeAliasAttributeDefinition(refs, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKType, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKDeclarationIdentity, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKRichEnum, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKBasicEnum, Ctor(Param("System.String"), Param(ByteArrayType()))),
            AttrClass(AKBasicEnumOrdinal, Ctor(Param("System.Int32"))) };
        return new CirFileDeclaration
        {
            FileClass = "", HasMain = false, Methods = [], Fields = [], Types = types,
        };
    }

    static CirTypeDeclaration TypeAliasAttributeDefinition(ReferenceMetadataIndex refs, CirConstructor constructor)
    {
        var usage = new AttributeApplication(new TypeNode.Fqn("System.AttributeUsageAttribute"),
            [new TypeNode.Fqn("System.AttributeTargets")], [new AttributeArgument.Int32Value(1)],
            AttrExternal: true, AttrAssembly: "System.Runtime", NamedArgs: [
                new NamedAttributeArgument("property", "AllowMultiple", new TypeNode.Fqn("System.Boolean"), new AttributeArgument.BooleanValue(true)),
                new NamedAttributeArgument("property", "Inherited", new TypeNode.Fqn("System.Boolean"), new AttributeArgument.BooleanValue(false)),
            ]);
        return AttrClass(AKTypeAlias, constructor) with {
            Attrs = new[] { ClrMemberResolution.ResolveAttributeCtor(usage, refs) },
        };
    }

    static CirTypeDeclaration AttrClass(string fqn, params CirConstructor[] ctors) => new() {
        Name = fqn, Kind = CirTypeKind.Class, Vis = "internal", Final = true, Generated = true,
        Base = new TypeNode.Fqn("System.Attribute"), Interfaces = Array.Empty<TypeNode>(),
        Fields = Array.Empty<CirField>(), Methods = Array.Empty<CirMethod>(), Ctors = ctors,
    };

    // A ctor param with a bare CLR type and NO name; omitting the name means no Param metadata row is required.
    static CirParameter Param(string fqn) => new() { Type = new TypeNode.Fqn(fqn) };
    static CirParameter Param(TypeNode type) => new() { Type = type };

    static TypeNode ByteArrayType() => new TypeNode.Array(new TypeNode.Fqn("System.Byte"));
}
