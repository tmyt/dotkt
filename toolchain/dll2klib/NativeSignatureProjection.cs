using System.Collections.Immutable;
using System.Reflection.Metadata;
using DotKt.Bir;
using KType = DotKt.Klib.Metadata.Type;

// Decode once, retaining a native view only where CLR information differs from
// the ordinary Kotlin projection. Modifiers outside function pointers retain
// the existing surface behavior. Within one, every nested modifier/array shape
// becomes explicit Kotlin vocabulary before leaving this boundary.
internal sealed partial class SignatureDecoder
{
    NativeProjection? _nativeProjection;
    internal NativeProjection NativeProjectionProvider => _nativeProjection ??= new(this);

    internal readonly record struct SignatureType(KType Surface, KType? Native = null, string? Unsupported = null)
    {
        internal KType Exact => Native ?? Surface;
    }

    internal sealed class NativeProjection(SignatureDecoder decoder) : ISignatureTypeProvider<SignatureType, GenericContext>
    {
        KType Applied(string name, params KType[] arguments)
        {
            var type = decoder.Named(name);
            foreach (var argument in arguments)
                type.Argument.Add(new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Inv, Type = argument,
                });
            return type;
        }

        public SignatureType GetArrayType(SignatureType element, ArrayShape shape)
        {
            var supported = shape.Sizes.Length == 0 && shape.LowerBounds.All(bound => bound == 0)
                && FunctionPointerVocabulary.Arrays.ContainsKey(shape.Rank);
            return new(supported && ContainsPointer(element.Surface)
                ? Applied(FunctionPointerVocabulary.Arrays[shape.Rank], element.Exact)
                : decoder.GetArrayType(element.Surface, shape), supported
                ? Applied(FunctionPointerVocabulary.Arrays[shape.Rank], element.Exact) : null,
                element.Unsupported ?? (supported ? null : "Unsupported bounded CLR array in function-pointer signature"));
        }
        public SignatureType GetSZArrayType(SignatureType element) => new(
            decoder.GetSZArrayType(ContainsPointer(element.Surface) ? element.Exact : element.Surface),
            element.Native is null ? null : decoder.GetSZArrayType(element.Exact), element.Unsupported);
        public SignatureType GetByReferenceType(SignatureType element) => new(
            decoder.GetByReferenceType(ContainsPointer(element.Surface) ? element.Exact : element.Surface),
            element.Native is null ? null : decoder.GetByReferenceType(element.Exact), element.Unsupported);
        public SignatureType GetPointerType(SignatureType element) => new(
            decoder.GetPointerType(ContainsPointer(element.Surface) ? element.Exact : element.Surface),
            element.Native is null ? null : decoder.GetPointerType(element.Exact), element.Unsupported);
        public SignatureType GetFunctionPointerType(MethodSignature<SignatureType> signature)
        {
            if (signature.ParameterTypes.Prepend(signature.ReturnType).FirstOrDefault(t => t.Unsupported != null).Unsupported is { } error)
                throw new InvalidDataException(error);
            if (!FunctionPointerVocabulary.Conventions.TryGetValue(signature.Header.RawValue, out var convention)
                || signature.GenericParameterCount != 0
                || signature.RequiredParameterCount != signature.ParameterTypes.Length)
                throw new InvalidDataException("Unsupported generic/vararg/instance CLR function-pointer calling convention");
            var shape = decoder.Function(signature.ParameterTypes.Select(parameter => parameter.Exact), signature.ReturnType.Exact);
            return new(Applied(FunctionPointerVocabulary.Pointer, decoder.Named(convention), shape));
        }
        public SignatureType GetGenericInstantiation(SignatureType generic, ImmutableArray<SignatureType> arguments)
        {
            var surface = decoder.GetGenericInstantiation(generic.Surface, arguments.Select(t => t.Surface).ToImmutableArray());
            // A real CLR Nullable<T> is not a Kotlin NRT annotation on T. Keep
            // its nominal constructor in native signatures, including open T.
            if (generic.Surface.HasClassName && decoder._names.ClassName(generic.Surface.ClassName) is "System.Nullable" or "System.Nullable1")
            {
                var exact = generic.Surface.Clone();
                exact.Argument.Add(arguments.Select(argument => new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Inv, Type = argument.Exact,
                }));
                return new(surface, exact, arguments.FirstOrDefault(t => t.Unsupported != null).Unsupported);
            }
            return new(surface, generic.Native is null && arguments.All(t => t.Native is null) ? null
                : decoder.GetGenericInstantiation(generic.Exact, arguments.Select(t => t.Exact).ToImmutableArray()),
                generic.Unsupported ?? arguments.FirstOrDefault(t => t.Unsupported != null).Unsupported);
        }
        public SignatureType GetModifiedType(SignatureType modifier, SignatureType type, bool required) => new(
            type.Surface, Applied(required ? FunctionPointerVocabulary.Required : FunctionPointerVocabulary.Optional,
                decoder.AsNonNull(modifier.Exact), type.Exact), type.Unsupported ?? modifier.Unsupported);
        public SignatureType GetPinnedType(SignatureType element) => element;
        bool ContainsPointer(KType type) => type.HasClassName && decoder._names.ClassName(type.ClassName) == FunctionPointerVocabulary.Pointer
            || type.Argument.Any(argument => argument.Type is { } nested && ContainsPointer(nested));
        public SignatureType GetPrimitiveType(PrimitiveTypeCode code) => new(decoder.GetPrimitiveType(code), code switch {
            PrimitiveTypeCode.IntPtr => decoder.Named("System.IntPtr"),
            PrimitiveTypeCode.UIntPtr => decoder.Named("System.UIntPtr"),
            PrimitiveTypeCode.TypedReference => decoder.Named("System.TypedReference"),
            _ => null,
        });
        public SignatureType GetGenericMethodParameter(GenericContext context, int index) => new(decoder.GetGenericMethodParameter(context, index));
        public SignatureType GetGenericTypeParameter(GenericContext context, int index) => new(decoder.GetGenericTypeParameter(context, index));
        public SignatureType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind) => new(
            decoder.GetTypeFromDefinition(reader, handle, kind),
            NativeNamed(decoder.DefinitionKotlinClassName(reader, handle), kind));
        public SignatureType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind) => new(
            decoder.GetTypeFromReference(reader, handle, kind),
            NativeNamed(decoder.ReferenceKotlinClassName(reader, handle), kind));
        KType? NativeNamed(int name, byte kind) => decoder._names.ClassName(name) == "kotlin.Unit"
            ? Applied(FunctionPointerVocabulary.Value, SignatureDecoder.Named(name))
            : kind == (byte)SignatureTypeKind.Unknown ? SignatureDecoder.Named(name) : null;
        public SignatureType GetTypeFromSpecification(MetadataReader reader, GenericContext context, TypeSpecificationHandle handle, byte kind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}

