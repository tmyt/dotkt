using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Reads fixed reference metadata into scan-owned facts; no query index or source graph is available.
static class ReferenceMetadataReader
{
    const string KotlinLateinitStateAttr = "DotKt.Runtime.CompilerServices.KotlinLateinitStateAttribute";

    static LateinitStateTarget ReadLateinitState(MethodInfo getter)
    {
        var payload = CarrierJsonOf(getter.GetCustomAttributesData(), getter.DeclaringType?.Assembly,
            KotlinLateinitStateAttr) as JsonObject;
        if (payload == null) return null;
        if (payload.Count != 3 || payload["owner"] is not JsonValue ownerValue
            || !ownerValue.TryGetValue<string>(out var owner) || string.IsNullOrEmpty(owner)
            || payload["method"] is not JsonValue methodValue
            || !methodValue.TryGetValue<string>(out var method) || string.IsNullOrEmpty(method)
            || payload["static"] is not JsonValue staticValue || !staticValue.TryGetValue<bool>(out var isStatic))
            throw new InvalidDataException("malformed [KotlinLateinitState] payload");
        return new(owner, method, isStatic);
    }

    public enum TypeKeyKind
    {
        Named,
        Reference,
        GenericParameter,
        Function,
        Object,
        String,
        Void,
        Int8,
        Int16,
        Int32,
        Int64,
        Float32,
        Float64,
        Boolean,
        Char,
        UInt8,
        UInt16,
        UInt32,
        UInt64,
        ByRef,
        Array,
        Nullable,
    }

    public sealed record TypeKey(TypeKeyKind Kind, string Name = null, TypeKey Element = null);

    public sealed class SignatureKey : IEquatable<SignatureKey>
    {
        readonly TypeKey[] _parameters;

        public SignatureKey(IEnumerable<TypeKey> parameters) => _parameters = parameters.ToArray();

        public bool Equals(SignatureKey other) =>
            other != null && _parameters.SequenceEqual(other._parameters);

