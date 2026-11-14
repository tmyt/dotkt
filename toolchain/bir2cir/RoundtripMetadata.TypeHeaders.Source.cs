using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    // PrepareMetadata grants this capability at the declaration metadata phase.
    // In particular, types synthesized by CapturedGenericInvocation afterwards
    // must not acquire a source header from their mere presence at this seam.
    internal static void StampTypeHeaders(BirFileDeclaration file, bool runtimeMode)
    {
        foreach (var declaration in file.Types.OrEmpty())
        {
            var type = (IBirTypeDeclaration)declaration;
            if (!HeaderPending(type)) continue;
            ClearHeaderPending(type);
            if (runtimeMode) StripRuntimeTypeHeader(type);
            else StampTypeHeader(type);
        }
    }

    static bool HeaderPending(IBirTypeDeclaration type) => type switch {
        BirClassDefinition value => value.RoundtripTypeHeaderPending.OrDefault(),
        BirInterfaceDefinition value => value.RoundtripTypeHeaderPending.OrDefault(),
        BirEnumDefinition value => value.RoundtripTypeHeaderPending.OrDefault(),
        _ => false,
    };

    static void ClearHeaderPending(IBirTypeDeclaration type)
    {
        switch (type)
        {
            case BirClassDefinition value: value.RoundtripTypeHeaderPending = default; break;
            case BirInterfaceDefinition value: value.RoundtripTypeHeaderPending = default; break;
            case BirEnumDefinition value: value.RoundtripTypeHeaderPending = default; break;
        }
    }

    static SourceTypeFacts HeaderSupertypes(IBirTypeDeclaration type) => type switch {
        BirClassDefinition value => value.RoundtripSupertypesPre.OrDefault(),
        BirInterfaceDefinition value => value.RoundtripSupertypesPre.OrDefault(),
        _ => null,
    };

    static void ClearHeaderSupertypes(IBirTypeDeclaration type)
    {
        switch (type)
        {
            case BirClassDefinition value: value.RoundtripSupertypesPre = default; break;
            case BirInterfaceDefinition value: value.RoundtripSupertypesPre = default; break;
        }
    }

    static void AppendHeader(IBirTypeDeclaration type, BirAttribute attribute)
        => type.Attrs = type.Attrs.OrEmpty().Append(attribute).ToArray();

    static BirAttribute HeaderMarker(string name, string argumentType = null, int value = 0) => new() {
        Attr = new TypeNode.Fqn(name),
        ArgTypes = argumentType == null ? Array.Empty<TypeNode>() : new TypeNode[] { new TypeNode.Fqn(argumentType) },
        Args = argumentType == null ? Array.Empty<IBirAttributeValue>() : new IBirAttributeValue[] {
            new BirAttributeLiteral { Type = new TypeNode.Fqn(argumentType), Value = new BirLiteral.IntegerLiteral(value) },
        },
    };

    static BirAttribute HeaderCarrier(string name, BirGraphValue fact)
        => PlacementCarrier(name, BirCarrier.EncodeBody(BirCarrier.JsonV1, fact, (writer, value) => value.WriteJson(writer)));

    static void StampTypeHeader(IBirTypeDeclaration type)
    {
        if (type is BirInterfaceDefinition { ManagedReferenceContract.HasValue: true } contract)
        {
            AppendHeader(type, HeaderCarrier(AKManagedReferenceContract, contract.ManagedReferenceContract.Value));
            contract.ManagedReferenceContract = default;
        }
        // Preserve producer-owned ordinals before the CLR-enum early return;
        // downstream consumers must not infer them from FieldDef ordering.
        StampBasicEnumHeader(type);
        if (type.Kind.OrDefault() == "enum") return;

        var context = HeaderMarker(ANullableCtx, "System.Byte", 1);
        context.AttrExternal = true;
        context.AttrAssembly = "System.Runtime";
        type.Attrs = new[] { context }.Concat(type.Attrs.OrEmpty()).ToArray();
        var mods = type.Mods.OrDefault();
        if (mods?.Fun.OrDefault() == true) AppendHeader(type, HeaderMarker(AKFunInterface));
        if (mods?.Sealed.OrDefault() == true) AppendHeader(type, HeaderMarker(AKSealed));
        // KotlinValue carries source value-class identity independently of a
        // backend annotation or the erased CLR representation.
        if (mods?.Value.OrDefault() == true) AppendHeader(type, HeaderMarker(AKValue));
        if (mods?.Object.OrDefault() == true) AppendHeader(type, HeaderMarker(AKObject));
        if (mods?.Inner.OrDefault() == true)
            AppendHeader(type, HeaderMarker(AKInner, "System.Int32", type.CapturedTypeParams.OrEmpty().Length));
        if (type is BirClassDefinition { RichEnum.HasValue: true } rich)
        {
            AppendHeader(type, HeaderCarrier(AKRichEnum, rich.RichEnum.Value));
            rich.RichEnum = default;
            rich.EnumRich = default;
        }
        if (type is BirClassDefinition { KotlinCompanion.HasValue: true })
            throw new InvalidOperationException("semantic companion reached metadata emission without representation lowering");
        if (type.CompanionCarrier.HasValue)
        {
            if (!type.CompanionCarrier.Value.Kind.HasValue)
                throw new InvalidOperationException("semantic companion reached metadata emission without representation lowering");
            AppendHeader(type, HeaderCarrier(AKCompanion, type.CompanionCarrier.Value));
            type.CompanionCarrier = default;
        }
        if (type.StaticCarrier.HasValue)
        {
            AppendHeader(type, HeaderCarrier(AKStaticCarrier, type.StaticCarrier.Value));
            type.StaticCarrier = default;
        }
        var source = HeaderSupertypes(type);
        var frame = source?.NullableFrame;
        var offset = type.CapturedTypeParams.OrEmpty().Length;
        var parameters = type.TypeParams.OrEmpty();
        if (frame != null && offset + parameters.Length > frame.PhysicalArity)
            throw new InvalidOperationException($"Type '{type.Name.OrDefault()}' has {offset + parameters.Length} physical generic parameters "
                + $"but its declaration frame describes only {frame.PhysicalArity}");
        // CLR class variance is weaker than Kotlin's. Preserve authored
        // variance by source-frame index, not emitted GenericParam layout.
        var variances = new Dictionary<int, string>();
        for (var index = 0; index < parameters.Length; index++)
            if (parameters[index].Variance.OrDefault() is "in" or "out" &&
                (frame == null ? (int?)(offset + index) : frame.SourceIndex(offset + index)) is int sourceIndex)
                variances[sourceIndex] = parameters[index].Variance.Value;
        if (variances.Count != 0) source = (source ?? SourceTypeFacts.Empty).MergeVariances(variances);
        if (source != null)
        {
            if (frame != null && frame.PhysicalArity != offset + parameters.Length)
                throw new InvalidOperationException("Nullable representation frame does not match emitted declaration arity");
            // KotlinSupertypes restores pre-erasure edges, bounds and variance;
            // physical signature rewriting must not reinterpret this payload.
            AppendHeader(type, PlacementCarrier(AKSupertypes,
                BirCarrier.EncodeBody(BirCarrier.JsonV1, source, (writer, value) => value.WritePayload(writer))));
            ClearHeaderSupertypes(type);
        }
        StampRepresentationTypeHeader(type);
    }

    static void StampBasicEnumHeader(IBirTypeDeclaration type)
    {
        if (type is not BirEnumDefinition { BasicEnum.HasValue: true } enumeration) return;
        var physical = enumeration.Entries.OrEmpty();
        var semantic = enumeration.BasicEnum.Value.Entries.OrEmpty();
        if (!enumeration.Entries.HasValue || !enumeration.BasicEnum.Value.Entries.HasValue || physical.Length != semantic.Length)
            throw new InvalidOperationException("basic-enum metadata does not match its physical entry declarations");
        for (var index = 0; index < physical.Length; index++)
        {
            if (physical[index].Name.OrDefault() != semantic[index].Name.OrDefault() || semantic[index].Ordinal.Value != index)
                throw new InvalidOperationException("basic-enum metadata entry does not match its physical declaration");
            physical[index].Attrs = physical[index].Attrs.OrEmpty().Append(HeaderMarker(AKBasicEnumOrdinal, "System.Int32", index)).ToArray();
        }
        AppendHeader(type, HeaderCarrier(AKBasicEnum, enumeration.BasicEnum.Value));
        enumeration.BasicEnum = default;
    }

    static void StampRepresentationTypeHeader(IBirTypeDeclaration type)
    {
        if (type is not BirInterfaceDefinition { KotlinType.HasValue: true } representation) return;
        // KotlinType associates a compiler-synthesized physical declaration
        // with its logical Kotlin type (for example an existential interface).
        AppendHeader(type, PlacementCarrier(AKType, BirCarrier.EncodeBody(BirCarrier.JsonV1,
            representation.KotlinType.Value, TypeNode.WriteJson)));
        representation.KotlinType = default;
    }

    static void StripRuntimeTypeHeader(IBirTypeDeclaration type)
    {
        if (type.Attrs.HasValue)
        {
            var kept = type.Attrs.Value.Where(attribute => (attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name is not string name
                || !IsRuntimeStrippable(name)).ToArray();
            type.Attrs = kept.Length == 0 ? default : OptionalField<BirAttribute[]>.Of(kept);
        }
        type.CompanionCarrier = default;
        type.StaticCarrier = default;
        if (type is BirClassDefinition value) value.KotlinCompanion = default;
        ClearHeaderSupertypes(type);
        StampRepresentationTypeHeader(type);
        if (type is BirInterfaceDefinition representation) representation.KotlinType = default;
        if (type is BirClassDefinition { RichEnum.HasValue: true } rich)
        {
            // Generic enum operations also need this exact API map at runtime,
            // including for stdlib-owned enums; it is not reference-only metadata.
            AppendHeader(type, HeaderCarrier(AKRichEnum, rich.RichEnum.Value));
        }
        if (type is BirClassDefinition runtimeClass)
        {
            runtimeClass.RichEnum = default;
            runtimeClass.EnumRich = default;
        }
        StampBasicEnumHeader(type);
    }

    internal static bool HasRepresentationTypeAssociations(BirFileDeclaration file)
        => file.Types.OrEmpty().Cast<IBirTypeDeclaration>().Any(type => type.Attrs.OrEmpty()
            .Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: AKType }));
}