internal static class ProjectedSignatures
{
    static MethodSignature<KType> Surface(MethodSignature<SignatureDecoder.SignatureType> signature) => new(
        signature.Header, signature.ReturnType.Surface, signature.RequiredParameterCount,
        signature.GenericParameterCount, signature.ParameterTypes.Select(t => t.Surface).ToImmutableArray());

    public static MethodSignature<KType> DecodeProjectedSignature(this MethodDefinition method, SignatureDecoder decoder, GenericContext context) =>
        Surface(method.DecodeSignature(decoder.NativeProjectionProvider, context));
    public static MethodSignature<KType> DecodeProjectedSignature(this PropertyDefinition property, SignatureDecoder decoder, GenericContext context) =>
        Surface(property.DecodeSignature(decoder.NativeProjectionProvider, context));
    public static KType DecodeProjectedSignature(this FieldDefinition field, SignatureDecoder decoder, GenericContext context) =>
        field.DecodeSignature(decoder.NativeProjectionProvider, context).Surface;
    public static KType DecodeProjectedSignature(this TypeSpecification type, SignatureDecoder decoder, GenericContext context) =>
        type.DecodeSignature(decoder.NativeProjectionProvider, context).Surface;
    public static MethodSignature<KType> DecodeProjectedMethodSignature(this MemberReference member, SignatureDecoder decoder, GenericContext context) =>
        Surface(member.DecodeMethodSignature(decoder.NativeProjectionProvider, context));

    // Other decoders (raw identity comparisons, constraints) have no Kotlin
    // projection and continue using SRM's unchanged decoding contract.
    public static MethodSignature<T> DecodeProjectedSignature<T, C>(this MethodDefinition value, ISignatureTypeProvider<T, C> provider, C context) => value.DecodeSignature(provider, context);
    public static MethodSignature<T> DecodeProjectedSignature<T, C>(this PropertyDefinition value, ISignatureTypeProvider<T, C> provider, C context) => value.DecodeSignature(provider, context);
    public static T DecodeProjectedSignature<T, C>(this FieldDefinition value, ISignatureTypeProvider<T, C> provider, C context) => value.DecodeSignature(provider, context);
    public static T DecodeProjectedSignature<T, C>(this TypeSpecification value, ISignatureTypeProvider<T, C> provider, C genericContext) => value.DecodeSignature(provider, genericContext);
    public static MethodSignature<T> DecodeProjectedMethodSignature<T, C>(this MemberReference value, ISignatureTypeProvider<T, C> provider, C context) => value.DecodeMethodSignature(provider, context);
}