        public override bool Equals(object obj) => Equals(obj as SignatureKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var parameter in _parameters) hash.Add(parameter);
            return hash.ToHashCode();
        }
    }

    public readonly record struct DefaultKey(
        string Owner, string Method, int ParamCount, SignatureKey Signature = null, bool Relaxed = false);

    public sealed record RichEnumMetadata(
        IReadOnlyDictionary<string, string> EntryFields,
        string Name,
        string Ordinal,
        string Values,
        string ValueOf);

    sealed class MalformedTrustedCompanionException : Exception
    {
        public MalformedTrustedCompanionException(string message, Exception inner = null) : base(message, inner) { }
    }

    sealed class MalformedTrustedStaticCarrierException : Exception
    {
        public MalformedTrustedStaticCarrierException(string message, Exception inner = null) : base(message, inner) { }
    }

    const string KotlinFileClassAttr = "DotKt.Runtime.CompilerServices.KotlinFileClassAttribute";
    const string KotlinFunctionAttr = "DotKt.Runtime.CompilerServices.KotlinFunctionAttribute";
    const string KotlinInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute";
    const string KotlinTypeAttr = "DotKt.Runtime.CompilerServices.KotlinTypeAttribute";
    const string KotlinSupertypesAttr = "DotKt.Runtime.CompilerServices.KotlinSupertypesAttribute";
    const string KotlinSuspendResultAttr = "DotKt.Runtime.CompilerServices.KotlinSuspendResultAttribute";
    const string KotlinSuspendFunctionTypeAttr = "DotKt.Runtime.CompilerServices.KotlinSuspendFunctionTypeAttribute";
    const string KotlinCompanionAttr = "DotKt.Runtime.CompilerServices.KotlinCompanionAttribute";
    const string KotlinCompanionExtensionAttr = "DotKt.Runtime.CompilerServices.KotlinCompanionExtensionAttribute";
    const string KotlinPropertyAccessorAttr = "DotKt.Runtime.CompilerServices.KotlinPropertyAccessorAttribute";
    const string KotlinSourceMethodAttr = "DotKt.Runtime.CompilerServices.KotlinSourceMethodAttribute";
    const string KotlinInnerConstructorFactoryAttr = "DotKt.Runtime.CompilerServices.KotlinInnerConstructorFactoryAttribute";
    const string KotlinDeclarationIdentityAttr = "DotKt.Runtime.CompilerServices.KotlinDeclarationIdentityAttribute";
    const string KotlinConstructorAdapterAttr = "DotKt.Runtime.CompilerServices.KotlinConstructorAdapterAttribute";
    const string KotlinExtensionCoreAttr = "DotKt.Runtime.CompilerServices.KotlinExtensionCoreAttribute";
    const string KotlinStaticCarrierAttr = "DotKt.Runtime.CompilerServices.KotlinStaticCarrierAttribute";
    const string KotlinRichEnumAttr = "DotKt.Runtime.CompilerServices.KotlinRichEnumAttribute";
    const string KotlinBasicEnumAttr = "DotKt.Runtime.CompilerServices.KotlinBasicEnumAttribute";
    const string KotlinInnerAttr = "DotKt.Runtime.CompilerServices.KotlinInnerAttribute";
    // The #86/#147 positional carrier: the PRE-erasure Kotlin TypeNode of a declaration slot whose `Nullable(Tv)`
    // NullableGenericErasure object-erased. Read per member slot (return parameter and each value parameter) so a
    // CONSUMING module can re-derive `Subst(Erase(decl))` at a use of that slot instead of guessing from the call.
    const string KotlinNullableGenericAttr = "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute";
    const string DotKtAssemblyMarkerAttr = "System.Reflection.AssemblyMetadataAttribute";
    internal const string DotKtAssemblyMarkerKey = "DotKt.Compiler";
    internal const string DotKtAssemblyMarkerValue = "metadata-v1";
    const string CompilerGeneratedAttr = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
    // The round-trip marker RoundtripMetadata stamps on every `value`/inline class. It REPLACES the old
    // `kotlin.jvm.JvmInline` key: the 2.4.0 frontend no longer materializes @JvmInline into the IR (OptionalExpectation
    // `expect` with no non-JVM actual), so value-ness now rides `mods.value` -> this synthetic attribute on the ref/rt DLL.
    const string KotlinValueAttr = "DotKt.Runtime.CompilerServices.KotlinValueAttribute";
    const string RestrictsSuspensionAttr = "kotlin.coroutines.RestrictsSuspension";
    // [KotlinFunction(flags)] flag word (mirrors ilemit Program.cs pass 4 / dll2klib): Infix=1, Operator=2, Suspend=4.
    const int KotlinFunctionSuspendFlag = 4;

    /// The `method` component of a CONSTRUCTOR's @KotlinDefault key (#235). `.ctor` is the CLR's own constructor name and
    /// is unspeakable in Kotlin, so it can never collide with a real method a `new`'s owner declares.
    internal const string CtorKeyName = ".ctor";

    // ---- Call-substitution lookups (consumed by MemberCallSubstitution) ----

    // The open identity of a current structured Fqn's name. Generic arguments live in Fqn.Args; the name may carry
    // CLR metadata arity punctuation, which is the only decoration normalized here.
    public static string BareOwnerFqn(string fqnName) => StripGenericArity(fqnName.Trim());

    internal static TypeNode.Fqn ExactDeclaringView(Type type)
    {
        if (HierarchyView(type) is not TypeNode.Fqn view) return null;
        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        return new TypeNode.Fqn(ExactPhysicalMetadataName(definition), view.Args);
    }

    // Total arity disambiguates a top-level hierarchy edge, not a nested one:
    // Outer<T>.Face<U> and Outer.Face<T,U> have the same flattened signature.
    static TypeNode.Fqn HierarchyView(Type type) =>
        DeclarationTypeNode(type, nominalDelegates: false, preserveNestedIdentity: true) as TypeNode.Fqn;

    internal static string CompanionExtensionKey(string owner, string receiverJson, string kind, string sourceName)
    {
        if (owner == null || receiverJson == null || kind == null || sourceName == null) return "";
        TypeNode receiverType = TypeJson.Read(JsonNode.Parse(receiverJson));
        while (receiverType is TypeNode.Oblivious oblivious) receiverType = oblivious.Of;
        while (receiverType is TypeNode.Nullable nullable) receiverType = nullable.Of;
        var classifier = (receiverType is TypeNode.Fqn fqn ? fqn.Name : null)
            ?? throw new InvalidDataException("companion-extension receiver payload is not a classifier");
        // The source language association is the bare classifier. dll2klib can legitimately rehydrate a generic
        // receiver as C<Any>; its arguments are not declaration identity and must not enter this trusted key.
        var receiver = TypeJson.Fqn(BareOwnerFqn(DottedFqn(classifier))).ToJsonString();
        return StripGenericArity(DottedFqn(owner)) + "\u001f" + receiver + "\u001f" + kind + "\u001f" + sourceName;
    }

    static byte[] ReferenceParameterNullableFlags(ParameterInfo parameter, TypeNode physical)
    {
        if (physical is not (TypeNode.Fqn or TypeNode.Array or TypeNode.Fn))
            return null;
        var attribute = parameter.GetCustomAttributesData().FirstOrDefault(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is byte flag) return new[] { flag };
        return attribute?.ConstructorArguments.FirstOrDefault().Value is IEnumerable<CustomAttributeTypedArgument> flags
            ? flags.Select(value => Convert.ToByte(value.Value)).ToArray() : null;
    }

    // Canonicalize one already-parsed Fqn identity. No string type grammar is recognized here: wrappers and generic
    // arguments are represented by TypeNode/System.Type and dispatched structurally before this leaf is reached.
    internal static TypeKey ParamKeyFqn(string t, bool relaxed = false)
    {
        var exact = t switch
        {
            "kotlin.Byte" or "System.SByte" or "sbyte" => new TypeKey(TypeKeyKind.Int8),
            "kotlin.Short" or "System.Int16" or "short" => new TypeKey(TypeKeyKind.Int16),
            "kotlin.Int" or "System.Int32" or "int" => new TypeKey(TypeKeyKind.Int32),
            "kotlin.Long" or "System.Int64" or "long" => new TypeKey(TypeKeyKind.Int64),
            "kotlin.Float" or "System.Single" or "float" => new TypeKey(TypeKeyKind.Float32),
            "kotlin.Double" or "System.Double" or "double" => new TypeKey(TypeKeyKind.Float64),
            "kotlin.Boolean" or "System.Boolean" or "bool" => new TypeKey(TypeKeyKind.Boolean),
            "kotlin.Char" or "System.Char" or "char" => new TypeKey(TypeKeyKind.Char),
            "kotlin.String" or "System.String" or "string" => new TypeKey(TypeKeyKind.String),
            "kotlin.CharSequence" or SharedSyntheticSynthesis.CharSeq =>
                new TypeKey(TypeKeyKind.Named, SharedSyntheticSynthesis.CharSeq),
            "System.Void" or "void" => new TypeKey(TypeKeyKind.Void),
            "kotlin.Any" or "System.Object" or "object" => new TypeKey(TypeKeyKind.Object),
            // Unsigned scalars, folded like every other primitive: the specialized ARRAYS were already folded below, but
            // the element types were not, so a `UInt` parameter keyed as `kotlin.UInt` from a pre-lowering call site and
            // as `uint` from a reference assembly — two spellings of one type that no signature compare could match.
            "kotlin.UByte" or "System.Byte" or "byte" => new TypeKey(TypeKeyKind.UInt8),
            "kotlin.UShort" or "System.UInt16" or "ushort" => new TypeKey(TypeKeyKind.UInt16),
            "kotlin.UInt" or "System.UInt32" or "uint" => new TypeKey(TypeKeyKind.UInt32),
            "kotlin.ULong" or "System.UInt64" or "ulong" => new TypeKey(TypeKeyKind.UInt64),
            // Primitive-array class spellings (kotc lowers to a structured array node, but the ref.dll may reflect the kotlin.IntArray
            // class) -> the same array key so a top-level `sort(IntArray)`@ClrIntrinsic matches by signature.
            "kotlin.IntArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int32)),
            "kotlin.LongArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int64)),
            "kotlin.ByteArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int8)),
            "kotlin.ShortArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int16)),
            "kotlin.FloatArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Float32)),
            "kotlin.DoubleArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Float64)),
            "kotlin.BooleanArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Boolean)),
            "kotlin.CharArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Char)),
            // Unsigned specialized arrays (#53): native System.Byte[]/UInt16[]/UInt32[]/UInt64[]. Same array key as
            // their element token so an @ClrIntrinsic signature over the ref.dll spelling matches.
            "kotlin.UByteArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt8)),
            "kotlin.UShortArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt16)),
            "kotlin.UIntArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt32)),
            "kotlin.ULongArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt64)),
            _ => new TypeKey(TypeKeyKind.Named, StripGenericArity(t)),
        };
        return relaxed && exact.Kind == TypeKeyKind.Named ? new TypeKey(TypeKeyKind.Reference) : exact;
    }

    internal static TypeKey ParamKey(TypeNode t, bool relaxed) => t switch
    {
        TypeNode.Oblivious o => ParamKey(o.Of, relaxed),
        TypeNode.ByRef b => new TypeKey(TypeKeyKind.ByRef, Element: ParamKey(b.Of, relaxed)),
        TypeNode.Array a => new TypeKey(TypeKeyKind.Array, Element: ParamKey(a.Elem, relaxed)),
        TypeNode.Nullable n when relaxed && !IsValueKey(ParamKey(n.Of, false)) => ParamKey(n.Of, true),
        TypeNode.Nullable n => new TypeKey(TypeKeyKind.Nullable, Element: ParamKey(n.Of, relaxed)),
        TypeNode.Fn fn => new TypeKey(fn.Suspend ? TypeKeyKind.Object : TypeKeyKind.Function),
        TypeNode.Tv => new TypeKey(TypeKeyKind.GenericParameter),
        TypeNode.Fqn f => ParamKeyFqn(f.Name, relaxed),
        _ => new TypeKey(TypeKeyKind.Object),
    };

    internal static TypeKey ParamKey(Type type, bool relaxed)
    {
        if (type.IsByRef) return new TypeKey(TypeKeyKind.ByRef, Element: ParamKey(type.GetElementType()!, relaxed));
        if (type.IsArray) return new TypeKey(TypeKeyKind.Array, Element: ParamKey(type.GetElementType()!, relaxed));
        if (type.IsGenericParameter) return new TypeKey(TypeKeyKind.GenericParameter);
        if (IsDelegate(type)) return new TypeKey(TypeKeyKind.Function);
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (IsNullableDefinition(def))
            {
                var inner = type.GetGenericArguments()[0];
                if (relaxed && !IsValueKey(ParamKey(inner, false))) return ParamKey(inner, true);
                return new TypeKey(TypeKeyKind.Nullable, Element: ParamKey(inner, relaxed));
            }
            return ParamKeyFqn(StripGenericArity(def.FullName ?? def.Name), relaxed);
        }
        return ParamKeyFqn(PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name), relaxed);
    }

    public static TypeKey ParamKey(TypeNode t) => ParamKey(t, relaxed: false);

    // ParamKey off a structured JSON type slot.
    public static TypeKey ParamKey(JsonNode typeSlot) =>
        TypeJson.Read(typeSlot) is { } type ? ParamKey(type) : null;

    public static TypeKey ReceiverParamKey(JsonNode typeSlot)
        => ReceiverParamKey(TypeNode.Parse(typeSlot.ToJsonString()));

    public static TypeKey ReceiverParamKey(TypeNode type)
    {
        if (type is TypeNode.Nullable nullable) type = nullable.Of;
        return ParamKey(type, relaxed: false);
    }

    internal static TypeKey ReceiverParamKey(Type type)
    {
        if (type.IsConstructedGenericType && IsNullableDefinition(type.GetGenericTypeDefinition()))
            type = type.GetGenericArguments()[0];
        return ParamKey(type, relaxed: false);
    }

    // Populate the substitution index (Aliases / TypeKinds / HelperTypes / MemberBindings) from the ref.dll using a
    // MetadataLoadContext so the metadata-only assembly reads cleanly. Per-type try/catch: one malformed type is
    // skipped, never aborting the whole scan (the failure mode that left Assembly.LoadFrom's index empty).
    internal static ReferenceDotKtMetadata Read(string reference, MetadataLoadContext mlc,
        Func<MethodBase, NativeSignatureReader.Signature> methodSignature, Func<ParameterInfo, TypeNode> signatureSlot)
    {
        var metadata = new ReferenceDotKtMetadata();
        try
        {
            ValidateCompanionMetadata(reference);
            IndexMethodImplMetadata(reference, metadata);
            var asm = mlc.LoadFromAssemblyPath(Path.GetFullPath(reference));

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            // DotKt ownership is provenance, not a namespace/type-name guess. ilemit stamps the versioned assembly
            // marker and bir2cir emits a [CompilerGenerated] metadata carrier; require BOTH, matching dll2klib's
            // classifier. A third-party C# assembly may legally define a full-name lookalike and must remain on the
            // ordinary CLR metadata path (including custom awaitables and BCL collection signatures).
            var dotKtAuthored = IsDotKtEmittedAssembly(asm);
            if (dotKtAuthored)
                foreach (var authoredType in types)
                    metadata.DotKtOwners.Add(
                        StripGenericArity(DottedFqn(authoredType.FullName ?? authoredType.Name)));

            var singletonCompanionCarriers = new Dictionary<string, string>(StringComparer.Ordinal);
            var companionSourcePaths = new Dictionary<Type, string>();
            var companionRepresentations = dotKtAuthored
                ? ValidateCompanionCarriers(types, asm, out singletonCompanionCarriers,
                    metadata.CompanionCarrierByPhysicalOwner,
                    metadata.CompanionSourceNameByPhysicalOwner,
                    metadata.CompanionPhysicalOwnerBySemanticType,
                    metadata.CompanionSemanticOwnerByCarrier, companionSourcePaths)
                : new Dictionary<Type, bool>();
            foreach (var companion in singletonCompanionCarriers)
                metadata.SingletonCompanionCarrierBySemanticOwner.Add(companion.Key, companion.Value);

            var semanticOwnerByStaticCarrier = new Dictionary<string, string>(StringComparer.Ordinal);
            if (dotKtAuthored)
                foreach (var carrierType in types)
                    if (TrustedStaticCarrierPayload(carrierType, asm) is JsonObject payload)
                        semanticOwnerByStaticCarrier.Add(StripGenericArity(DottedFqn(carrierType.FullName ?? carrierType.Name)),
                            StripGenericArity(DottedFqn(payload["owner"].GetValue<string>())));

            string StaticNestedSourceName(Type type)
            {
                if (companionSourcePaths.TryGetValue(type, out var companionPath)) return companionPath;
                var physical = StripGenericArity(DottedFqn(type.FullName ?? type.Name));
                if (semanticOwnerByStaticCarrier.TryGetValue(physical, out var semantic)) return semantic;
                return type.DeclaringType == null ? physical
                    : StaticNestedSourceName(type.DeclaringType) + "." + StripGenericArity(type.Name);
            }

            if (dotKtAuthored)
            {
                var bySemanticName = types.Where(type => !semanticOwnerByStaticCarrier.ContainsKey(
                        StripGenericArity(DottedFqn(type.FullName ?? type.Name))))
                    .GroupBy(StaticNestedSourceName, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                foreach (var carrierType in types)
                {
                    var payload = TrustedStaticCarrierPayload(carrierType, asm);
                    if (payload == null) continue;
                    var owner = payload["owner"].GetValue<string>();
                    var physical = StripGenericArity(DottedFqn(carrierType.FullName ?? carrierType.Name));
                    if (!carrierType.IsPublic)
                        throw new MalformedTrustedStaticCarrierException(
                            $"trusted [KotlinStaticCarrier] '{carrierType.FullName}' must be public");
                    if (string.IsNullOrEmpty(owner) || carrierType.IsGenericType ||
                        !HasAttribute(carrierType.GetCustomAttributesData(), CompilerGeneratedAttr) ||
                        !bySemanticName.TryGetValue(StripGenericArity(DottedFqn(owner)), out var semanticTypes) ||
                        semanticTypes.Length != 1 || !semanticTypes[0].IsGenericTypeDefinition)
                        throw new MalformedTrustedStaticCarrierException(
                            $"malformed trusted [KotlinStaticCarrier] on '{carrierType.FullName}'");
                    ValidateStaticCarrierMembers(carrierType);
                    var semanticOwner = StripGenericArity(DottedFqn(owner));
                    if (!metadata.GenericStaticCarrierBySemanticOwner.TryAdd(semanticOwner, physical))
                        throw new MalformedTrustedStaticCarrierException(
                            $"duplicate trusted [KotlinStaticCarrier] for semantic owner '{semanticOwner}'");
                }
            }

            foreach (var type in types)
            {
                try
                {
                    // Index by the REAL Kotlin FQN (kotc emits "kotlin.String" etc. as the type name) so a BIR
                    // member-call owner token matches. A CLR-bound owner carries @ClrTypeAlias (the type-identity
                    // binding) or, for any not-yet-renamed bound class, a class-level @ClrIntrinsic.
                    var ownerFqn = StripGenericArity(type.FullName ?? type.Name);
                    var valueRepresentation = dotKtAuthored ? type.GetCustomAttributesData().SingleOrDefault(attribute =>
                        attribute.AttributeType.FullName == ValueRepresentationLowering.TypeAttribute) : null;
                    var representationSource = valueRepresentation == null ? ownerFqn
                        : (string)valueRepresentation.ConstructorArguments[4].Value;
                    if (representationSource == "") representationSource = ownerFqn;
                    if (valueRepresentation != null)
                        metadata.ValueRepresentations.Add(representationSource, new ValueRepresentationBinding(
                            (string)valueRepresentation.ConstructorArguments[0].Value,
                            (string)valueRepresentation.ConstructorArguments[1].Value,
                            (string)valueRepresentation.ConstructorArguments[2].Value,
                            (string)valueRepresentation.ConstructorArguments[3].Value,
                            (string)type.GetCustomAttributesData().SingleOrDefault(attribute =>
                                attribute.AttributeType.FullName == ValueRepresentationLowering.CaptureAttribute)?.ConstructorArguments[0].Value));
                    var exactPhysicalOwner = ExactPhysicalMetadataName(type);
                    // A dedicated carrier describes operational dispatch, not a Kotlin supertype or value view.
                    // Its slot vector explicitly maps the interface's physical frame into the source owner frame.
                    if (dotKtAuthored && type.IsInterface && type.IsGenericType
                        && HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr)
                        && CarrierJsonOf(type.GetCustomAttributesData(), asm,
                            RoundtripMetadata.AKManagedReferenceContract) is JsonObject referenceContract)
                    {
                        var arity = type.GetGenericArguments().Length;
                        var slots = ((JsonArray)referenceContract["slots"]).Select(value => value.GetValue<int>()).ToArray();
                        if (slots.Length != arity || slots.Any(index => index < 0)
                            || slots.Distinct().Count() != slots.Length)
                            throw new InvalidDataException("Malformed managed-reference contract frame");
                        var semantic = StripGenericArity(DottedFqn(referenceContract["owner"].GetValue<string>()));
                        if (!metadata.ManagedReferenceContracts.TryGetValue(semantic, out var contracts))
                            metadata.ManagedReferenceContracts.Add(semantic, contracts = new());
                        contracts.Add((exactPhysicalOwner, slots, referenceContract["declaration"]?.GetValue<string>(),
                            referenceContract["invoke"].GetValue<string>()));
                    }
                    AddExactPhysicalTypeName(metadata.ExactPhysicalTypeByDottedName,
                        OwnerIdentity(ownerFqn, type.IsGenericType ? type.GetGenericArguments().Length : 0),
                        exactPhysicalOwner);
                    if (dotKtAuthored && type.IsInterface && !type.IsGenericType
                        && HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr)
                        && KotlinTypeOf(type.GetCustomAttributesData(), asm) is TypeNode.Fqn
                        {
                            Args: { Length: > 0 } existentialArgs
                        } existentialSurface
                        && existentialArgs.All(a => a is TypeNode.Star))
                    {
                        var semantic = StripGenericArity(DottedFqn(existentialSurface.Name));
                        var declarations = types.Where(candidate =>
                        {
                            if (HasAttribute(candidate.GetCustomAttributesData(), CompilerGeneratedAttr)) return false;
                            var candidateName = StaticNestedSourceName(candidate);
                            return candidateName == semantic
                                && candidate.GetGenericArguments().Length == existentialArgs.Length;
                        }).ToArray();
                        if (declarations.Length != 1)
                            throw new InvalidDataException(
                                $"trusted Kotlin existential '{type.FullName}' resolves semantic owner "
                                + $"'{semantic}'/{existentialArgs.Length} to {declarations.Length} declarations");
                        var physical = ExactPhysicalMetadataName(type);
                        if (!metadata.ExistentialPhysicalBySemanticOwner.TryAdd(semantic, physical)
                            || metadata.ExistentialPhysicalBySemanticOwner[semantic] != physical)
                            throw new InvalidDataException($"duplicate Kotlin existential ABI for '{semantic}'");
                    }
                    var indexedOwnerFqn = semanticOwnerByStaticCarrier.GetValueOrDefault(
                        StripGenericArity(DottedFqn(ownerFqn))) ?? ownerFqn;
                    // dll2klib never exposes compiler-generated implementation classifiers to Kotlin. Do not let a
                    // same-named inline-materialized closure in the current compilation be rebound to a stale helper
                    // TypeDef from a reference assembly merely because both deterministic names coincide.
                    if (dotKtAuthored && !HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr))
                    {
                        var physicalName = ExactPhysicalMetadataName(type);
                        // KLIB classifiers use dotted nesting, while a reconstructed external classifier can
                        // legitimately re-enter BIR with the CLR `+` separator. Both spellings name the same
                        // trusted DotKt declaration; keep the exact arity-bearing metadata identity as the value.
                        metadata.PhysicalTypeBySemanticName[ownerFqn] = physicalName;
                        metadata.PhysicalTypeBySemanticName[DottedFqn(ownerFqn)] = physicalName;
                        var sourceName = StaticNestedSourceName(type);
                        if (sourceName != StripGenericArity(DottedFqn(ownerFqn)))
                        {
                            metadata.PhysicalTypeBySemanticName[sourceName] = physicalName;
                            metadata.DotKtOwners.Add(sourceName);
                            AddExactPhysicalTypeName(metadata.ExactPhysicalTypeByDottedName,
                                OwnerIdentity(sourceName, type.GetGenericArguments().Length), physicalName);
                        }
                    }
                    if (dotKtAuthored && AttrInt32(type.GetCustomAttributesData(), KotlinInnerAttr) is int capturedCount)
                    {
                        var innerName = StripGenericArity(DottedFqn(ownerFqn));
                        metadata.InnerCapturedCount[innerName] = capturedCount;
                        if (type.DeclaringType is Type declaringType)
                            metadata.InnerSemanticOwner[innerName] = StripGenericArity(DottedFqn(
                                declaringType.FullName ?? declaringType.Name));
                    }
                    if (dotKtAuthored && RichEnumMetadataOf(type, asm) is { } richEnum)
                    {
                        var semanticOwner = StripGenericArity(DottedFqn(ownerFqn));
                        if (!metadata.RichEnums.TryAdd(semanticOwner, richEnum))
                            throw new InvalidDataException(
                                $"duplicate trusted [KotlinRichEnum] for '{semanticOwner}'");
                    }
                    if (dotKtAuthored && BasicEnumMetadataOf(type, asm) is { } basicEnum)
                    {
                        var semanticOwner = StripGenericArity(DottedFqn(ownerFqn));
                        if (!metadata.BasicEnums.TryAdd(semanticOwner, basicEnum))
                            throw new InvalidDataException(
                                $"duplicate trusted [KotlinBasicEnum] for '{semanticOwner}'");
                    }
                    if (companionRepresentations.TryGetValue(type, out var companionIsStatic))
                        metadata.CompanionStaticByPhysicalOwner.Add(
                            StripGenericArity(DottedFqn(ownerFqn)), companionIsStatic);
                    var typeDeclarationIdentity = OwnerIdentity(ownerFqn,
                        type.IsGenericType ? type.GetGenericArguments().Length : 0);
                    var declarationKind = TypeKind(type);
                    metadata.TypeKinds[typeDeclarationIdentity] = declarationKind;
                    metadata.PhysicalTypeKinds[exactPhysicalOwner] = declarationKind;
                    if (type.IsValueType || !type.IsAbstract && !type.IsInterface &&
                        MetadataDeclaredMembers.HasPublicParameterlessConstructor(type))
                    {
                        metadata.PublicParameterlessConstructibleOwners.Add(
                            typeDeclarationIdentity);
                        metadata.PublicParameterlessConstructiblePhysicalOwners.Add(exactPhysicalOwner);
                    }
                    // Both spellings: the reflection name nests with `+`, every bir2cir type token is DOTTED, and a
                    // NESTED `ref struct` (`Span<T>.Enumerator`, `MemoryExtensions.SpanSplitEnumerator`) is exactly
                    // the shape a spill of `for (x in span)` would mint a field of.
                    if (IsByRefLikeType(type))
                    {
                        metadata.ByRefLikeOwners.Add(typeDeclarationIdentity);
                        metadata.ByRefLikePhysicalOwners.Add(exactPhysicalOwner);
                    }
                    var sourceFacts = dotKtAuthored
                        ? CarrierJsonOf(type.GetCustomAttributesData(), asm, KotlinSupertypesAttr) as JsonObject : null;
                    var semanticTypeShape = new ReferenceTypeShape(
                        type.IsGenericType ? type.GetGenericArguments().Length : 0,
                        TypeKind(type),
                        HierarchyView(type.BaseType),
                        type.GetInterfaces().Select(HierarchyView).OfType<TypeNode.Fqn>().ToArray(), sourceFacts);
                    metadata.TypeShapes[typeDeclarationIdentity] = semanticTypeShape;
                    // Inheritance edges are declaration identities just like member owners. The exact index retains
                    // the reflected TypeDef spelling so current-format override markers traverse a physical graph
                    // without falling back to arity-free names. Keep the semantic graph separately: Kotlin aliases
                    // intentionally walk that vocabulary before bir2cir selects their CLR representation.
                    metadata.PhysicalTypeShapes[exactPhysicalOwner] = new ReferenceTypeShape(
                        semanticTypeShape.TypeParamCount,
                        semanticTypeShape.Kind,
                        ExactDeclaringView(type.BaseType),
                        type.GetInterfaces().Select(ExactDeclaringView).OfType<TypeNode.Fqn>().ToArray(), sourceFacts);
                    if (type.IsGenericType)
                    {
                        var gargs = type.GetGenericArguments();
                        if (sourceFacts?[NullableRepresentationFrame.MetadataKey] is JsonNode frameNode)
                        {
                            var frame = ReadNullableFrame(frameNode, gargs.Length);
                            metadata.NullableFrames[ownerFqn] = frame;
                            metadata.NullableFrames[DottedFqn(ownerFqn)] = frame;
                            metadata.NullableFrames[exactPhysicalOwner] = frame;
                            // Frame materialization precedes physical type-name binding. A relocated classifier's
                            // source identity must select the same producer-authored frame as its CLR TypeDef.
                            if (!HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr))
                                metadata.NullableFrames[StaticNestedSourceName(type)] = frame;
                        }
                        metadata.TypeArity[ownerFqn] = gargs.Length;
                        metadata.TypeArity[DottedFqn(ownerFqn)] = gargs.Length;
                        metadata.TypeParamNames[ownerFqn] = gargs.Select(g => g.Name).ToArray();
                        metadata.TypeParamNames[DottedFqn(ownerFqn)] = gargs.Select(g => g.Name).ToArray();
                        var declarations = gargs.Select(GenericParamSourceDeclaration).ToArray();
                        if (dotKtAuthored)
                            RestoreKotlinTypeParameterVariances(type, asm, declarations);
                        metadata.TypeParamDeclarations[ownerFqn] = declarations;
                        metadata.TypeParamDeclarations[DottedFqn(ownerFqn)] = declarations;
                        if (dotKtAuthored && !HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr))
                            metadata.TypeParamDeclarations[StaticNestedSourceName(type)] = declarations;
                        // The struct-ness ORACLE for a TYPE VARIABLE (#37/#48): record each type-param's CLR constraint
                        // class from GenericParameterAttributes so a `T?` on a struct-constrained param stays Nullable<T>.
                        metadata.TypeParamConstraints[ownerFqn] = gargs.Select(GenericParamConstraintClass).ToArray();
                        // The declared type-BOUND of each type param (`E : Element` -> Element), keyed by the DOTTED FQN
                        // (nested `+` -> `.`) so bir2cir's dotted lookup (StarProjectionBoundLowering) matches. A star
                        // projection `Key<*>` erased to `Key<object>` violates this bound and is repointed to `Key<Element>`.
                        metadata.TypeParamBounds[DottedFqn(ownerFqn)] = gargs.Select(GenericParamBound).ToArray();
                    }
                    var classAlias = ClrAliasOf(type.GetCustomAttributesData());
                    if (classAlias != null)
                    {
                        metadata.Aliases[ownerFqn] = classAlias;
                        metadata.Aliases[DottedFqn(ownerFqn)] = classAlias;
                        metadata.AliasKinds[ownerFqn] = TypeKind(type);
                        metadata.AliasKinds[DottedFqn(ownerFqn)] = TypeKind(type);
                        metadata.Aliases[representationSource] = classAlias;
                        metadata.Aliases[DottedFqn(representationSource)] = classAlias;
                        metadata.AliasKinds[representationSource] = TypeKind(type);
                        metadata.AliasKinds[DottedFqn(representationSource)] = TypeKind(type);
                    }
                    // A compiler-generated implementation classifier carried by an inline body may capture the
                    // enclosing receiver/free values through its sole constructor. Record that exact declaration
                    // shape so StringCharSequenceBridge can adapt a static String value to a CharSequence capture
                    // slot even after #225 nests the classifier. CompilerGenerated + exactly one constructor is the
                    // structural boundary; choosing the first of multiple overloads would be an ownership guess.
                    if (dotKtAuthored && HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr))
                    {
                        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (ctors.Length == 1)
                        {
                            var ctorParams = ctors[0].GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray();
                            metadata.CtorParamTypes[ownerFqn] = ctorParams;
                            metadata.CtorParamTypes[DottedFqn(ownerFqn)] = ctorParams;
                            metadata.CtorParamTypes[ExactPhysicalMetadataName(type)] = ctorParams;
                        }
                    }
                    if (ownerFqn.StartsWith("dotkt$ClrH_", StringComparison.Ordinal)) metadata.HelperTypes.Add(ownerFqn);
                    if (HasAttribute(type.GetCustomAttributesData(), RestrictsSuspensionAttr)) metadata.RestrictsSuspensionTypes.Add(ownerFqn);
                    var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);
                    if (dotKtAuthored && isFileClass)
                        metadata.FileClassOwners.Add(StripGenericArity(DottedFqn(ownerFqn)));
                    if (dotKtAuthored)
                        IndexCompanionExtensionMembers(type, ownerFqn, isFileClass, metadata);
                    IndexCSharp14StaticExtensionMembers(type, ownerFqn, metadata, dotKtAuthored);

                    // `value`/inline class (marked with [KotlinValue], the 2.4.0 carrier of `mods.value`): its single
                    // instance backing field IS the erased value. Record that property's GETTER + the field's CLR conv
                    // token so the semantic property getter collapses to `conv(<recv>)`. NARROWED to EXACTLY ONE instance field
                    // — a value class has precisely one property/backing field, so requiring a single field picks the
                    // correct underlying type (and refuses to erase off an arbitrary FirstOrDefault if the shape is
                    // unexpected). The GETTER is the accessor of the PROPERTY that OWNS that field: an accessor-routed
                    // property's storage carries the compiler-generated `<data>k__BackingField` name. The owning
                    // PropertyInfo and its exact GetMethod association are therefore required; no field-name fallback
                    // can identify the Kotlin accessor.
                    if (HasAttribute(type.GetCustomAttributesData(), KotlinValueAttr))
                    {
                        var instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        var backing = instanceFields.Length == 1 ? instanceFields[0] : null;
                        if (backing != null && InlineFieldConv(backing.FieldType) is string conv)
                        {
                            var owningProp = type
                                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                .FirstOrDefault(p => BackingFieldRename.Mangle(p.Name) == backing.Name);
                            if (owningProp?.GetGetMethod(true) is MethodInfo getter)
                                metadata.InlineBacking[ownerFqn] = (getter.Name, conv);
                        }
                    }

                    // MethodSemantics is an association table. Index it once per Type instead of rescanning every
                    // PropertyInfo for every MethodInfo below. A malformed/ambiguous method associated with more than
                    // one property states no source identity; never select whichever reflection happens to return first.
                    var propertyAccessors = new Dictionary<int, (string Name, string Kind, bool IsIndexer)?>();
                    void IndexPropertyAccessor(MethodInfo accessor, string propertyName, string kind, bool isIndexer)
                    {
                        if (accessor == null) return;
                        var association = (propertyName, kind, isIndexer);
                        if (propertyAccessors.TryGetValue(accessor.MetadataToken, out var existing)
                            && existing != association)
                            propertyAccessors[accessor.MetadataToken] = null;
                        else
                            propertyAccessors[accessor.MetadataToken] = association;
                    }
                    foreach (var property in MetadataDeclaredMembers.Properties(type))
                    {
                        var isIndexer = property.GetIndexParameters().Length > 0;
                        IndexPropertyAccessor(property.GetGetMethod(true), property.Name, "get", isIndexer);
                        IndexPropertyAccessor(property.GetSetMethod(true), property.Name, "set", isIndexer);
                    }

                    foreach (var method in MetadataDeclaredMembers.Methods(type))
                    {
                        propertyAccessors.TryGetValue(method.MetadataToken, out var owningProperty);
                        var carriedProperty = dotKtAuthored
                            ? KotlinPropertyAccessorPayload(method.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                            : null;
                        var sourceMethod = dotKtAuthored
                            ? KotlinSourceMethodPayload(method.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                            : null;
                        var sourceMethodName = !HasAttribute(method.GetCustomAttributesData(), CompilerGeneratedAttr)
                            ? sourceMethod?.Name : null;
                        var innerConstructorFactory = dotKtAuthored
                            ? KotlinInnerConstructorFactoryPayload(
                                method.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                            : null;
                        // dll2klib projects a parameterized CLR Property as Kotlin operator `get`/`set` functions.
                        // Preserve that source identity while retaining the associated MethodDef's exact physical name
                        // (`get_Item`, a custom indexer accessor, etc.) for MethodImpl allocation.
                        if (sourceMethodName == null && carriedProperty is null && owningProperty?.IsIndexer == true)
                            sourceMethodName = owningProperty.Value.Kind;
                        // Declaration identity is also required for a non-public target embedded in a public default
                        // argument/inline carrier. The consumer must retarget the generated UnsafeAccessor to the
                        // producer's allocated MethodDef without resolving again from its erased signature.
                        var declarationIdentity = dotKtAuthored
                            ? KotlinDeclarationIdentityPayload(method.GetCustomAttributesData(), method.DeclaringType?.Assembly,
                                method.GetGenericArguments().Length)
                            : null;
                        var intrinsic = ClrIntrinsicOf(method.GetCustomAttributesData());
                        var prop = ClrPropertyOf(method.GetCustomAttributesData());
                        var byrefPositions = ByrefPositionsOf(method);
                        var nativeSignature = methodSignature(method);
                        var countRange = CountRangeOf(method);
                        var collectionFactoryKind = isFileClass && method.IsStatic
                            ? AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrCollectionFactory")
                            : null;
                        var arrayFactoryKind = isFileClass && method.IsStatic
                            ? AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrArrayFactory")
                            : null;
                        var arrayFactoryElementHint = arrayFactoryKind == null
                            ? null
                            : ArrayElemHint(method.ReturnType);
                        // @ClrConv (numeric primitive conversion): the call lowers to a CIL `conv` to the callee's OWN
                        // declared return type (toLong -> the emitted `kotlin.Long` type, ...). Read the marker + capture
                        // the return-type token here (the pre-lowering Kotlin FQN, from THIS reference/metadata dll), so
                        // MemberCallSubstitution can emit `{k:conv, to:<convTo>, e:<recv>}` — the target BirTypeLowering
                        // then lowers to System.Int64/etc. and ilemit picks the conv opcode.
                        var isConv = HasAttribute(method.GetCustomAttributesData(), "kotlin.clr.ClrConv");
                        var convTo = isConv ? DeclarationTypeNode(method.ReturnType) : null;
                        // Default argument VALUES remain authoritative in the selected reference DLL. KotlinDefault
                        // contributes its raw Kotlin-expression BIR; an ordinary ECMA-335 constant contributes a plain
                        // const expression. The reference KLIB carries only DECLARES_DEFAULT_VALUE for frontend
                        // resolution, never either payload.
                        if (CallableDefaultsOf(method, coreName => mlc.CoreAssembly.GetType(coreName, throwOnError: false)) is Dictionary<int, IBirExpression> defaults)
                        {
                            AddKotlinDefaults(metadata, indexedOwnerFqn, method.Name, method.GetParameters(), defaults);
                            if (exactPhysicalOwner != indexedOwnerFqn)
                                AddKotlinDefaults(metadata, exactPhysicalOwner, method.Name, method.GetParameters(), defaults);
                            if (declarationIdentity?.Id is string declarationId
                                && !metadata.KotlinDefaultsByDeclarationId.TryAdd(declarationId, defaults))
                                throw new InvalidDataException($"duplicate defaults for Kotlin declaration identity '{declarationId}'");
                        }
                        // The `suspend` bit from the DotKt round-trip [KotlinFunction(flags)] attribute (Suspend = 4,
                        // the flag word ilemit stamps; the dead Assembly.LoadFrom scan read it, this live scan didn't).
                        // Channelled into MemberBinding.Suspend for the coroutine bundle (bundle 6) — no consumer yet.
                        var suspend = (KotlinFunctionFlags(method.GetCustomAttributesData()) & KotlinFunctionSuspendFlag) != 0;
                        var suspendReturn = dotKtAuthored && suspend
                            ? CarrierTypeOf(method.GetCustomAttributesData(), method.DeclaringType?.Assembly,
                                KotlinSuspendResultAttr)
                                ?? throw new InvalidDataException(
                                    $"trusted suspend MethodDef '{method.DeclaringType?.FullName}.{method.Name}' has no logical-result carrier")
                            : null;
                        if (suspend && Environment.GetEnvironmentVariable("DOTKT_BIR2CIR_DEBUG_SUSPEND") == "1")
                            Console.Error.WriteLine($"bir2cir: ref-scan suspend member {ownerFqn}.{method.Name}/{method.GetParameters().Length} (Suspend=true)");
                        metadata.MemberBindings.Add(new MemberBinding(
                            indexedOwnerFqn,
                            method.Name,
                            method.GetParameters().Length,
                            intrinsic,
                            method.IsAbstract,
                            method.IsStatic,
                            prop?.Access ?? 0,
                            prop?.Name,
                            byrefPositions,
                            suspend,
                            isConv,
                            convTo,
                            nativeSignature?.Return ?? TypeNodeOf(method.ReturnType),
                            method.GetGenericArguments().Length,
                            method.GetParameters().Select((p, i) => nativeSignature?.Parameters[i] ?? DeclarationTypeNode(p.ParameterType)).ToArray(),
                            method.IsVirtual,
                            dotKtAuthored ? KotlinTypeOf(method.ReturnParameter.GetCustomAttributesData(), method.DeclaringType?.Assembly) : null,
                            suspendReturn,
                            // #86 D1 — the positional pre-erasure carrier, per slot. Only a DotKt-authored assembly can
                            // carry it, and only the erasure records it, so a slot without one is simply absent here and
                            // the consumer falls back to the physical declaration (which IS `Erase(decl)` by construction).
                            dotKtAuthored ? CarrierTypeOf(method.ReturnParameter.GetCustomAttributesData(), method.DeclaringType?.Assembly, KotlinNullableGenericAttr) : null,
                            dotKtAuthored
                                ? method.GetParameters().Select(p => CarrierTypeOf(p.GetCustomAttributesData(), method.DeclaringType?.Assembly, KotlinNullableGenericAttr)).ToArray()
                                : null,
                            nativeSignature?.Return ?? DeclarationTypeNode(method.ReturnType),
                            method.MetadataToken,
                            carriedProperty?.Name ?? owningProperty?.Name,
                            carriedProperty?.Kind ?? owningProperty?.Kind,
                            owningProperty?.Name,
                            carriedProperty?.SourceAssociation != null,
                            method.IsPublic,
                            carriedProperty?.Association,
                            carriedProperty?.SourceAssociation,
                            sourceMethodName,
                            method.GetGenericArguments().Select(GenericParamSourceDeclaration).ToArray(),
                            declarationIdentity?.Id,
                            declarationIdentity?.Name,
                            exactPhysicalOwner,
                            declarationIdentity?.SemanticParams,
                            declarationIdentity?.SemanticReturn,
                            collectionFactoryKind,
                            arrayFactoryKind,
                            arrayFactoryElementHint,
                            countRange?.Start ?? -1,
                            countRange?.End ?? -1,
                            declarationIdentity?.SemanticReifiedTypeParameterIndices,
                            declarationIdentity?.NullableWitnessTypeParameterIndices,
                            dotKtAuthored
                                ? method.GetParameters().Select(p =>
                                    CarrierTypeOf(p.GetCustomAttributesData(), method.DeclaringType?.Assembly,
                                        KotlinSuspendFunctionTypeAttr)
                                    ?? KotlinTypeOf(p.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                                    ?? DeclarationTypeNode(p.ParameterType)).ToArray()
                                : null,
                            innerConstructorFactory?.Inner,
                            innerConstructorFactory?.Declaration,
                            innerConstructorFactory?.TypeArguments,
                            SemanticMethodTypeParameters(method, dotKtAuthored, declarationIdentity?.NullableFrame), declarationIdentity?.NullableFrame)
                        {
                            SourceMethodDeclaration = sourceMethod?.SourceDeclaration,
                            ReflectedWitnessIndices = declarationIdentity?.ReflectedWitnessIndices,
                            ValueAdapter = dotKtAuthored ? method.GetCustomAttributesData().SingleOrDefault(attribute =>
                                attribute.AttributeType.FullName == ValueRepresentationLowering.MemberAttribute)
                                ?.ConstructorArguments[0].Value as string : null,
                            ValueSlot = dotKtAuthored ? method.GetCustomAttributesData().SingleOrDefault(attribute =>
                                attribute.AttributeType.FullName == ValueRepresentationLowering.MemberAttribute)
                                ?.ConstructorArguments[1].Value as string : null,
                            SignatureAllocated = declarationIdentity?.SignatureAllocated == true,
                            LateinitState = dotKtAuthored ? ReadLateinitState(method) : null,
                            SignatureParameters = method.GetParameters().Select(signatureSlot).ToArray(),
                            SignatureReturn = signatureSlot(method.ReturnParameter),
                            PhysicalVisibility = method.IsPublic ? "public"
                                : method.IsFamily || method.IsFamilyOrAssembly ? "protected"
                                : method.IsAssembly ? "internal" : "private",
                            ParameterNullableFlags = dotKtAuthored
                                ? method.GetParameters().Select((parameter, i) => ReferenceParameterNullableFlags(parameter,
                                    nativeSignature?.Parameters[i] ?? DeclarationTypeNode(parameter.ParameterType))).ToArray() : null,
                            VarargPositions = method.GetParameters()
                                .Where(parameter => parameter.GetCustomAttributesData().Any(attribute =>
                                    attribute.AttributeType.FullName == "System.ParamArrayAttribute"))
                                .Select(parameter => parameter.Position).ToArray(),
                        });
                        // [KotlinInline] raw-BIR carrier (#71/#75 S1): decode the versioned carrier now (the codec is
                        // BirCarrier, shared) and key it owner|name|pc|ga so InlineSplice can splice this external inline
                        // fn's body at a cross-module call site. This carrier is compiler-internal ABI: an older or
                        // malformed payload is unsupported and never enters a plain-call compatibility path.
                        // ga = generic arity.
                        var inlineCad = method.GetCustomAttributesData().FirstOrDefault(c => c.AttributeType.FullName == KotlinInlineAttr);
                        if (inlineCad != null)
                        {
                            if (inlineCad.ConstructorArguments.Count != 2)
                                throw new InvalidDataException();
                            try
                            {
                                var ver = (string)inlineCad.ConstructorArguments[0].Value!;
                                var content = ReadByteArrayArg(inlineCad.ConstructorArguments[1]);
                                var decoded = ExecutableBirCodec.ReadInline(ver, content);
                                // §4.2 (#75 S4b): key `owner|name|pc|ga` -> a LIST of candidate overload payloads. pc/ga come
                                // from the .NET method (the payload counts them identically: an extension receiver rides as a
                                // leading `__self` param). InlineSplice picks the UNIQUE candidate whose decoded `params[i].type`
                                // structurally matches the call's paramSig — the decoded params are kotc-emitted decl nodes, so
                                // they equal the callInline's paramSig exactly (both from `birType(param.type)`).
                                var ikey = indexedOwnerFqn + "|" + method.Name + "|" + method.GetParameters().Length + "|" + method.GetGenericArguments().Length;
                                if (!metadata.InlinePayloads.TryGetValue(ikey, out var ilst)) metadata.InlinePayloads[ikey] = ilst = new List<BirInlinePayload>();
                                ilst.Add(decoded);
                                if (declarationIdentity?.Id is string declarationId
                                    && !metadata.InlinePayloadsByDeclarationId.TryAdd(declarationId, decoded))
                                    throw new InvalidDataException($"duplicate inline declaration identity '{declarationId}'");
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidDataException(null, ex);
                            }
                        }
                        // A top-level fun (file-class static) with @ClrIntrinsic. TWO shapes:
                        //   FQ "System.X.Y"  -> a fully-qualified BCL static (isNaN, clrTimestamp); keyed by NAME.
                        //   bare "Name"      -> a member on an EXTENSION receiver (`Array<T>.nativeClone()` ->
                        //                       @ClrIntrinsic("Clone")). Keyed by NAME|recvKey (the first param's type),
                        //                       because the name alone collides across receivers (MutableMap.set->set_Item
                        //                       vs StringBuilder.set->set_Chars). recvKey of the call site is its first arg.
                        if (isFileClass && method.IsStatic && intrinsic != null)
                        {
                            var ps = method.GetParameters();
                            if (intrinsic.Contains('.'))
                            {
                                // Name-only map (first-wins) is retained for single-static intrinsics (isNaN,
                                // clrTimestamp); when a name is seen binding to a DIFFERENT static, mark it ambiguous so
                                // the caller requires an exact-signature match instead (sqrt/abs/pow -> Math vs MathF).
                                if (metadata.TopLevelIntrinsics.TryGetValue(method.Name, out var prior))
                                {
                                    if (prior != intrinsic) metadata.AmbiguousTopLevelIntrinsics.Add(method.Name);
                                }
                                else metadata.TopLevelIntrinsics[method.Name] = intrinsic;
                                // ALSO key by name|<full ParamKey signature> so a call resolves the EXACT overload
                                // (sqrt(Double)->System.Math.Sqrt, sqrt(Float)->System.MathF.Sqrt) and a non-intrinsic
                                // sibling (Double.pow(Int)) misses -> falls through to its real Kotlin body.
                                metadata.TopLevelIntrinsicsBySig.TryAdd((method.Name, ArrayBindingSigKeyOf(ps)), intrinsic);
                                if (byrefPositions.Length > 0) metadata.TopLevelIntrinsicByref.TryAdd(method.Name, byrefPositions);
                            }
                            else if (ps.Length >= 1)
                                // Key by name|<full ParamKey signature> (receiver-first, mirroring TopLevelIntrinsicsBySig)
                                // so a call resolves the EXACT overload — `substring(Int)`@ClrIntrinsic does NOT capture a
                                // same-count non-intrinsic sibling `substring(IntRange)` (which then falls to its Kotlin body).
                                metadata.ExtMemberIntrinsics.TryAdd((method.Name, ArrayBindingSigKeyOf(ps)), intrinsic);
                        }
                        // A NON-intrinsic top-level fun (a real Kotlin body in a file-class) -> index it by name so an APP
                        // build can attribute a referenced `callStatic owner=null` to this file-class (disambiguated by the
                        // first-param receiver type when overloaded across file-classes). The stdlib self-build never reads it.
                        // #157: this DELIBERATELY has no IsSpecialName exclusion, so a top-level property accessor (a
                        // file-class static with intrinsic==null) is indexed too. That is what lets a cross-module
                        // top-level `val` read (kotc emits owner:null + prop:get; bir2cir allocates the physical name) resolve GENERICALLY
                        // through TryResolveTopLevelStatic (e.g. COROUTINE_SUSPENDED -> IntrinsicsKt), with no per-name special-case.
                        var isCSharpExtension = method.IsStatic && method.GetCustomAttributesData().Any(a =>
                            a.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute");
                        if ((isFileClass || isCSharpExtension) && method.IsStatic && intrinsic == null)
                        {
                            var ps = method.GetParameters();
                            var rk = ps.Length >= 1 ? RecvKey(ps[0].ParameterType) : "";
                            // The FINE first-param key (ParamKey space): distinguishes the array overloads a coarse "[]"
                            // recvKey collapses arrays, so the structural key distinguishes IntArray, UByteArray, and Array<T>
                            // owner attribution pins the RIGHT file-class+overload (#153 unsigned-array miscompile).
                            var pk = ps.Length >= 1 ? ReceiverParamKey(ps[0].ParameterType) : null;
                            if (!metadata.TopLevelStatics.TryGetValue(method.Name, out var lst))
                                metadata.TopLevelStatics[method.Name] = lst = new List<(string, string, TypeKey)>();
                            lst.Add((ownerFqn, rk, pk));
                        }
                        // Retain the collection name index for map realignment. Construction
                        // lowering uses the selected declaration's MemberBinding instead.
                        if (isFileClass && method.IsStatic)
                        {
                            if (collectionFactoryKind is string cf)
                                metadata.CollectionFactories[method.Name] = cf;
                        }
                    }
                    // KotlinDefaultAttribute(index, codec, content) on a CONSTRUCTOR's params -> the splice source for a `new` that omits a
                    // non-constant default (#235). A ctor has no name of its own, so the key is `<owner>|.ctor|<declared
                    // param count>`: the args array a `new` carries is POSITIONALLY complete (kotc emits a placeholder for
                    // every omitted slot), so its length is that same declared count and each stamped index indexes it.
                    // NonPublic included for the same reason the method scan includes it — an `internal` ctor is still a
                    // legitimate splice target inside its own module's ref dll.
                    // A CONSTRUCTOR has no name of its own, so it is keyed by the `.ctor` pseudo-name (#235). Same scheme as
                    // the method scan above: the `new` carries its resolved ctor's declared parameter types in `argTypes`,
                    // the same ParamKey space, so same-arity ctor overloads resolve rather than collide.
                    foreach (var ctor in MetadataDeclaredMembers.Constructors(type).Where(constructor => !constructor.IsStatic))
                    {
                        if (dotKtAuthored)
                        {
                            // Constructor signatures imported from Kotlin retain source vocabulary even when the
                            // owning class has no companions (or no type parameters at all).
                            var constructorFrame = metadata.NullableFrames.GetValueOrDefault(ownerFqn)
                                ?? new NullableRepresentationFrame(type.IsGenericType ? type.GetGenericArguments().Length : 0,
                                    Array.Empty<int>());
                            metadata.KotlinConstructorOwnerFrames[ownerFqn] = constructorFrame;
                            metadata.KotlinConstructorOwnerFrames[DottedFqn(ownerFqn)] = constructorFrame;
                            metadata.KotlinConstructorOwnerFrames[exactPhysicalOwner] = constructorFrame;
                        }
                        if (CallableDefaultsOf(ctor, coreName => mlc.CoreAssembly.GetType(coreName, throwOnError: false)) is Dictionary<int, IBirExpression> cdefaults)
                        {
                            AddKotlinDefaults(metadata, ownerFqn, CtorKeyName, ctor.GetParameters(), cdefaults);
                            if (exactPhysicalOwner != ownerFqn)
                                AddKotlinDefaults(metadata, exactPhysicalOwner, CtorKeyName, ctor.GetParameters(), cdefaults);
                        }
                        if (dotKtAuthored && AliasConstructorAdapterOf(
                                ctor.GetCustomAttributesData(), ctor.DeclaringType?.Assembly) is { } adapter)
                            metadata.AliasConstructorAdapters.Add(new(DottedFqn(ownerFqn), adapter));
                        // #86 D1 — a `new`'s arguments fill the constructor's declaration slots, so the ctor's shape is
                        // indexed exactly as a method's is. `Cell<T>(x: T?)` erases to `.ctor(object)` and its carrier
                        // holds the pre-erasure `T?`.
                        metadata.CtorBindings.Add(new CtorBinding(
                            ownerFqn,
                            exactPhysicalOwner,
                            ctor.GetParameters().Length,
                            ctor.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray(),
                            dotKtAuthored
                                ? ctor.GetParameters().Select(p => CarrierTypeOf(p.GetCustomAttributesData(), ctor.DeclaringType?.Assembly, KotlinNullableGenericAttr)).ToArray()
                                : null));
                    }
                }
                catch (MalformedTrustedCompanionException) { throw; }
                catch (MalformedTrustedStaticCarrierException) { throw; }
                catch (InvalidDataException) { throw; }
                catch (Exception ex)
                {
                    metadata.Diagnostics.Add($"subst scan skip {type?.FullName}: {ex.GetType().Name}");
                }
            }
        }
        catch (MalformedTrustedCompanionException) { throw; }
        catch (MalformedTrustedStaticCarrierException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            metadata.Diagnostics.Add($"{Path.GetFileName(reference)}: subst scan failed: {ex.GetType().Name}: {ex.Message}");
        }
        return metadata;
    }

    // CustomAttribute.Parent can name metadata rows that reflection has no object model for (notably InterfaceImpl
    // and GenericParamConstraint). Validate the trusted companion-extension and static carriers over the complete ECMA
    // table before MLC projects the assembly. The shared raw reader also validates the exact constructor blobs
    // (including zero named arguments), so bir2cir and dll2klib accept precisely the same carrier envelopes rather than
    // two reflection-dependent subsets.
    static void ValidateCompanionMetadata(string reference)
    {
        using var stream = File.OpenRead(reference);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return;
        var reader = pe.GetMetadataReader();
        var attrs = new MetadataAttributes(reader);
        if (!attrs.IsDotKtAssembly) return;

        attrs.ValidateCarrierTargets(
            KotlinCompanionExtensionAttr,
            HandleKind.MethodDefinition,
            HandleKind.FieldDefinition);
        attrs.ValidateCarrierTargets(
            KotlinExtensionCoreAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinPropertyAccessorAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinSourceMethodAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinInnerConstructorFactoryAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinDeclarationIdentityAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinLateinitStateAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinSuspendResultAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinStaticCarrierAttr,
            HandleKind.TypeDefinition);
        attrs.ValidateCarrierTargets(RoundtripMetadata.AKManagedReferenceContract, HandleKind.TypeDefinition);
        attrs.ValidateCarrierTargets(
            KotlinRichEnumAttr,
            HandleKind.TypeDefinition);
        attrs.ValidateCarrierTargets(
            KotlinBasicEnumAttr,
            HandleKind.TypeDefinition);
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            using (attrs.CarrierDocument(typeHandle, KotlinStaticCarrierAttr)) { }
            using (attrs.CarrierDocument(typeHandle, RoundtripMetadata.AKManagedReferenceContract)) { }
            using (attrs.CarrierDocument(typeHandle, KotlinRichEnumAttr)) { }
            using (attrs.CarrierDocument(typeHandle, KotlinBasicEnumAttr)) { }
            foreach (var method in type.GetMethods())
            {
                using (attrs.CarrierDocument(method, KotlinCompanionExtensionAttr)) { }
                using (attrs.CarrierDocument(method, KotlinExtensionCoreAttr)) { }
                using (attrs.CarrierDocument(method, KotlinPropertyAccessorAttr)) { }
                using (attrs.CarrierDocument(method, KotlinSourceMethodAttr)) { }
                using (attrs.CarrierDocument(method, KotlinInnerConstructorFactoryAttr)) { }
                using (attrs.CarrierDocument(method, KotlinDeclarationIdentityAttr)) { }
            }
            foreach (var field in type.GetFields())
                using (attrs.CarrierDocument(field, KotlinCompanionExtensionAttr)) { }
        }
    }

    static void IndexMethodImplMetadata(string reference, ReferenceDotKtMetadata metadata)
    {
        using var stream = File.OpenRead(reference);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return;
        var reader = pe.GetMetadataReader();
        if (!new MetadataAttributes(reader).IsDotKtAssembly) return;
        var provider = new MethodImplOwnerTypeProvider();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var bodyOwner = MetadataDefinitionName(reader, typeHandle);
            foreach (var implementationHandle in type.GetMethodImplementations())
            {
                var implementation = reader.GetMethodImplementation(implementationHandle);
                if (implementation.MethodBody.Kind != HandleKind.MethodDefinition) continue;
                if (!TryMethodImplDeclaration(reader, provider, implementation.MethodDeclaration,
                        out var declarationOwner, out var declarationMember))
                    continue;
                metadata.MethodImplBindings.Add(new MethodImplBinding(
                    bodyOwner,
                    MetadataTokens.GetToken(implementation.MethodBody),
                    declarationOwner,
                    declarationMember));
            }
        }
    }

    static bool TryMethodImplDeclaration(MetadataReader reader, MethodImplOwnerTypeProvider provider,
        EntityHandle declaration, out TypeNode.Fqn owner, out string member)
    {
        owner = null;
        member = null;
        EntityHandle parent;
        switch (declaration.Kind)
        {
            case HandleKind.MemberReference:
            {
                var reference = reader.GetMemberReference((MemberReferenceHandle)declaration);
                member = reader.GetString(reference.Name);
                parent = reference.Parent;
                break;
            }
            case HandleKind.MethodDefinition:
            {
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)declaration);
                member = reader.GetString(definition.Name);
                parent = definition.GetDeclaringType();
                break;
            }
            default:
                return false;
        }
        TypeNode decoded = parent.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                reader, (TypeDefinitionHandle)parent, (byte)SignatureTypeKind.Class),
            HandleKind.TypeReference => provider.GetTypeFromReference(
                reader, (TypeReferenceHandle)parent, (byte)SignatureTypeKind.Class),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)parent)
                .DecodeSignature(provider, default(MethodImplGenericContext)),
            _ => null,
        };
        owner = decoded as TypeNode.Fqn;
        return owner != null && !string.IsNullOrEmpty(member);
    }

    static string MetadataDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var simple = StripGenericArity(reader.GetString(definition.Name));
        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return MetadataDefinitionName(reader, parent) + "." + simple;
        var ns = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    readonly record struct MethodImplGenericContext;

    sealed class MethodImplOwnerTypeProvider : ISignatureTypeProvider<TypeNode, MethodImplGenericContext>
    {
        public TypeNode GetArrayType(TypeNode elementType, ArrayShape shape) => new TypeNode.Array(elementType);
        public TypeNode GetByReferenceType(TypeNode elementType) => new TypeNode.ByRef(elementType);
        public TypeNode GetFunctionPointerType(MethodSignature<TypeNode> signature) =>
            new TypeNode.FnPtr(signature.Header.RawValue, signature.GenericParameterCount,
                signature.RequiredParameterCount, signature.ReturnType, signature.ParameterTypes.ToArray());
        public TypeNode GetGenericInstantiation(TypeNode genericType, ImmutableArray<TypeNode> typeArguments) =>
            genericType is TypeNode.Fqn f ? new TypeNode.Fqn(f.Name, typeArguments.ToArray()) : genericType;
        public TypeNode GetGenericMethodParameter(MethodImplGenericContext genericContext, int index) =>
            new TypeNode.Tv("method", index);
        public TypeNode GetGenericTypeParameter(MethodImplGenericContext genericContext, int index) =>
            new TypeNode.Tv("type", index);
        public TypeNode GetModifiedType(TypeNode modifier, TypeNode unmodifiedType, bool isRequired) => unmodifiedType;
        public TypeNode GetPinnedType(TypeNode elementType) => elementType;
        public TypeNode GetPointerType(TypeNode elementType) => new TypeNode.Ptr(elementType);
        public TypeNode GetPrimitiveType(PrimitiveTypeCode typeCode) => new TypeNode.Fqn(typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => throw new InvalidDataException($"unsupported primitive in MethodImpl owner: {typeCode}"),
        });
        public TypeNode GetSZArrayType(TypeNode elementType) => new TypeNode.Array(elementType);
        public TypeNode GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new TypeNode.Fqn(MetadataDefinitionName(reader, handle));
        public TypeNode GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            new TypeNode.Fqn(MetadataReferenceName(reader, handle));
        public TypeNode GetTypeFromSpecification(MetadataReader reader, MethodImplGenericContext genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        static string MetadataReferenceName(MetadataReader reader, TypeReferenceHandle handle)
        {
            var reference = reader.GetTypeReference(handle);
            var simple = StripGenericArity(reader.GetString(reference.Name));
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                return MetadataReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "." + simple;
            var ns = reader.GetString(reference.Namespace);
            return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
        }
    }

    sealed record AssemblyProvenance(bool DotKtEmitted);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Assembly, AssemblyProvenance> assemblyProvenance = new();

    static bool IsDotKtEmittedAssembly(Assembly asm) => asm != null
        && assemblyProvenance.GetValue(asm, assembly => new AssemblyProvenance(ReadDotKtEmittedAssembly(assembly))).DotKtEmitted;

    static bool ReadDotKtEmittedAssembly(Assembly asm)
    {
        try
        {
            var marked = asm.GetCustomAttributesData().Any(c =>
                c.AttributeType.FullName == DotKtAssemblyMarkerAttr
                && c.ConstructorArguments.Count == 2
                && c.ConstructorArguments[0].Value as string == DotKtAssemblyMarkerKey
                && c.ConstructorArguments[1].Value as string == DotKtAssemblyMarkerValue);
            var carrier = asm.GetType(KotlinFileClassAttr, throwOnError: false, ignoreCase: false);
            return marked && carrier != null && carrier.GetCustomAttributesData()
                .Any(c => c.AttributeType.FullName == CompilerGeneratedAttr);
        }
        catch
        {
            // Classification is an ownership routing hint. An unreadable marker is not authority to reinterpret a
            // foreign assembly as Kotlin; the normal CLR path will surface any actually required metadata failure.
            return false;
        }
    }

    static Dictionary<Type, bool> ValidateCompanionCarriers(
        Type[] types,
        Assembly assembly,
        out Dictionary<string, string> singletonCompanionCarriers,
        Dictionary<string, string> companionCarriersByPhysicalOwner,
        Dictionary<string, string> companionSourceNamesByPhysicalOwner,
        Dictionary<string, string> companionPhysicalOwnerBySemanticType,
        Dictionary<string, string> companionSemanticOwnerByCarrier,
        Dictionary<Type, string> companionSourcePaths)
    {
        var physicalTypes = types
            .GroupBy(t => (Name: PhysicalMetadataName(t), Arity: DeclaredGenericArity(t)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var semanticOwners = new Dictionary<string, Type>(StringComparer.Ordinal);
        var claimedPhysicalOwners = new HashSet<Type>();
        var result = new Dictionary<Type, bool>();
        singletonCompanionCarriers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var carrierType in types)
        {
            CustomAttributeData carrierAttribute;
            try
            {
                var carrierAttributes = carrierType.GetCustomAttributesData().Where(c =>
                    c.AttributeType.FullName == KotlinCompanionAttr &&
                    c.AttributeType.Assembly == assembly &&
                    HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr))
                    .ToArray();
                if (carrierAttributes.Length > 1)
                    throw new MalformedTrustedCompanionException(
                        $"duplicate trusted [KotlinCompanion] carriers on '{carrierType.FullName}'");
                carrierAttribute = carrierAttributes.SingleOrDefault();
            }
            catch (MalformedTrustedCompanionException) { throw; }
            catch (Exception ex)
            {
                throw new MalformedTrustedCompanionException(
                    $"could not inspect trusted [KotlinCompanion] on '{carrierType.FullName}'", ex);
            }
            if (carrierAttribute == null) continue;

            JsonObject payload;
            try
            {
                if (carrierAttribute.ConstructorArguments.Count != 2 ||
                    carrierAttribute.ConstructorArguments[0].Value is not string version)
                    throw new FormatException("expected (version, byte[]) constructor arguments");
                if (carrierAttribute.NamedArguments.Count != 0)
                    throw new FormatException("named arguments are forbidden");
                payload = BirCarrier.DecodeBody(version, ReadByteArrayArg(carrierAttribute.ConstructorArguments[1]))
                    as JsonObject ?? throw new FormatException("payload must be an object");
            }
            catch (Exception ex)
            {
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': {ex.Message}", ex);
            }

            string RequiredString(string property)
            {
                if (payload[property] is not JsonValue value ||
                    !value.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text))
                    throw new MalformedTrustedCompanionException(
                        $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': '{property}' must be a non-empty string");
                return text;
            }
            var kind = RequiredString("kind");
            var owner = RequiredString("owner");
            var name = RequiredString("name");
            var visibility = RequiredString("visibility");
            var physicalOwner = RequiredString("physicalOwner");
            if (kind is not ("nested" or "sidecar"))
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid kind '{kind}'");
            if (!IsSemanticQualifiedName(owner) || !IsSemanticNameSegment(name))
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid semantic owner/name");
            if (visibility is not ("public" or "internal" or "private" or "protected" or "protectedInternal"))
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid visibility '{visibility}'");
            if (payload["physicalOwnerArity"] is not JsonValue arityValue ||
                !arityValue.TryGetValue<int>(out var physicalOwnerArity) || physicalOwnerArity < 0)
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid physicalOwnerArity");
            if (!physicalTypes.TryGetValue((physicalOwner, physicalOwnerArity), out var ownerMatches) ||
                ownerMatches.Length != 1)
                throw new MalformedTrustedCompanionException(
                    $"trusted [KotlinCompanion] owner '{physicalOwner}' arity {physicalOwnerArity} resolved to " +
                    $"{(ownerMatches is null ? 0 : ownerMatches.Length)} physical types");
            var ownerType = ownerMatches[0];
            if (!claimedPhysicalOwners.Add(ownerType))
                throw new MalformedTrustedCompanionException(
                    $"multiple trusted [KotlinCompanion] carriers name owner '{owner}'");
            if (!semanticOwners.TryAdd(owner, ownerType) && semanticOwners[owner] != ownerType)
                throw new MalformedTrustedCompanionException(
                    $"multiple physical types claim Kotlin companion owner '{owner}'");

            // A companion is nested in its physical owner exactly when that owner is non-generic; a generic owner's
            // carrier is hoisted beside it so the singleton cannot multiply across closed instantiations. Either way
            // the carrier itself declares no generic slot, so one `$INSTANCE` exists per Kotlin companion.
            if (kind == "sidecar")
            {
                if (physicalOwnerArity == 0)
                    throw new MalformedTrustedCompanionException(
                        "hoisted trusted [KotlinCompanion] requires a generic physical owner");
                if (carrierType.DeclaringType != null)
                    throw new MalformedTrustedCompanionException(
                        "hoisted trusted [KotlinCompanion] must be a top-level type");
                if (!carrierType.IsPublic)
                    throw new MalformedTrustedCompanionException(
                        "hoisted trusted [KotlinCompanion] carrier must be public");
            }
            else
            {
                if (physicalOwnerArity != 0)
                    throw new MalformedTrustedCompanionException(
                        "nested trusted [KotlinCompanion] requires a non-generic physical owner");
                if (carrierType == ownerType || carrierType.DeclaringType != ownerType)
                    throw new MalformedTrustedCompanionException(
                        "nested trusted [KotlinCompanion] must be an ordinary nested type of its physical owner");
                if (!carrierType.IsNestedPublic)
                    throw new MalformedTrustedCompanionException(
                        "nested trusted [KotlinCompanion] carrier must have NestedPublic visibility");
            }
            if (carrierType.GetGenericArguments().Length != 0)
                throw new MalformedTrustedCompanionException(
                    "trusted [KotlinCompanion] carrier must declare no generic parameters");
            if (!HasTrustedMarker(carrierType, assembly, "DotKt.Runtime.CompilerServices.KotlinObjectAttribute"))
                throw new MalformedTrustedCompanionException(
                    "trusted [KotlinCompanion] requires trusted [KotlinObject]");
            FieldInfo[] instances;
            try
            {
                instances = carrierType.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(f => f.Name == "$INSTANCE" && f.IsPublic && IsOpenSelfType(f.FieldType, carrierType))
                    .ToArray();
            }
            catch (Exception ex)
            {
                throw new MalformedTrustedCompanionException(
                    $"could not validate [KotlinCompanion] carrier '{carrierType.FullName}'", ex);
            }
            if (instances.Length != 1)
                throw new MalformedTrustedCompanionException(
                    "trusted [KotlinCompanion] requires one public static self-typed $INSTANCE field");

            companionSemanticOwnerByCarrier.Add(
                StripGenericArity(DottedFqn(carrierType.FullName ?? carrierType.Name)),
                StripGenericArity(owner));
            companionSourcePaths.Add(carrierType, owner + "." + name);

            // Private/internal companions do not participate in downstream call binding. Public and protected
            // companions do; the carrier remains public enough for lifted helper types while the payload restores
            // Kotlin visibility. Every carrier was still validated above so malformed compiler-owned metadata cannot
            // silently alter the scan.
            if (visibility is "public" or "protected" &&
                IsPubliclyVisible(ownerType) && IsPubliclyVisible(carrierType))
            {
                result.Add(carrierType, false);
                var semanticType = StripGenericArity(owner) + ".<companion:" + name + ">";
                companionPhysicalOwnerBySemanticType.Add(semanticType,
                    carrierType.FullName ?? carrierType.Name);
                singletonCompanionCarriers.Add(
                    StripGenericArity(owner),
                    carrierType.FullName ?? carrierType.Name);
                companionCarriersByPhysicalOwner.Add(
                    StripGenericArity(DottedFqn(physicalOwner)),
                    carrierType.FullName ?? carrierType.Name);
                companionSourceNamesByPhysicalOwner.Add(
                    StripGenericArity(DottedFqn(physicalOwner)), name);
            }
        }
        return result;
    }

    static bool IsOpenSelfType(Type fieldType, Type carrierType)
    {
        if (!carrierType.IsGenericTypeDefinition) return fieldType == carrierType;
        if (!fieldType.IsGenericType || fieldType.GetGenericTypeDefinition() != carrierType) return false;
        var args = fieldType.GetGenericArguments();
        return args.Length == carrierType.GetGenericArguments().Length &&
            args.Select((arg, index) => arg.IsGenericParameter &&
                arg.DeclaringMethod == null && arg.GenericParameterPosition == index).All(x => x);
    }

    static readonly char[] ForbiddenSemanticNameCharacters = ['.', '/', '\\', '<', '>', ':', '[', ']', '$'];

    static bool IsSemanticNameSegment(string value) =>
        value.Length != 0 && value.IndexOfAny(ForbiddenSemanticNameCharacters) < 0 &&
        !value.Any(char.IsControl);

    static bool IsSemanticQualifiedName(string value) =>
        value.Split('.', StringSplitOptions.None).All(IsSemanticNameSegment);

    static bool HasTrustedMarker(Type type, Assembly assembly, string marker) =>
        type.GetCustomAttributesData().Any(c => c.AttributeType.FullName == marker &&
            c.AttributeType.Assembly == assembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr));

    static bool IsPubliclyVisible(Type type) => type.IsNested
        ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem) &&
            type.DeclaringType != null && IsPubliclyVisible(type.DeclaringType)
        : type.IsPublic;

    static int DeclaredGenericArity(Type type)
    {
        if (!type.IsGenericType) return 0;
        var total = type.GetGenericArguments().Length;
        var inherited = type.DeclaringType is { IsGenericType: true } parent
            ? parent.GetGenericArguments().Length : 0;
        return total - inherited;
    }

    static string PhysicalMetadataName(Type type)
    {
        var simple = StripGenericArity(type.Name);
        if (type.DeclaringType != null) return PhysicalMetadataName(type.DeclaringType) + "+" + simple;
        return string.IsNullOrEmpty(type.Namespace) ? simple : type.Namespace + "." + simple;
    }

    internal static string ExactPhysicalMetadataName(Type type)
    {
        if (type.DeclaringType != null) return ExactPhysicalMetadataName(type.DeclaringType) + "+" + type.Name;
        return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
    }

    static BirConstructorAdapterPayload AliasConstructorAdapterOf(
        IList<CustomAttributeData> attributes, Assembly declaringAssembly)
    {
        if (declaringAssembly == null) return null;
        var carrier = attributes.FirstOrDefault(candidate =>
            candidate.AttributeType.FullName == KotlinConstructorAdapterAttr
            && candidate.AttributeType.Assembly == declaringAssembly
            && HasAttribute(candidate.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr));
        if (carrier == null) return null;
        try
        {
            if (carrier.ConstructorArguments.Count != 2)
                throw new FormatException("constructor-adapter carrier does not have (version, bytes)");
            var version = carrier.ConstructorArguments[0].Value as string
                ?? throw new FormatException("constructor-adapter carrier version is not a string");
            return ConstructorAdapterCarrier.Decode(version, ReadByteArrayArg(carrier.ConstructorArguments[1]));
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("malformed trusted constructor-adapter metadata", ex);
        }
    }


    // A reflected byte[] ctor argument materializes under MetadataLoadContext as an IReadOnlyList<CustomAttributeTypedArgument>
    // (each element's .Value a boxed byte), not a byte[] — reify it (mirrors ilemit Emitter.CompilerServices.ReadByteArrayArg).
    static byte[] ReadByteArrayArg(CustomAttributeTypedArgument a)
    {
        if (a.Value is byte[] b) return b;
        if (a.Value is IReadOnlyList<CustomAttributeTypedArgument> arr)
        {
            var r = new byte[arr.Count];
            for (int i = 0; i < arr.Count; i++) r[i] = (byte)arr[i].Value!;
            return r;
        }
        throw new FormatException("carrier content is not a byte[]");
    }

    static bool HasAttribute(IList<CustomAttributeData> attrs, string fullName) =>
        attrs.Any(a => a.AttributeType.FullName == fullName);

    // Decode a compiler-owned round-trip [KotlinType] carrier.
    internal static TypeNode KotlinTypeOf(IList<CustomAttributeData> attrs, Assembly declaringAssembly) =>
        CarrierTypeOf(attrs, declaringAssembly, KotlinTypeAttr);

    // Decode a compiler-owned round-trip TypeNode carrier (`[KotlinType]`, `[KotlinNullableGeneric]` — both ride the
    // same explicitly declared TypeNode codec envelope). Full-name equality is insufficient: a foreign
    // assembly may define a lookalike. Require the containing assembly's DotKt marker + generated
    // carrier test here too: physical field/signature readers also call this outside the initial scan. Require
    // this embedded attribute type to come from that assembly and itself carry
    // [CompilerGenerated], matching dll2klib's provenance rule.
    static TypeNode CarrierTypeOf(IList<CustomAttributeData> attrs, Assembly declaringAssembly, string attrFullName)
    {
        if (!IsDotKtEmittedAssembly(declaringAssembly)) return null;
        foreach (var cad in attrs)
        {
            string codec;
            byte[] content;
            try
            {
                if (cad.AttributeType.FullName != attrFullName
                    || cad.AttributeType.Assembly != declaringAssembly
                    || !HasAttribute(cad.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)
                    || cad.ConstructorArguments.Count != 2
                    || cad.ConstructorArguments[0].Value is not string declaredCodec)
                    continue;
                codec = declaredCodec;
                content = ReadByteArrayArg(cad.ConstructorArguments[1]);
            }
            catch
            {
                // Reflection/provenance probing cannot authenticate this candidate.
                continue;
            }
            // An authenticated compiler carrier owns its explicitly declared
            // codec. Decoder failures must not select a reflected fallback.
            try { return TypeNodeCarrier.Decode(codec, content); }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                // Both surrounding scan boundaries propagate InvalidDataException.
                // Authenticated payload failures must not leave a partial index.
                throw new InvalidDataException("malformed trusted TypeNode carrier", ex);
            }
        }
        return null;
    }

    static JsonNode CarrierJsonOf(
        IList<CustomAttributeData> attrs,
        Assembly declaringAssembly,
        string attrFullName)
    {
        if (declaringAssembly == null) return null;
        var cad = attrs.FirstOrDefault(c =>
            c.AttributeType.FullName == attrFullName &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr));
        if (cad == null || cad.ConstructorArguments.Count != 2 ||
            cad.ConstructorArguments[0].Value is not string version)
            return null;
        return BirCarrier.DecodeBody(version, ReadByteArrayArg(cad.ConstructorArguments[1]));
    }

    static BirTypeParameter[] SemanticMethodTypeParameters(MethodInfo method, bool dotKtAuthored,
        NullableRepresentationFrame frame)
    {
        var physicalParameters = method.GetGenericArguments();
        var parameters = (frame == null
            ? physicalParameters
            : Enumerable.Range(0, frame.SourceArity)
                .Select(source => physicalParameters[frame.SourcePosition(source)]))
            .Select(GenericParamSourceDeclaration).ToArray();
        if (!dotKtAuthored || CarrierJsonOf(method.GetCustomAttributesData(), method.DeclaringType?.Assembly,
                "DotKt.Runtime.CompilerServices.KotlinTypeParameterBoundsAttribute") is not JsonObject payload
            || payload["bounds"] is not JsonObject bounds) return parameters;
        foreach (var pair in bounds)
        {
            var index = int.Parse(pair.Key);
            parameters[index].Constraints = pair.Value.AsArray().Select(TypeJson.Read).ToArray();
        }
        return parameters;
    }

    static void RestoreKotlinTypeParameterVariances(
        Type type, Assembly declaringAssembly, BirTypeParameter[] declarations)
    {
        if (CarrierJsonOf(type.GetCustomAttributesData(), declaringAssembly, KotlinSupertypesAttr)
                is not JsonObject payload
            || payload["variances"] is null)
            return;
        if (payload["variances"] is not JsonObject variances || variances.Count == 0)
            throw new InvalidDataException("malformed trusted [KotlinSupertypes] variances");
        var frame = payload[NullableRepresentationFrame.MetadataKey] is JsonNode frameNode
            ? ReadNullableFrame(frameNode, declarations.Length) : null;
        var seen = new HashSet<int>();
        foreach (var entry in variances)
        {
            if (!int.TryParse(entry.Key, out var index) || index < 0
                || index >= (frame?.SourceArity ?? declarations.Length)
                || !seen.Add(index)
                || (entry.Value as JsonValue)?.TryGetValue<string>(out var variance) != true
                || variance is not ("in" or "out"))
                throw new InvalidDataException("malformed trusted [KotlinSupertypes] variance entry");
            var physicalIndex = frame?.SourcePosition(index) ?? index;
            var declaration = declarations[physicalIndex]
                ?? throw new InvalidDataException("missing physical type parameter declaration");
            declaration.Variance = variance;
        }
    }

    static void IndexCompanionExtensionMembers(
        Type type, string ownerFqn, bool isFileClass, ReferenceDotKtMetadata metadata)
    {
        // Constructors share MethodDef as their ECMA-335 parent kind, but reflection deliberately excludes them from
        // GetMethods.  Inspect both instance constructors and the type initializer explicitly so the trusted-carrier
        // validator accepts the same physical member set as dll2klib's MetadataReader walk.  No constructor is an
        // ordinary Kotlin companion-extension function, even when its carrier payload is otherwise well-formed.
        foreach (var constructor in type.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (CompanionExtensionPayload(constructor.GetCustomAttributesData(), type.Assembly) != null)
                throw new InvalidDataException(
                    $"malformed trusted [KotlinCompanionExtension] on constructor '{type.FullName}.{constructor.Name}'");
        if (type.TypeInitializer is ConstructorInfo typeInitializer &&
            CompanionExtensionPayload(typeInitializer.GetCustomAttributesData(), type.Assembly) != null)
            throw new InvalidDataException(
                $"malformed trusted [KotlinCompanionExtension] on constructor '{type.FullName}.{typeInitializer.Name}'");

        foreach (var method in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly))
        {
            var payload = CompanionExtensionPayload(method.GetCustomAttributesData(), type.Assembly);
            if (payload == null) continue;
            if (!isFileClass || !method.IsStatic || method.IsSpecialName || payload.Kind == "field")
                throw new InvalidDataException(
                    $"malformed trusted [KotlinCompanionExtension] on method '{type.FullName}.{method.Name}'");
            AddCompanionExtensionMember(metadata, ownerFqn, payload, method.Name);
        }

        foreach (var field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly))
        {
            var payload = CompanionExtensionPayload(field.GetCustomAttributesData(), type.Assembly);
            if (payload == null) continue;
            if (!isFileClass || !field.IsStatic || payload.Kind != "field")
                throw new InvalidDataException(
                    $"malformed trusted [KotlinCompanionExtension] on field '{type.FullName}.{field.Name}'");
            AddCompanionExtensionMember(metadata, ownerFqn, payload, field.Name);
        }
    }

    static void IndexCSharp14StaticExtensionMembers(
        Type container,
        string ownerFqn,
        ReferenceDotKtMetadata metadata,
        bool dotKtAuthored)
    {
        const string extensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
        const string markerAttribute = "System.Runtime.CompilerServices.ExtensionMarkerAttribute";
        if (container.DeclaringType != null || !container.IsAbstract || !container.IsSealed ||
            !HasAttribute(container.GetCustomAttributesData(), extensionAttribute))
            return;

        foreach (var group in container.GetNestedTypes(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var declarations = group.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>()
                .Concat(group.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(member => (Member: member, Markers: CSharp14MarkerNames(member, markerAttribute)))
                .Where(entry => entry.Markers.Length != 0)
                .ToArray();
            if (declarations.Length == 0) continue;
            if (!group.IsSpecialName || !HasAttribute(group.GetCustomAttributesData(), extensionAttribute))
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': invalid grouping type");
            var markerNames = declarations.SelectMany(entry => entry.Markers)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (markerNames.Length != 1)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': ambiguous receiver marker");
            var markerMatches = group.GetNestedTypes(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(type => type.Name == markerNames[0]).ToArray();
            if (markerMatches.Length != 1)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': receiver marker does not resolve");
            var marker = markerMatches[0];
            var markerMethods = marker.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "<Extension>$").ToArray();
            if (markerMethods.Length != 1 || !marker.IsSpecialName || !marker.IsAbstract || !marker.IsSealed)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{marker.FullName}': invalid receiver marker");
            var receiverMarker = markerMethods[0];
            var markerIsGenerated = HasAttribute(receiverMarker.GetCustomAttributesData(), CompilerGeneratedAttr);
            var markerBodyIsSignatureOnly = CSharp14MarkerBodyIsSignatureOnly(receiverMarker);
            if (!receiverMarker.IsStatic || !receiverMarker.IsSpecialName ||
                receiverMarker.ReturnType.FullName != "System.Void" ||
                receiverMarker.GetGenericArguments().Length != 0 || receiverMarker.GetParameters().Length != 1 ||
                !markerIsGenerated || !markerBodyIsSignatureOnly)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{marker.FullName}': invalid marker method " +
                    $"(static={receiverMarker.IsStatic}, special={receiverMarker.IsSpecialName}, " +
                    $"return={receiverMarker.ReturnType.FullName}, generic={receiverMarker.GetGenericArguments().Length}, " +
                    $"params={receiverMarker.GetParameters().Length}, generated={markerIsGenerated}, " +
                    $"signatureOnly={markerBodyIsSignatureOnly})");
            var blockParameters = group.GetGenericArguments();
            if (!CSharp14GenericParametersMatch(
                    blockParameters, marker.GetGenericArguments(), implementationBlockArity: 0))
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{marker.FullName}': marker constraints differ");

            var receiverName = CSharp14KotlinClassifier(receiverMarker.GetParameters()[0].ParameterType);
            var receiverJson = TypeJson.Fqn(receiverName).ToJsonString();
            var propertyAccessors = new HashSet<MethodInfo>();
            foreach (var property in group.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var propertyMarkers = CSharp14MarkerNames(property, markerAttribute);
                var accessors = property.GetAccessors(nonPublic: true);
                if (propertyMarkers.Length == 0)
                {
                    if (accessors.Any(accessor => CSharp14MarkerNames(accessor, markerAttribute).Length != 0))
                        throw new InvalidDataException(
                            $"malformed C# 14 static extension graph at '{group.FullName}': unmarked Property row");
                    continue;
                }
                if (propertyMarkers.Length != 1 || propertyMarkers[0] != marker.Name || accessors.Length == 0 ||
                    accessors.Any(accessor =>
                        CSharp14MarkerNames(accessor, markerAttribute) is not [var value] || value != marker.Name))
                    throw new InvalidDataException(
                        $"malformed C# 14 static extension graph at '{group.FullName}': inconsistent property markers");
                foreach (var accessor in accessors) propertyAccessors.Add(accessor);
                var getter = property.GetMethod ?? throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': property has no getter");
                if (!getter.IsStatic) continue;
                var getterImplementation = CSharp14Implementation(container, group, getter, blockParameters.Length);
                getterImplementation = CSharp14KotlinImplementation(
                    container, group, getter, getterImplementation, dotKtAuthored);
                AddCompanionExtensionMember(metadata, ownerFqn,
                    new CompanionExtensionPayloadInfo(receiverJson, property.Name, "get"),
                    getterImplementation.Name);
                if (property.SetMethod is { } setter)
                {
                    var setterImplementation = CSharp14Implementation(container, group, setter, blockParameters.Length);
                    setterImplementation = CSharp14KotlinImplementation(
                        container, group, setter, setterImplementation, dotKtAuthored);
                    AddCompanionExtensionMember(metadata, ownerFqn,
                        new CompanionExtensionPayloadInfo(receiverJson, property.Name, "set"),
                        setterImplementation.Name);
                }
            }

            foreach (var declaration in group.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var declarationMarkers = CSharp14MarkerNames(declaration, markerAttribute);
                if (declarationMarkers.Length != 1 || declarationMarkers[0] != marker.Name)
                    throw new InvalidDataException(
                        $"malformed C# 14 static extension graph at '{group.FullName}': unmarked callable declaration");
                if (propertyAccessors.Contains(declaration)) continue;
                if (!declaration.IsStatic) continue;
                var implementation = CSharp14Implementation(container, group, declaration, blockParameters.Length);
                implementation = CSharp14KotlinImplementation(
                    container, group, declaration, implementation, dotKtAuthored);
                AddCompanionExtensionMember(metadata, ownerFqn,
                    new CompanionExtensionPayloadInfo(receiverJson, declaration.Name, "function"),
                    implementation.Name);
            }
        }
    }

    static string[] CSharp14MarkerNames(MemberInfo member, string markerAttribute)
    {
        var attributes = member.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == markerAttribute)
            .ToArray();
        var result = new string[attributes.Length];
        for (var index = 0; index < attributes.Length; index++)
        {
            var attribute = attributes[index];
            if (attribute.ConstructorArguments.Count != 1 ||
                attribute.ConstructorArguments[0].Value is not string value ||
                string.IsNullOrEmpty(value) || attribute.NamedArguments.Count != 0)
                throw new InvalidDataException("malformed [ExtensionMarker] attribute");
            result[index] = value;
        }
        return result;
    }

    static MethodInfo CSharp14Implementation(
        Type container,
        Type group,
        MethodInfo declaration,
        int blockArity)
    {
        if (!CSharp14DeclarationBodyIsSignatureOnly(declaration))
            throw new InvalidDataException(
                $"malformed C# 14 static extension graph at '{group.FullName}': declaration '{declaration.Name}' is callable");
        var matches = container.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(candidate => candidate.Name == declaration.Name && !candidate.IsSpecialName)
            .Where(candidate => CSharp14SignaturesMatch(declaration, candidate, blockArity))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"malformed C# 14 static extension graph at '{group.FullName}': declaration '{declaration.Name}' " +
                $"resolves to {matches.Length} implementations");
        return matches[0];
    }

    static MethodInfo CSharp14KotlinImplementation(
        Type container,
        Type group,
        MethodInfo declaration,
        MethodInfo wrapper,
        bool dotKtAuthored)
    {
        // KotlinExtensionCore is an internal DotKt edge, not part of the standard C# 14 graph. A foreign assembly may
        // legally define a same-named attribute; without the assembly marker it has no authority to redirect Kotlin
        // calls away from the standard implementation. Keep this trust boundary aligned with dll2klib's raw reader.
        if (!dotKtAuthored) return wrapper;
        var coreName = KotlinExtensionCoreName(wrapper.GetCustomAttributesData(), container.Assembly);
        if (coreName == null) return wrapper;
        var matches = container.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(candidate => candidate.Name == coreName && !candidate.IsSpecialName)
            .Where(candidate => CSharp14CoreSignatureMatches(declaration, candidate))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"malformed C# 14 static extension graph at '{group.FullName}': Kotlin core '{coreName}' " +
                $"resolves to {matches.Length} methods");
        return matches[0];
    }

    static bool CSharp14CoreSignatureMatches(MethodInfo declaration, MethodInfo core)
    {
        if (!core.IsStatic ||
            (core.Attributes & MethodAttributes.MemberAccessMask) !=
                (declaration.Attributes & MethodAttributes.MemberAccessMask) ||
            core.GetGenericArguments().Length != declaration.GetGenericArguments().Length ||
            CSharp14TypeKey(declaration.ReturnType, implementation: false, blockArity: 0) !=
                CSharp14TypeKey(core.ReturnType, implementation: true, blockArity: 0))
            return false;
        var declarationParameters = declaration.GetParameters();
        var coreParameters = core.GetParameters();
        if (declarationParameters.Length != coreParameters.Length) return false;
        for (var index = 0; index < declarationParameters.Length; index++)
            if (CSharp14TypeKey(declarationParameters[index].ParameterType, implementation: false, blockArity: 0) !=
                CSharp14TypeKey(coreParameters[index].ParameterType, implementation: true, blockArity: 0))
                return false;
        return CSharp14GenericParametersMatch(
            declaration.GetGenericArguments(), core.GetGenericArguments(), implementationBlockArity: 0);
    }

    static bool CSharp14SignaturesMatch(MethodInfo declaration, MethodInfo implementation, int blockArity)
    {
        if ((declaration.Attributes & MethodAttributes.MemberAccessMask) !=
                (implementation.Attributes & MethodAttributes.MemberAccessMask) ||
            implementation.GetGenericArguments().Length != blockArity + declaration.GetGenericArguments().Length ||
            CSharp14TypeKey(declaration.ReturnType, implementation: false, blockArity) !=
                CSharp14TypeKey(implementation.ReturnType, implementation: true, blockArity))
            return false;
        var declarationParameters = declaration.GetParameters();
        var implementationParameters = implementation.GetParameters();
        if (declarationParameters.Length != implementationParameters.Length) return false;
        for (var index = 0; index < declarationParameters.Length; index++)
            if (CSharp14TypeKey(declarationParameters[index].ParameterType, implementation: false, blockArity) !=
                CSharp14TypeKey(implementationParameters[index].ParameterType, implementation: true, blockArity))
                return false;
        var declarationGeneric = declaration.GetGenericArguments();
        var implementationGeneric = implementation.GetGenericArguments();
        return CSharp14GenericParametersMatch(
                declaration.DeclaringType!.GetGenericArguments(),
                implementationGeneric.Take(blockArity).ToArray(),
                blockArity) &&
            CSharp14GenericParametersMatch(
                declarationGeneric,
                implementationGeneric.Skip(blockArity).ToArray(),
                blockArity);
    }

    static bool CSharp14GenericParametersMatch(
        Type[] declarations,
        Type[] implementations,
        int implementationBlockArity)
    {
        if (declarations.Length != implementations.Length) return false;
        for (var index = 0; index < declarations.Length; index++)
        {
            if (declarations[index].GenericParameterAttributes != implementations[index].GenericParameterAttributes)
                return false;
            var left = declarations[index].GetGenericParameterConstraints()
                .Select(type => CSharp14TypeKey(type, implementation: false, implementationBlockArity))
                .OrderBy(value => value, StringComparer.Ordinal);
            var right = implementations[index].GetGenericParameterConstraints()
                .Select(type => CSharp14TypeKey(type, implementation: implementationBlockArity != 0,
                    implementationBlockArity))
                .OrderBy(value => value, StringComparer.Ordinal);
            if (!left.SequenceEqual(right, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    static string CSharp14TypeKey(Type type, bool implementation, int blockArity)
    {
        if (type.IsByRef) return "byref<" + CSharp14TypeKey(type.GetElementType()!, implementation, blockArity) + ">";
        if (type.IsPointer) return "ptr<" + CSharp14TypeKey(type.GetElementType()!, implementation, blockArity) + ">";
        if (type.IsArray) return $"array:{type.GetArrayRank()}<" +
            CSharp14TypeKey(type.GetElementType()!, implementation, blockArity) + ">";
        if (type.IsGenericParameter)
        {
            var index = type.GenericParameterPosition;
            if (!implementation)
                return type.DeclaringMethod is null ? $"!{index}" : $"!!{index}";
            return index < blockArity ? $"!{index}" : $"!!{index - blockArity}";
        }
        if (type.IsConstructedGenericType)
            return StripGenericArity(type.GetGenericTypeDefinition().FullName ?? type.Name) + "<" +
                string.Join(",", type.GetGenericArguments().Select(argument =>
                    CSharp14TypeKey(argument, implementation, blockArity))) + ">";
        return type.FullName ?? type.Name;
    }

    static string CSharp14KotlinClassifier(Type receiver)
    {
        if (receiver.IsConstructedGenericType) receiver = receiver.GetGenericTypeDefinition();
        return PrimitiveBirName(receiver) switch
        {
            "bool" => "kotlin.Boolean",
            "sbyte" => "kotlin.Byte",
            "byte" => "kotlin.UByte",
            "char" => "kotlin.Char",
            "double" => "kotlin.Double",
            "float" => "kotlin.Float",
            "int" => "kotlin.Int",
            "long" => "kotlin.Long",
            "object" => "kotlin.Any",
            "short" => "kotlin.Short",
            "string" => "kotlin.String",
            "ushort" => "kotlin.UShort",
            "uint" => "kotlin.UInt",
            "ulong" => "kotlin.ULong",
            _ => DottedFqn(StripGenericArity(receiver.FullName ?? receiver.Name)),
        };
    }

    static bool CSharp14MarkerBodyIsSignatureOnly(MethodInfo method)
    {
        byte[] body;
        try { body = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (BadImageFormatException) { return true; }
        return body is null or [0x2A] || CSharp14KnownThrowStub(body);
    }

    static bool CSharp14DeclarationBodyIsSignatureOnly(MethodInfo method)
    {
        byte[] body;
        try { body = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (BadImageFormatException) { return true; }
        return body is null || CSharp14KnownThrowStub(body);
    }

    static bool CSharp14KnownThrowStub(byte[] body) =>
        body is [0x14, 0x7A] ||
        body.Length == 6 && body[0] == 0x73 && body[^1] == 0x7A;

    sealed record CompanionExtensionPayloadInfo(string ReceiverJson, string Name, string Kind);
    sealed record PropertyAccessorPayloadInfo(string Name, string Kind, string Association,
        string SourceAssociation);
    internal sealed record DeclarationIdentityPayloadInfo(string Id, string Name, TypeNode[] SemanticParams,
        TypeNode SemanticReturn, int[] SemanticReifiedTypeParameterIndices,
        int[] NullableWitnessTypeParameterIndices, NullableRepresentationFrame NullableFrame, bool SignatureAllocated,
        int[] ReflectedWitnessIndices = null);

    internal static DeclarationIdentityPayloadInfo KotlinDeclarationIdentityPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly, int methodGenericArity = int.MaxValue)
    {
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinDeclarationIdentityAttr) as JsonObject;
        if (payload == null) return null;
        return ParseDeclarationIdentityPayload(payload, methodGenericArity);
    }

    internal static DeclarationIdentityPayloadInfo ParseDeclarationIdentityPayload(JsonObject payload, int methodGenericArity)
    {
        if (payload.Count is < 2 or > 9 ||
            payload["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
            payload["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)
            || payload.Any(kv => kv.Key is not ("id" or "name" or "signature" or "reified" or "nullableWitness" or "reflectedWitness" or NullableRepresentationFrame.MetadataKey or DeclarationSignatureIdentity.AllocationMetadataKey or "constructorPrimary"))
            || payload.ContainsKey("constructorPrimary") &&
                (payload["constructorPrimary"] is not JsonValue primary || !primary.TryGetValue<bool>(out _))
            || payload[DeclarationSignatureIdentity.AllocationMetadataKey] is JsonNode allocation
                && (allocation is not JsonValue allocationValue || !allocationValue.TryGetValue<bool>(out var allocated) || !allocated)
            || payload["signature"] is JsonNode signature && signature is not JsonObject
            || payload["reified"] is JsonNode reifiedNode && reifiedNode is not JsonArray
            || payload["nullableWitness"] is JsonNode witnessNode && witnessNode is not JsonArray
            || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            throw new InvalidDataException(
                $"malformed [KotlinDeclarationIdentity] payload: {payload.ToJsonString()}");
        var nullableFrame = payload[NullableRepresentationFrame.MetadataKey] is JsonNode frameNode
            ? ReadNullableFrame(frameNode, methodGenericArity) : null;
        if (nullableFrame != null && payload["signature"] == null)
            throw new InvalidDataException("Nullable representation frame requires original Kotlin signature");
        var sourceArity = nullableFrame?.SourceArity ?? methodGenericArity;
        var reified = payload["reified"] is JsonArray reifiedArray
            ? reifiedArray.Select(node => node is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0
                ? index
                : throw new InvalidDataException("malformed [KotlinDeclarationIdentity] reified index"))
                .ToArray()
            : Array.Empty<int>();
        if (reified.Distinct().Count() != reified.Length)
            throw new InvalidDataException("duplicate [KotlinDeclarationIdentity] reified index");
        if (reified.Any(index => index >= sourceArity))
            throw new InvalidDataException("[KotlinDeclarationIdentity] reified index exceeds method generic arity");
        var nullableWitness = payload["nullableWitness"] is JsonArray witnessArray
            ? witnessArray.Select(node => node is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0
                ? index
                : throw new InvalidDataException(
                    "malformed [KotlinDeclarationIdentity] nullable-witness index"))
                .ToArray()
            : Array.Empty<int>();
        if (nullableWitness.Distinct().Count() != nullableWitness.Length)
            throw new InvalidDataException("duplicate [KotlinDeclarationIdentity] nullable-witness index");
        if (nullableWitness.Any(index => index >= sourceArity))
            throw new InvalidDataException(
                "[KotlinDeclarationIdentity] nullable-witness index exceeds method generic arity");
        TypeNode[] semanticParams = null;
        TypeNode semanticReturn = null;
        if (payload["signature"] is JsonObject semanticSignature)
        {
            if (semanticSignature.Count != 2
                || semanticSignature.Any(kv => kv.Key is not ("params" or "ret"))
                || semanticSignature["params"] is not JsonArray parameters
                || semanticSignature["ret"] is not JsonNode returnType)
                throw new InvalidDataException(
                    $"malformed [KotlinDeclarationIdentity] semantic signature: {semanticSignature.ToJsonString()}");
            try
            {
                semanticParams = parameters.Select(parameter => parameter == null
                        ? throw new FormatException("null parameter type")
                        : TypeNode.Parse(parameter.ToJsonString()))
                    .ToArray();
                semanticReturn = TypeNode.Parse(returnType.ToJsonString());
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException
                or KeyNotFoundException or JsonException)
            {
                throw new InvalidDataException(
                    $"malformed [KotlinDeclarationIdentity] semantic signature: {semanticSignature.ToJsonString()}", ex);
            }
        }
        var reflected = payload["reflectedWitness"]?.AsArray().Select(index => index.GetValue<int>()).ToArray();
        if (reflected != null && reflected.Except(nullableWitness).Any())
            throw new InvalidDataException("Reflected type witness has no method argument");
        return new DeclarationIdentityPayloadInfo(
            id, name, semanticParams, semanticReturn, reified, nullableWitness, nullableFrame,
            payload[DeclarationSignatureIdentity.AllocationMetadataKey]?.GetValue<bool>() == true, reflected);
    }

    internal static NullableRepresentationFrame ReadNullableFrame(JsonNode node, int physicalArity)
    {
        var frame = NullableRepresentationFrame.Read(node);
        if (physicalArity != int.MaxValue && frame.PhysicalArity != physicalArity)
            throw new InvalidDataException("Nullable representation frame disagrees with declared CLR generic arity");
        return frame;
    }

    static PropertyAccessorPayloadInfo KotlinPropertyAccessorPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinPropertyAccessorAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinPropertyAccessor]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinPropertyAccessorAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinPropertyAccessor] payload");
        if (payload.Count is < 3 or > 6 ||
            payload["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) ||
            payload["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind) ||
            payload["association"] is not JsonValue associationValue ||
                !associationValue.TryGetValue<string>(out var association) ||
            string.IsNullOrEmpty(name) || string.IsNullOrEmpty(association) || kind is not ("get" or "set"))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] payload");
        string sourceAssociation = null;
        if (payload["sourceAssociation"] is JsonValue sourceValue)
        {
            if (!sourceValue.TryGetValue<string>(out sourceAssociation) || string.IsNullOrEmpty(sourceAssociation))
                throw new InvalidDataException("malformed [KotlinPropertyAccessor] source association");
        }
        else if (payload.ContainsKey("sourceAssociation"))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] source association");
        string nativeEvent = null;
        if (payload.ContainsKey("event") && (kind != "get" || payload["event"] is not JsonValue eventValue ||
            !eventValue.TryGetValue<string>(out nativeEvent) || string.IsNullOrEmpty(nativeEvent)))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] event association");
        var inherited = payload.ContainsKey("inherited");
        if (inherited && (payload["inherited"] is not JsonValue inheritedValue ||
            !inheritedValue.TryGetValue<bool>(out var isInherited) || !isInherited))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] inherited declaration fact");
        if (payload.Count != 3 + (sourceAssociation != null ? 1 : 0) + (nativeEvent != null ? 1 : 0) + (inherited ? 1 : 0))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] payload");
        return new PropertyAccessorPayloadInfo(name, kind, association, sourceAssociation);
    }

    sealed record SourceMethodPayload(string Name, string SourceDeclaration);

    static SourceMethodPayload KotlinSourceMethodPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinSourceMethodAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinSourceMethod]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinSourceMethodAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinSourceMethod] payload");
        if (payload.Count is not (1 or 2) ||
            payload["name"] is not JsonValue nameValue ||
            !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name))
            throw new InvalidDataException("malformed [KotlinSourceMethod] payload");
        string sourceDeclaration = null;
        if (payload.Count == 2 && (payload["sourceDeclaration"] is not JsonValue source
            || !source.TryGetValue<string>(out sourceDeclaration) || string.IsNullOrEmpty(sourceDeclaration)))
            throw new InvalidDataException("malformed [KotlinSourceMethod] source declaration");
        return new SourceMethodPayload(name, sourceDeclaration);
    }

    sealed record InnerConstructorFactoryPayloadInfo(
        string Inner, string Declaration, int[] TypeArguments);

    static InnerConstructorFactoryPayloadInfo KotlinInnerConstructorFactoryPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinInnerConstructorFactoryAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinInnerConstructorFactory]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinInnerConstructorFactoryAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinInnerConstructorFactory] payload");
        if (payload.Count != 3
            || payload["inner"] is not JsonValue innerValue
            || !innerValue.TryGetValue<string>(out var inner) || string.IsNullOrEmpty(inner)
            || payload["declaration"] is not JsonValue declarationValue
            || !declarationValue.TryGetValue<string>(out var declaration) || string.IsNullOrEmpty(declaration)
            || payload["typeArgs"] is not JsonArray typeArguments)
            throw new InvalidDataException("malformed [KotlinInnerConstructorFactory] payload");
        var parsedTypeArguments = typeArguments.Select(argument =>
            argument is JsonValue value && value.TryGetValue<int>(out var position) ? position : int.MinValue).ToArray();
        if (parsedTypeArguments.Any(position =>
                position < FBoundStarProjectionErasure.InnerFactoryBottomTypeArgument))
            throw new InvalidDataException("malformed [KotlinInnerConstructorFactory] parameter descriptor");
        return new InnerConstructorFactoryPayloadInfo(inner, declaration, parsedTypeArguments);
    }

    static string KotlinExtensionCoreName(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinExtensionCoreAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinExtensionCore]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinExtensionCoreAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinExtensionCore] payload");
        if (payload.Count != 1 ||
            payload["name"] is not JsonValue nameValue ||
            !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name))
            throw new InvalidDataException("malformed [KotlinExtensionCore] payload");
        return name;
    }

    static CompanionExtensionPayloadInfo CompanionExtensionPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinCompanionExtensionAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinCompanionExtension]");

        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinCompanionExtensionAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinCompanionExtension] payload");
        if (payload.Count != 3 || payload["receiver"] == null ||
            payload["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) ||
            payload["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind) ||
            string.IsNullOrEmpty(name) || kind is not ("function" or "get" or "set" or "field"))
            throw new InvalidDataException("malformed [KotlinCompanionExtension] payload");
        if (TypeJson.Read(payload["receiver"]) is not TypeNode.Fqn { Args: null } receiverType)
            throw new InvalidDataException("companion-extension receiver is not a bare classifier type");
        var receiver = TypeJson.Write(receiverType).ToJsonString();
        return new CompanionExtensionPayloadInfo(receiver, name, kind);
    }

    static void AddCompanionExtensionMember(
        ReferenceDotKtMetadata metadata,
        string owner,
        CompanionExtensionPayloadInfo payload,
        string physicalName)
    {
        var key = CompanionExtensionKey(owner, payload.ReceiverJson, payload.Kind, payload.Name);
        if (!metadata.CompanionExtensionMembers.TryAdd(key, physicalName) &&
            metadata.CompanionExtensionMembers[key] != physicalName)
            // The source-name index is intentionally incomplete: Kotlin overloads may erase to distinct physical
            // names under #395. Mark that structural key ambiguous; frontend-selected declaration identity remains
            // the authoritative cross-module binding and name-only consumers fail closed.
            metadata.CompanionExtensionMembers[key] = "";
    }

    static BasicEnumMetadata BasicEnumMetadataOf(Type type, Assembly declaringAssembly)
    {
        var trusted = type.GetCustomAttributesData().Where(attribute =>
            attribute.AttributeType.FullName == KotlinBasicEnumAttr &&
            attribute.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(attribute.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinBasicEnum]");
        var payload = CarrierJsonOf(type.GetCustomAttributesData(), declaringAssembly, KotlinBasicEnumAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinBasicEnum] payload");
        if (!type.IsEnum || type.IsGenericType || payload.Count != 2 ||
            payload["underlying"] is not JsonValue underlyingValue ||
            !underlyingValue.TryGetValue<string>(out var underlying) || string.IsNullOrEmpty(underlying) ||
            payload["entries"] is not JsonArray entries || type.GetEnumUnderlyingType().FullName != underlying)
            throw new InvalidDataException(
                $"malformed [KotlinBasicEnum] on '{type.FullName}': expected a non-generic enum, exact underlying type, and entries");

        var ordered = new List<BasicEnumEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var physicalValues = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            if (entries[ordinal] is not JsonObject entry || entry.Count != 3 ||
                entry["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name) || !names.Add(name) ||
                entry["ordinal"] is not JsonValue ordinalValue ||
                !ordinalValue.TryGetValue<int>(out var declaredOrdinal) || declaredOrdinal != ordinal ||
                entry["physicalValue"] is not JsonValue physicalValue ||
                !physicalValue.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text) ||
                !physicalValues.Add(text))
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] on '{type.FullName}': entries require unique name/ordinal/value triples in declaration order");
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(field => field.Name == name && field.FieldType == type && field.IsLiteral).ToArray();
            if (fields.Length != 1 || EnumConstantText(fields[0].GetRawConstantValue()) != text)
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] on '{type.FullName}': entry '{name}' does not match its literal FieldDef");
            ordered.Add(new BasicEnumEntry(name, ordinal, text));
        }
        var literalNames = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral && field.FieldType == type)
            .Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (!literalNames.SetEquals(names))
            throw new InvalidDataException(
                $"malformed [KotlinBasicEnum] on '{type.FullName}': entry map does not match literal fields");
        return new BasicEnumMetadata(underlying, ordered);
    }

    internal static string EnumConstantText(object value) => value switch
    {
        char v => ((int)v).ToString(CultureInfo.InvariantCulture),
        sbyte v => v.ToString(CultureInfo.InvariantCulture),
        byte v => v.ToString(CultureInfo.InvariantCulture),
        short v => v.ToString(CultureInfo.InvariantCulture),
        ushort v => v.ToString(CultureInfo.InvariantCulture),
        int v => v.ToString(CultureInfo.InvariantCulture),
        uint v => v.ToString(CultureInfo.InvariantCulture),
        long v => v.ToString(CultureInfo.InvariantCulture),
        ulong v => v.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("malformed enum literal constant"),
    };

    static RichEnumMetadata RichEnumMetadataOf(Type type, Assembly declaringAssembly)
    {
        var trusted = type.GetCustomAttributesData().Where(attribute =>
            attribute.AttributeType.FullName == KotlinRichEnumAttr &&
            attribute.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(attribute.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinRichEnum]");
        var payload = CarrierJsonOf(type.GetCustomAttributesData(), declaringAssembly, KotlinRichEnumAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinRichEnum] payload");
        if (payload.Count != 5 || payload["entries"] is not JsonArray entries ||
            payload["name"] is not JsonValue nameFieldValue ||
            !nameFieldValue.TryGetValue<string>(out var nameField) || string.IsNullOrEmpty(nameField) ||
            payload["ordinal"] is not JsonValue ordinalFieldValue ||
            !ordinalFieldValue.TryGetValue<string>(out var ordinalField) || string.IsNullOrEmpty(ordinalField) ||
            payload["values"] is not JsonValue valuesValue ||
            !valuesValue.TryGetValue<string>(out var values) || string.IsNullOrEmpty(values) ||
            payload["valueOf"] is not JsonValue valueOfValue ||
            !valueOfValue.TryGetValue<string>(out var valueOf) || string.IsNullOrEmpty(valueOf))
            throw new InvalidDataException(
                "malformed [KotlinRichEnum] payload: expected entries plus name/ordinal fields and values/valueOf APIs");
        if (!type.IsClass || type.IsEnum || type.IsGenericType)
            throw new InvalidDataException(
                $"malformed [KotlinRichEnum] on '{type.FullName}': expected a non-generic reference class");

        var entryFields = new Dictionary<string, string>(StringComparer.Ordinal);
        var physicalFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in entries)
        {
            if (node is not JsonObject entry || entry.Count != 2 ||
                entry["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name) ||
                entry["field"] is not JsonValue fieldValue ||
                !fieldValue.TryGetValue<string>(out var fieldName) || string.IsNullOrEmpty(fieldName) ||
                !entryFields.TryAdd(name, fieldName) || !physicalFields.Add(fieldName))
                throw new InvalidDataException(
                    "malformed [KotlinRichEnum] payload: entries require unique non-empty name/field pairs");
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(field => field.Name == fieldName && field.FieldType == type && field.IsInitOnly).ToArray();
            if (fields.Length != 1)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] on '{type.FullName}': entry field '{fieldName}' " +
                    "must be uniquely public, static, initonly, and self-typed");
        }

        if (!physicalFields.Add(nameField) || !physicalFields.Add(ordinalField))
            throw new InvalidDataException(
                "malformed [KotlinRichEnum] payload: physical fields must be distinct");
        void RequireMetadataField(string fieldName, IReadOnlySet<string> fieldTypes)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(field => field.Name == fieldName && fieldTypes.Contains(field.FieldType.FullName) && field.IsInitOnly)
                .ToArray();
            if (fields.Length != 1)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] on '{type.FullName}': metadata field '{fieldName}' " +
                    $"must be uniquely public, instance, initonly, and {string.Join("/", fieldTypes)}-typed");
        }
        RequireMetadataField(nameField, new HashSet<string>(StringComparer.Ordinal) { "System.String", "kotlin.String" });
        RequireMetadataField(ordinalField, new HashSet<string>(StringComparer.Ordinal) { "System.Int32", "kotlin.Int" });

        var generatedValues = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == values && method.ReturnType.IsArray &&
                method.ReturnType.GetArrayRank() == 1 && method.ReturnType.GetElementType() == type &&
                !method.IsGenericMethod && method.GetParameters().Length == 0 &&
                HasAttribute(method.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        var generatedValueOf = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == valueOf && method.ReturnType == type &&
                !method.IsGenericMethod &&
                method.GetParameters() is [var parameter] &&
                parameter.ParameterType.FullName is "System.String" or "kotlin.String" &&
                HasAttribute(method.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (generatedValues.Length != 1 || generatedValueOf.Length != 1 ||
            generatedValues[0].MetadataToken == generatedValueOf[0].MetadataToken)
            throw new InvalidDataException(
                $"malformed [KotlinRichEnum] on '{type.FullName}': compiler-generated values/valueOf APIs are missing or ambiguous");
        return new RichEnumMetadata(entryFields, nameField, ordinalField, values, valueOf);
    }

    static JsonObject TrustedStaticCarrierPayload(Type carrierType, Assembly declaringAssembly)
    {
        try
        {
            var attrs = carrierType.GetCustomAttributesData().Where(c =>
                c.AttributeType.FullName == KotlinStaticCarrierAttr &&
                c.AttributeType.Assembly == declaringAssembly &&
                HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr))
                .ToArray();
            if (attrs.Length == 0) return null;
            if (attrs.Length != 1)
                throw new FormatException("expected exactly one trusted attribute");
            var attr = attrs[0];
            if (attr.ConstructorArguments.Count != 2 ||
                attr.ConstructorArguments[0].Value is not string version)
                throw new FormatException("expected (version, byte[]) constructor arguments");
            if (attr.NamedArguments.Count != 0)
                throw new FormatException("named arguments are forbidden");
            if (BirCarrier.DecodeBody(version, ReadByteArrayArg(attr.ConstructorArguments[1])) is not JsonObject payload ||
                payload.Any(pair => pair.Key is not ("owner" or "protectedNested")) || payload["owner"] is not JsonValue ownerValue ||
                !ownerValue.TryGetValue<string>(out var owner) || string.IsNullOrEmpty(owner))
                throw new FormatException("expected a non-empty string 'owner'");
            if (payload["protectedNested"] is JsonNode protectedNode)
            {
                if (protectedNode is not JsonArray names || names.Any(name =>
                    name is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text))
                    || names.Select(name => name.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != names.Count)
                    throw new FormatException("expected distinct protected nested classifier names");
                foreach (var name in names.Select(name => name.GetValue<string>()))
                    if (carrierType.GetNestedTypes(BindingFlags.Public).Count(type => StripGenericArity(type.Name) == name) != 1)
                        throw new FormatException("protected nested classifier is not a unique public declaration");
            }
            return payload;
        }
        catch (MalformedTrustedStaticCarrierException) { throw; }
        catch (Exception ex)
        {
            throw new MalformedTrustedStaticCarrierException(
                $"malformed trusted [KotlinStaticCarrier] on '{carrierType.FullName}'", ex);
        }
    }

    static void ValidateStaticCarrierMembers(Type carrierType)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        if (carrierType.GetFields(declared).Any(field => !field.IsStatic) ||
            carrierType.GetMethods(declared).Any(method => !method.IsStatic) ||
            carrierType.GetConstructors(declared).Any(ctor => !ctor.IsStatic) ||
            carrierType.GetProperties(declared).Any(property =>
                property.GetAccessors(nonPublic: true) is not { Length: > 0 } accessors ||
                accessors.Any(accessor => !accessor.IsStatic)))
            throw new MalformedTrustedStaticCarrierException(
                $"trusted [KotlinStaticCarrier] '{carrierType.FullName}' contains an instance declaration");
    }

    // The first constructor string argument of the attribute `fullName` (e.g. @ClrCollectionFactory("list") -> "list"),
    // or null when the attribute is absent / carries no string arg. Used for the factory-kind markers.
    static string AttrStringArg(IList<CustomAttributeData> attrs, string fullName)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == fullName);
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The element type FQN of an array factory's return type (`kotlin.IntArray` -> "kotlin.Int"), or null when the return
    // is not a concrete array (the generic `arrayOf<T>` returns `Array<T>` whose element is a type variable). Used only as
    // a last-resort element source for a concrete primitive factory call that carries neither a type argument nor a
    // `newArray` vararg wrapper — i.e. one whose vararg was written as a spread.
    static string ArrayElemHint(Type retType)
    {
        try
        {
            if (retType != null && retType.IsArray)
            {
                var el = retType.GetElementType();
                if (el != null && !el.IsGenericParameter && TypeNodeOf(el) is TypeNode.Fqn { Args: null } f)
                    return f.Name;
            }
        }
        catch { }
        return null;
    }

    // The class-level CLR binding: @ClrTypeAlias (the type-identity binding); a class-level @ClrIntrinsic is also
    // accepted for any not-yet-renamed bound class. Returns the single ctor-arg (the .NET FQN), or null if not CLR-bound.
    static string ClrAliasOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrTypeAlias" or "kotlin.clr.ClrIntrinsic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The member-level CLR binding: @ClrIntrinsic("Name"). Returns the BCL member name (the call is
    // rewritten to owner.Name), or null when the member carries no intrinsic (a rule-3 candidate).
    static string ClrIntrinsicOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrIntrinsic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The PARAMETER positions (0-based, over the method's declared params) marked @ClrRefArgument — a plain-typed
    // parameter the bound BCL member takes BY REFERENCE (`ref`/`out`). The substituted call wraps these argTypes
    // positions in ByRef nodes so ilemit resolves the ref/out overload + emits the address-load. Empty when none.
    static int[] ByrefPositionsOf(MethodBase method)
    {
        var ps = method.GetParameters();
        List<int> hits = null;
        for (var i = 0; i < ps.Length; i++)
            if (ps[i].GetCustomAttributesData().Any(a => a.AttributeType.FullName == "kotlin.clr.ClrRefArgument"))
                (hits ??= new List<int>()).Add(i);
        return hits?.ToArray() ?? Array.Empty<int>();
    }

    // One parameter of an @ClrIntrinsic declaration may state that its Kotlin exclusive-end value occupies a CLR
    // count slot. The marker sits on the END parameter and names the START parameter. Keep the pair on the exact
    // declaration binding; a name/arity side index would recreate the overload collapse this metadata is meant to
    // avoid. Multiple markers or an invalid/non-earlier start are malformed compiler-provided stdlib metadata.
    static (int Start, int End)? CountRangeOf(MethodBase method)
    {
        (int Start, int End)? result = null;
        var ps = method.GetParameters();
        for (var end = 0; end < ps.Length; end++)
        {
            var attr = ps[end].GetCustomAttributesData().FirstOrDefault(a =>
                a.AttributeType.FullName == "kotlin.clr.ClrCountFromExclusiveEnd");
            if (attr == null) continue;
            if (result != null || ClrIntrinsicOf(method.GetCustomAttributesData()) == null
                || attr.ConstructorArguments.Count != 1
                || attr.ConstructorArguments[0].Value is not int start || start < 0 || start >= end
                // The reference build preserves Kotlin primitive declaration types (`kotlin.Int`); runtime builds
                // later lower those to System.Int32. Validate the shared semantic/physical primitive identity rather
                // than requiring one build phase's spelling.
                || ParamKey(ps[start].ParameterType, relaxed: false).Kind != TypeKeyKind.Int32
                || ParamKey(ps[end].ParameterType, relaxed: false).Kind != TypeKeyKind.Int32)
                throw new InvalidDataException(
                    $"invalid @ClrCountFromExclusiveEnd on {method.DeclaringType?.FullName}.{method.Name} parameter {end}");
            result = (start, end);
        }
        return result;
    }

    // Parameter metadata owns the argument position, codec and bytes. Decode once at this boundary; consumers clone
    // the raw source expression before splicing rather than serializing it between compiler passes.
    static Dictionary<int, IBirExpression> KotlinDefaultsOf(MethodBase method)
    {
        Dictionary<int, IBirExpression> map = null;
        foreach (var p in method.GetParameters())
        {
            var a = p.GetCustomAttributesData().FirstOrDefault(x => x.AttributeType.FullName == DefaultBirCarrier.AttributeName);
            if (a == null) continue;
            var bir = ExecutableBirCodec.ReadExpression((string)a.ConstructorArguments[1].Value,
                ReadByteArrayArg(a.ConstructorArguments[2]));
            (map ??= new Dictionary<int, IBirExpression>())[Convert.ToInt32(a.ConstructorArguments[0].Value)] = bir;
        }
        return map;
    }

    // The complete default-value map for an already selected declaration. KotlinDefault wins because it carries the
    // Kotlin expression (including reads of earlier parameters/receivers); otherwise use the ECMA-335 constant directly.
    // This is deliberately a reference-DLL scan. Neither dll2klib nor kotc materializes a default value.
    internal static Dictionary<int, IBirExpression> CallableDefaultsOf(MethodBase method, Func<string, Type> coreType)
    {
        var map = KotlinDefaultsOf(method);
        foreach (var p in method.GetParameters())
        {
            if (map?.ContainsKey(p.Position) == true || !p.HasDefaultValue) continue;
            if (ConstantDefaultBir(p, coreType) is not { } bir) continue;
            (map ??= new Dictionary<int, IBirExpression>())[p.Position] = bir;
        }
        return map;
    }

    static IBirExpression ConstantDefaultBir(ParameterInfo parameter, Func<string, Type> coreType)
    {
        object value;
        try { value = parameter.RawDefaultValue; }
        catch { return null; }
        if (ReferenceEquals(value, DBNull.Value) || ReferenceEquals(value, Missing.Value)) return null;

        var type = parameter.ParameterType;
        var declaredType = DeclarationTypeNode(type);
        if (declaredType is null) return null;

        // A Constant row may carry CLASS(null) for a value-type parameter. Reflection consequently reports null for
        // C#'s `DateTime value = default`, but ldnull does not inhabit that slot. `default` is CIR's general zero-value
        // expression: ilemit uses initobj for a value type / generic parameter and ldnull only for a reference type.
        // Nullable<T> is a value type too, so this also realizes its null/default representation directly rather than
        // relying on a boxed-null round trip at the call boundary.
        if (value is null)
        {
            bool isValueType;
            if (type.IsGenericParameter) isValueType = true;
            else
            {
                // MetadataLoadContext can need the base-type chain to classify a nominal type. An incomplete reference
                // universe must make this default unrepresentable, not silently reinterpret an unknown slot as a
                // reference and recreate the invalid ldnull emission this path exists to prevent.
                try { isValueType = type.IsValueType; }
                catch { return null; }
            }
            if (isValueType)
                return new BirDefaultValue { Type = declaredType };
        }

        // A non-null Constant row on Nullable<V> describes a V value, not a literal whose stack type is the
        // constructed Nullable<V>. Emit the exact V constant and construct the wrapper explicitly. Typing the leaf as
        // Nullable<V> bypasses later coercion (source and target already appear equal) and makes ilemit fall through to
        // ldnull because no literal opcode exists for the structured slot.
        Type nullableElement = null;
        try
        {
            if (type.IsConstructedGenericType && IsNullableDefinition(type.GetGenericTypeDefinition()))
                nullableElement = type.GetGenericArguments().SingleOrDefault();
        }
        catch { return null; }
        if (nullableElement != null)
        {
            var elementType = DeclarationTypeNode(nullableElement);
            var elementValue = MetadataConstantNode(nullableElement, elementType, value, coreType);
            if (elementType == null || elementValue == null) return null;
            return new BirNullableWrap
            {
                Elem = elementType,
                E = OptionalField<IBirExpression>.Of(elementValue),
            };
        }

        return MetadataConstantNode(type, declaredType, value, coreType);
    }

    static IBirExpression MetadataConstantNode(Type type, TypeNode declaredType, object value, Func<string, Type> coreType)
    {
        if (type == null || declaredType == null) return null;
        // Null reaches this helper only for a reference-typed slot: value-type null/defaults are handled above.
        if (value == null)
            return new BirConstant
            {
                Type = declaredType,
                Value = new BirLiteral.NullLiteral(),
            };

        bool isEnum;
        try { isEnum = type.IsEnum; }
        catch { return null; }
        if (isEnum)
        {
            Type underlying;
            try
            {
                underlying = Enum.GetUnderlyingType(type);
            }
            catch { return null; }
            // An enum Constant row must contain the exact ECMA-335 carrier for its underlying type. Do not let
            // Convert.ToInt* reinterpret an unrelated custom-constant value merely because it happens to be numeric.
            if (!LiteralValueInhabits(underlying, value)) return null;
            var physical = EnumConstantText(value, underlying);
            if (physical == null) return null;
            // An ECMA-335 enum constant is the underlying bits interpreted in the DECLARED enum slot. It need not name
            // an entry (flags combinations commonly do not), so carry the exact physical value instead of recovering
            // an entry identity. EnumValueLowering uses this same CIR form for named external enum entries, and ilemit
            // emits it one-to-one while returning the declared enum stack type.
            return new BirEnumValue
            {
                Type = declaredType,
                Underlying = underlying.FullName ?? underlying.Name,
                PhysicalValue = physical,
            };
        }

        // DecimalConstantAttribute is surfaced by both runtime reflection and MetadataLoadContext as an actual
        // System.Decimal. Decimal has no ECMA-335 literal opcode, so materialize the exact 96-bit coefficient, sign,
        // and scale through its public (int lo, int mid, int hi, bool isNegative, byte scale) constructor. Keep this
        // conditioned on the declared slot: a custom constant of the same runtime value attached to another slot is
        // malformed/unrepresentable metadata, not permission to change that slot's type.
        if (type.FullName == "System.Decimal" && value is decimal decimalValue)
        {
            var bits = decimal.GetBits(decimalValue);
            return new BirNew
            {
                Type = declaredType,
                ArgTypes = new TypeNode[]
                {
                    new TypeNode.Fqn("System.Int32"), new TypeNode.Fqn("System.Int32"), new TypeNode.Fqn("System.Int32"),
                    new TypeNode.Fqn("System.Boolean"), new TypeNode.Fqn("System.Byte"),
                },
                Args = new IBirExpression[]
                {
                    MetadataConst("System.Int32", bits[0]),
                    MetadataConst("System.Int32", bits[1]),
                    MetadataConst("System.Int32", bits[2]),
                    MetadataConst("System.Boolean", (bits[3] & unchecked((int)0x80000000)) != 0),
                    MetadataConst("System.Byte", (bits[3] >> 16) & 0xff),
                },
            };
        }

        // DateTimeConstantAttribute likewise surfaces an actual DateTime. Its metadata contract carries ticks (and
        // therefore an Unspecified kind), exactly the public DateTime(long ticks) construction below. A zero/default
        // DateTime took the generic null/value-type path above, which intentionally emits initobj instead.
        if (type.FullName == "System.DateTime" && value is DateTime dateTimeValue)
            return new BirNew
            {
                Type = declaredType,
                ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.Int64") },
                Args = new IBirExpression[] { MetadataConst("System.Int64", dateTimeValue.Ticks) },
            };

        // ilemit emits a const from its declared CIR type and must not reinterpret the JSON value. Validate the exact
        // reflection carrier here, where the CLR parameter declaration and its Constant/custom-constant row coexist.
        // A reference slot is the one legal exception to exact identity: CLR optional metadata can surface, for
        // example, an Int32 or String carrier on an Object parameter. Resolve it in this same MetadataLoadContext,
        // prove the declared slot accepts it, then make the required boxing/upcast explicit in CIR. A mismatched value-type,
        // enum, or generic slot remains unrepresentable rather than being converted or reinterpreted.
        if (!LiteralValueInhabits(type, value))
        {
            bool isValueType;
            try { isValueType = type.IsValueType; }
            catch { return null; }
            if (isValueType || type.IsGenericParameter || value.GetType().FullName is not string carrierName)
                return null;

            Type carrierType;
            try { carrierType = coreType(carrierName); }
            catch { return null; }
            bool acceptsCarrier;
            try { acceptsCarrier = carrierType != null && type.IsAssignableFrom(carrierType); }
            catch { return null; }
            if (!acceptsCarrier) return null;

            var carrierDeclaredType = DeclarationTypeNode(carrierType);
            var carrierValue = MetadataConstantNode(carrierType, carrierDeclaredType, value, coreType);
            if (carrierDeclaredType == null || carrierValue == null) return null;
            return new BirCast
            {
                Type = declaredType,
                E = OptionalField<IBirExpression>.Of(carrierValue),
            };
        }

        var literal = MetadataLiteral(value);
        return literal == null ? null : new BirConstant { Type = declaredType, Value = literal };
    }

    static bool LiteralValueInhabits(Type type, object value) => type.FullName switch
    {
        "System.Boolean" => value is bool,
        "System.Char" => value is char,
        "System.SByte" => value is sbyte,
        "System.Byte" => value is byte,
        "System.Int16" => value is short,
        "System.UInt16" => value is ushort,
        "System.Int32" => value is int,
        "System.UInt32" => value is uint,
        "System.Int64" => value is long,
        "System.UInt64" => value is ulong,
        "System.Single" => value is float,
        "System.Double" => value is double,
        "System.String" => value is string,
        _ => false,
    };

    static BirConstant MetadataConst(string type, object value) => new()
    {
        Type = new TypeNode.Fqn(type),
        Value = MetadataLiteral(value),
    };

    static BirLiteral MetadataLiteral(object value) => value switch {
        null => new BirLiteral.NullLiteral(),
        bool scalar => new BirLiteral.BooleanLiteral(scalar),
        char scalar => new BirLiteral.IntegerLiteral(scalar),
        string scalar => new BirLiteral.TextLiteral(scalar),
        sbyte scalar => new BirLiteral.IntegerLiteral(scalar),
        byte scalar => new BirLiteral.IntegerLiteral(scalar),
        short scalar => new BirLiteral.IntegerLiteral(scalar),
        ushort scalar => new BirLiteral.IntegerLiteral(scalar),
        int scalar => new BirLiteral.IntegerLiteral(scalar),
        uint scalar => new BirLiteral.IntegerLiteral(unchecked((int)scalar)),
        long scalar => new BirLiteral.IntegerLiteral(scalar),
        ulong scalar => new BirLiteral.IntegerLiteral(unchecked((long)scalar)),
        float scalar => new Float32Literal(BitConverter.SingleToInt32Bits(scalar)),
        double scalar => new Float64Literal(BitConverter.DoubleToInt64Bits(scalar)),
        _ => null,
    };

    // MetadataLoadContext types are metadata-only and cannot be passed to Convert.ChangeType as a runtime Type.
    // Normalize through the exact legal enum-underlying identity instead; this also makes signedness and width
    // explicit before the bit pattern becomes the invariant-culture CIR string.
    internal static string EnumConstantText(object value, Type underlying)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return underlying.FullName switch
            {
                "System.SByte" => Convert.ToSByte(value, culture).ToString(culture),
                "System.Byte" => Convert.ToByte(value, culture).ToString(culture),
                "System.Int16" => Convert.ToInt16(value, culture).ToString(culture),
                "System.UInt16" => Convert.ToUInt16(value, culture).ToString(culture),
                "System.Int32" => Convert.ToInt32(value, culture).ToString(culture),
                "System.UInt32" => Convert.ToUInt32(value, culture).ToString(culture),
                "System.Int64" => Convert.ToInt64(value, culture).ToString(culture),
                "System.UInt64" => Convert.ToUInt64(value, culture).ToString(culture),
                _ => null,
            };
        }
        catch { return null; }
    }

    // The member-level PROPERTY-accessor binding: @ClrProperty(access, name). `access` is the READ(1)/WRITE(2) flag word;
    // `name` is the .NET property. Returns (access, name) or null when the member carries no @ClrProperty.
    static (int Access, string Name)? ClrPropertyOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == "kotlin.clr.ClrProperty");
        if (a == null || a.ConstructorArguments.Count < 2) return null;
        if (a.ConstructorArguments[1].Value is not string name) return null;
        var access = a.ConstructorArguments[0].Value is null ? 0 : Convert.ToInt32(a.ConstructorArguments[0].Value);
        return (access, name);
    }

    // A receiver-type key for an extension fun's first param, matched against a call's first-arg type. Arrays collapse
    // to "[]", generic params to "gp", a generic type to its open def's stripped FQN. A NESTED type's reflection name
    // ("kotlin.collections.Map`2+Entry`2") is normalized to the BIR semantic hierarchy
    // ("kotlin.collections.Map.Entry") — e.g. the Map.Entry.component1/2 extensions. Before #225 lifted nested
    // types carried a `$`-joined top-level metadata name; once ownership is represented by real CLR nesting, dropping
    // the declaring chain here would turn every sibling `Outer.Entry` into the unrelated `namespace.Entry`.
    static string RecvKey(Type t)
    {
        if (t.IsByRef && t.GetElementType() is Type e) t = e;
        if (t.IsArray) return "[]";
        if (t.IsGenericParameter) return "gp";
        var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        return DottedFqn(StripGenericArity(def.FullName ?? def.Name));
    }

    // A method's structural comparison signature, used to overload-disambiguate a top-level @ClrIntrinsic
    // (sqrt(Double) vs sqrt(Float); pow(Double,Double) intrinsic vs pow(Double,Int) real-body).
    internal static SignatureKey SigKeyOf(ParameterInfo[] ps, bool relaxed = false) =>
        new(ps.Select(p => ParamKey(p.ParameterType, relaxed)));

    // A binding is selected in Kotlin vocabulary. An open array's System.Array
    // value storage does not replace its explicitly recorded source classifier.
    static SignatureKey ArrayBindingSigKeyOf(ParameterInfo[] parameters) =>
        new(parameters.Select(parameter => parameter.ParameterType.FullName == "System.Array"
            && KotlinTypeOf(parameter.GetCustomAttributesData(), parameter.Member.DeclaringType?.Assembly)
                is TypeNode.Array sourceArray
            ? ParamKey(sourceArray) : ParamKey(parameter.ParameterType, relaxed: false)));

    /// A signature key with every nominal position that cannot be folded collapsed to [TypeKeyKind.Reference]. The two sides of a
    /// @KotlinDefault lookup describe the same parameter in DIFFERENT spaces — a call site carries kotc's pre-lowering
    /// Kotlin type (`kotlin.collections.List`), a reference assembly its lowered CLR form
    /// (`System.Collections.Generic.IReadOnlyList`) — so only a structurally folded kind is comparable, and
    /// anything else has to collapse. That still separates an overload differing in a folded position
    /// (`f(String, String)` from `f(String, List&lt;String&gt;)`), which is what the exact key cannot do here. Two
    /// overloads differing only between two DIFFERENT class types collapse together, are recorded as a conflict, and are
    /// refused rather than guessed.
    internal static bool IsValueKey(TypeKey key) => key.Kind is
        TypeKeyKind.Int8 or TypeKeyKind.Int16 or TypeKeyKind.Int32 or TypeKeyKind.Int64 or
        TypeKeyKind.Float32 or TypeKeyKind.Float64 or TypeKeyKind.Boolean or TypeKeyKind.Char or
        TypeKeyKind.UInt8 or TypeKeyKind.UInt16 or TypeKeyKind.UInt32 or TypeKeyKind.UInt64;

    /// Record one declaration's @KotlinDefault carriers under BOTH keys the splice can look up: `owner|name|arity|sigKey`
    /// (the exact overload — a call site reproduces that signature from its own declared parameter vector) and
    /// `owner|name|arity` (the fallback when no signature is available). The arity key is written once; a SECOND
    /// declaration of the same name+arity whose defaults differ marks it CONFLICTED, so the fallback refuses instead of
    /// serving whichever declaration the metadata scan happened to reach last.
    static void AddKotlinDefaults(ReferenceDotKtMetadata metadata, string ownerFqn, string name, ParameterInfo[] ps,
        Dictionary<int, IBirExpression> defaults)
    {
        var arityKey = new DefaultKey(ownerFqn, name, ps.Length);
        var sig = SigKeyOf(ps);
        // The callee's DECLARED parameter types, for a call site that carries none of its own — a constructor
        // DELEGATION rides the ctor declaration, so `baseArgs` is a bare array with no signature vector. The splice
        // needs them to type the temp it binds each spliced value to.
        Put(new DefaultKey(ownerFqn, name, ps.Length, sig), defaults);
        Put(new DefaultKey(ownerFqn, name, ps.Length, SigKeyOf(ps, relaxed: true), Relaxed: true), defaults);
        if (metadata.KotlinDefaults.TryGetValue(arityKey, out var prior))
        {
            if (!ExecutableBirCodec.SameDefaults(prior, defaults)) metadata.KotlinDefaultsConflicted.Add(arityKey);
            return;
        }
        metadata.KotlinDefaults[arityKey] = defaults;

        void Put(DefaultKey key, Dictionary<int, IBirExpression> d)
        {
            // Two declarations landing on the SAME signature key can only be told apart by a finer key, so this one
            // refuses too rather than serving the last writer.
            if (metadata.KotlinDefaults.TryGetValue(key, out var had))
            {
                if (!ExecutableBirCodec.SameDefaults(had, d)) metadata.KotlinDefaultsConflicted.Add(key);
                return;
            }
            metadata.KotlinDefaults[key] = d;
        }
    }

    // An @JvmInline backing-field's CLR `conv` target — the ilemit conv opcode token for the field's primitive type
    // (kotlin.Int -> "int", kotlin.Byte -> "sbyte", ...). Null if the field is not a primitive ilemit conv'able.
    static string InlineFieldConv(Type fieldType) => fieldType.FullName switch
    {
        "kotlin.Int" => "int",
        "kotlin.Long" => "long",
        "kotlin.Short" => "short",
        "kotlin.Byte" => "sbyte",
        "kotlin.Char" => "char",
        "kotlin.Double" => "double",
        "kotlin.Float" => "float",
        "System.Int32" => "int",
        "System.Int64" => "long",
        "System.Int16" => "short",
        "System.SByte" => "sbyte",
        "System.Char" => "char",
        "System.Double" => "double",
        "System.Single" => "float",
        _ => null,
    };

    static int KotlinFunctionFlags(IList<CustomAttributeData> attrs)
    {
        var attr = attrs.FirstOrDefault(a => a.AttributeType.FullName == KotlinFunctionAttr);
        if (attr == null || attr.ConstructorArguments.Count == 0) return 0;
        var value = attr.ConstructorArguments[0].Value;
        return value is int i ? i : 0;
    }

    static int? AttrInt32(IList<CustomAttributeData> attrs, string fullName)
    {
        var attr = attrs.FirstOrDefault(a => a.AttributeType.FullName == fullName);
        if (attr == null || attr.ConstructorArguments.Count == 0 || attr.ConstructorArguments[0].Value == null)
            return null;
        return Convert.ToInt32(attr.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
    }

    // A STRUCTURED TypeNode from a reflected ref.dll type — the pure-Kotlin identity kotc would have emitted (the ref
    // surface's types ARE named kotlin.* — kotlin.collections.List<kotlin.String>, kotlin.Int, …). Used to carry a
    // top-level fn / member RETURN type so bir2cir StaticType (#59) can recover a `callStatic`/`callInstance` whose
    // node lacks a `ret` (a non-generic call — kotc emits `ret` only for a generic call). Covers the shapes StaticType
    // needs (Fqn+args for collection detect, nullable, array, primitive, tv); a delegate/func return is left null.
    internal static TypeNode TypeNodeOf(Type type)
    {
        if (type.IsByRef) return TypeNodeOf(type.GetElementType()!) is TypeNode e0 ? new TypeNode.ByRef(e0) : null;
        if (type.IsPointer) return TypeNodeOf(type.GetElementType()!) is TypeNode ep ? new TypeNode.Ptr(ep) : null;
        if (type.IsArray)
        {
            if (TypeNodeOf(type.GetElementType()!) is not TypeNode e1) return null;
            return type.IsSZArray ? new TypeNode.Array(e1) : TypeNode.Array.General(e1, type.GetArrayRank());
        }
        if (type.IsGenericParameter) return null;   // an unresolved fn type-param: no useful static identity
        if (IsDelegate(type)) return null;
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(TypeNodeOf).ToArray();
            if (IsNullableDefinition(def)) return args[0] is TypeNode nv ? new TypeNode.Nullable(nv) : null;
            if (IsFunc(def) || IsAction(def)) return null;
            if (args.Any(a => a == null)) return new TypeNode.Fqn(StripGenericArity(def.FullName ?? def.Name));
            return new TypeNode.Fqn(StripGenericArity(def.FullName ?? def.Name), args);
        }
        var prim = PrimitiveBirName(type);
        return new TypeNode.Fqn(prim ?? StripGenericArity(type.FullName ?? type.Name));
    }

    // Declaration-signature projection used only by bir2cir's reference hierarchy/member index.
    // Unlike TypeNodeOf (a best-effort static-result helper), generic parameters are meaningful
    // here and must retain their CLR owner space and position.
    internal static TypeNode DeclarationTypeNode(Type type) => DeclarationTypeNode(type, nominalDelegates: false);

    internal static TypeNode DeclarationTypeNode(Type type, bool nominalDelegates) =>
        DeclarationTypeNode(type, nominalDelegates, preserveNestedIdentity: false);

    static TypeNode DeclarationTypeNode(Type type, bool nominalDelegates, bool preserveNestedIdentity)
    {
        TypeNode Project(Type t) => DeclarationTypeNode(t, nominalDelegates, preserveNestedIdentity);
        string Name(Type def) => preserveNestedIdentity && def.IsNested ? ExactPhysicalMetadataName(def)
            : DottedFqn(StripGenericArity(def.FullName ?? def.Name));
        if (type == null) return null;
        if (type.IsByRef) return Project(type.GetElementType()!) is TypeNode e0 ? new TypeNode.ByRef(e0) : null;
        if (type.IsPointer)
            return Project(type.GetElementType()!) is TypeNode ep ? new TypeNode.Ptr(ep) : null;
        if (type.IsArray)
        {
            if (Project(type.GetElementType()!) is not TypeNode e1) return null;
            return type.IsSZArray ? new TypeNode.Array(e1) : TypeNode.Array.General(e1, type.GetArrayRank());
        }
        if (type.IsGenericParameter)
            return new TypeNode.Tv(type.DeclaringMethod != null ? "method" : "type", type.GenericParameterPosition);
        // Kotlin function types remain `{t:fn}` in CIR, with the exact physical delegate family retained.
        // Unknown/custom CLR delegates stay nominal FQNs below; shape-projecting them would lose their identity.
        var delegateFamily = DelegateFamily(type);
        if (delegateFamily != null && !nominalDelegates)
        {
            var invoke = type.GetMethod("Invoke");
            if (invoke == null) return null;
            var ret = Project(invoke.ReturnType);
            var ps = invoke.GetParameters().Select(p => Project(p.ParameterType)).ToArray();
            return ret != null && ps.All(p => p != null) ? new TypeNode.Fn(false, ret, ps, null, delegateFamily) : null;
        }
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(Project).ToArray();
            if (IsNullableDefinition(def)) return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(Name(def), args);
        }
        // This is an exact reflected declaration, not Kotlin's Unit-return convention. A real Unit class must stay
        // a value-position token here, particularly beneath constructed interfaces; only System.Void denotes no value.
        var prim = type.FullName == "kotlin.Unit" ? null : PrimitiveBirName(type);
        return new TypeNode.Fqn(prim ?? Name(type));
    }

    static bool IsFunc(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Func`", StringComparison.Ordinal);

    // Reference types live in a MetadataLoadContext and are not reference-equal to runtime typeof(...) values.
    // Nullable is a metadata identity, so recognize its generic definition by that identity at the reflection edge.
    static bool IsNullableDefinition(Type type) =>
        type?.IsGenericTypeDefinition == true && type.FullName == "System.Nullable`1";

    static bool IsAction(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Action`", StringComparison.Ordinal);

    static string DelegateFamily(Type type)
    {
        Type def;
        try { def = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type; }
        catch { return null; }
        if (def.Namespace == "System")
        {
            if (def.Name == "Action" || def.Name.StartsWith("Action`", StringComparison.Ordinal)) return "System.Action";
            if (def.Name.StartsWith("Func`", StringComparison.Ordinal)) return "System.Func";
        }
        if (def.Namespace == "DotKt.Runtime.CompilerServices")
        {
            if (def.Name.StartsWith("KAction`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KAction";
            if (def.Name.StartsWith("KFunc`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KFunc";
        }
        return null;
    }

    internal static bool IsDelegate(Type type)
    {
        for (var cur = type; cur != null; cur = cur.BaseType)
            if (cur.FullName == "System.MulticastDelegate")
                return true;
        return false;
    }

    static string PrimitiveBirName(Type type)
    {
        if (type == typeof(bool)) return "bool";
        // .NET-aligned 8-bit tokens (#54): "sbyte" is SIGNED = kotlin.Byte (System.SByte); "byte" is UNSIGNED =
        // kotlin.UByte (System.Byte). This matches int/short/long, whose token names already agree with .NET.
        // The unsigned family (ushort/uint/ulong) is here for the same reason.
        if (type == typeof(sbyte)) return "sbyte";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(char)) return "char";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(object)) return "object";
        if (type == typeof(short)) return "short";
        if (type == typeof(ushort)) return "ushort";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(string)) return "string";
        if (type == typeof(void)) return "void";
        // The REFERENCE stdlib emits the pure-Kotlin primitives as real types whose FullName is literally
        // "kotlin.Int" / "kotlin.String" / ... When such a ref dll is read back, converge those onto the SAME
        // CLR-shorthand token as their BCL twin so a member signature speaks one vocabulary for TypeMatches.
        return PrimitiveBirNameByFullName(type.FullName);
    }

    static string PrimitiveBirNameByFullName(string fullName) => fullName switch
    {
        "kotlin.Boolean" => "bool",
        "kotlin.Byte" => "sbyte",
        "kotlin.Char" => "char",
        "kotlin.Double" => "double",
        "kotlin.Float" => "float",
        "kotlin.Int" => "int",
        "kotlin.Long" => "long",
        "kotlin.Any" => "object",
        "kotlin.Short" => "short",
        "kotlin.String" => "string",
        "kotlin.UByte" => "byte",
        "kotlin.UInt" => "uint",
        "kotlin.ULong" => "ulong",
        "kotlin.UShort" => "ushort",
        _ => null,
    };

    static string TypeKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        return "class";
    }

    // A BYREF-LIKE type (`ref struct`): a value that may hold a managed pointer, which the CLR forbids as the type of an
    // instance field of a non-byref-like type. The CLR encodes it as `IsByRefLikeAttribute`, so the attribute probe is
    // what actually answers here. `Type.IsByRefLike` follows only as a best-effort second try for the runtime-intrinsic
    // byref-likes (TypedReference/ArgIterator/RuntimeArgumentHandle) that carry no attribute: MetadataLoadContext is not
    // required to implement it, hence the catch-false — a throw means "unknown", which reads as not byref-like.
    const string ByRefLikeAttrFqn = "System.Runtime.CompilerServices.IsByRefLikeAttribute";
    static bool IsByRefLikeType(Type type)
    {
        if (!type.IsValueType) return false;
        try { if (type.GetCustomAttributesData().Any(c => c.AttributeType?.FullName == ByRefLikeAttrFqn)) return true; } catch { }
        try { return type.IsByRefLike; } catch { return false; }
    }

    // The constraint class of a generic parameter (a `GetGenericArguments()` element): "struct" when it carries the
    // value-type constraint (`where T : struct`), "class" when it carries the reference constraint (`where T : class`),
    // else "unconstrained". Drives the tv struct-ness oracle for the nullability fold.
    static string GenericParamConstraintClass(Type gp)
    {
        var a = gp.GenericParameterAttributes;
        if ((a & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) return "struct";
        if ((a & GenericParameterAttributes.ReferenceTypeConstraint) != 0) return "class";
        return "unconstrained";
    }

    internal static string StripGenericArity(string value)
    {
        if (value == null || value.IndexOf('`') < 0) return value;
        // A nested generic reflection name has one arity suffix per generic segment
        // (`Map`2+Map$Entry`2`). Truncating at the first backtick collapses the nested declaration to its outer owner
        // and can then apply the outer owner's @ClrTypeAlias to a member signature. Remove only each `N suffix.
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            if (value[i] == '`' && i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]))
            {
                i += 2;
                while (i < value.Length && char.IsAsciiDigit(value[i])) i++;
                continue;
            }
            result.Append(value[i++]);
        }
        return result.ToString();
    }

    internal readonly record struct OwnerTypeIdentity(string Name, int Arity);

    internal static OwnerTypeIdentity OwnerIdentity(string name, int arity) =>
        new(DottedFqn(StripGenericArity(name)), arity);

    // A flattened Kotlin/BIR identity does not encode which nested TypeDef segment owns each generic slot.
    // Preserve that loss of information as an ambiguous null entry instead of letting reflection or reference order
    // select between legal spellings such as Outer`1+Leaf`1 and Outer+Leaf`2.
    internal static void AddExactPhysicalTypeName(
        Dictionary<OwnerTypeIdentity, string> index, OwnerTypeIdentity identity, string exact)
    {
        if (!index.TryAdd(identity, exact) && index[identity] != exact)
            index[identity] = null;
    }

    // The nested-type separator normalizer: a reflected FullName uses `+` between an enclosing type and its nested type
    // (`kotlin.coroutines.CoroutineContext+Key`), while kotc/bir2cir speak dots everywhere. Converge onto dots so a
    // bound-index lookup keyed by kotc's `kotlin.coroutines.CoroutineContext.Key` matches.
    internal static string DottedFqn(string value) => value.Replace('+', '.');

    // The declared type-BOUND of a generic parameter as a structured TypeNode (`E : Element` -> Fqn(Element)), or null
    // when unconstrained / the sole bound is objectish (System.Object -> no useful restriction) / the bound is a
    // self-referential F-bound. A gp-dependent constraint (`E : Enum<E>`, or a sibling-var bound) has no valid closed
    // generic to substitute a `<*>` arg to, so it returns null — the objectish arg is left unchanged (symmetric with
    // StarProjectionBoundLowering's local ContainsTypeVar skip). The special class/struct/new() constraints are
    // irrelevant here (not a type identity a `<*>` arg can be repointed to). Nested `+` separators are normalized to dots.
    static TypeNode GenericParamBound(Type gp)
    {
        foreach (var c in gp.GetGenericParameterConstraints())
        {
            if (c.IsGenericParameter) continue;                       // a bare sibling type-var bound: no type identity
            if (c.IsConstructedGenericType && c.GetGenericArguments().Any(a => a.IsGenericParameter)) continue;  // F-bound / gp-dependent: no closed form
            if (TypeNodeOf(c) is not TypeNode node) continue;
            if (node is TypeNode.Fqn { Args: null } f && (f.Name is "object" or "System.Object" || DottedFqn(f.Name) == "kotlin.Any")) continue;
            return NormalizeNestedNames(node);
        }
        return null;
    }

    internal static BirTypeParameter GenericParamSourceDeclaration(Type gp)
    {
        var declaration = new BirTypeParameter { Name = gp.Name };
        var constraints = new List<TypeNode>();
        foreach (var constraint in gp.GetGenericParameterConstraints())
            if (DeclarationTypeNode(constraint) is TypeNode node)
                constraints.Add(NormalizeNestedNames(node));
        if (constraints.Count != 0) declaration.Constraints = constraints.ToArray();

        var special = new List<string>();
        var attrs = gp.GenericParameterAttributes;
        var variance = (attrs & GenericParameterAttributes.VarianceMask) switch
        {
            GenericParameterAttributes.Covariant => "out",
            GenericParameterAttributes.Contravariant => "in",
            _ => null,
        };
        if (variance != null) declaration.Variance = variance;
        if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0) special.Add("class");
        if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) special.Add("struct");
        if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0) special.Add("new");
        if ((attrs & GenericParameterAttributes.AllowByRefLike) != 0) special.Add("allowsRefStruct");
        if (special.Count != 0) declaration.SpecialConstraints = special.ToArray();
        return declaration;
    }

    // Recursively converge every Fqn name in a TypeNode onto dotted form (a nested-type bound like Element carries a `+`).
    static TypeNode NormalizeNestedNames(TypeNode t) => t switch
    {
        TypeNode.Fqn f => new TypeNode.Fqn(DottedFqn(f.Name), f.Args?.Select(NormalizeNestedNames).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(NormalizeNestedNames(n.Of)),
        TypeNode.Array a => new TypeNode.Array(NormalizeNestedNames(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(NormalizeNestedNames(b.Of)),
        _ => t,
    };
}

sealed record LateinitStateTarget(string Owner, string Method, bool Static);
