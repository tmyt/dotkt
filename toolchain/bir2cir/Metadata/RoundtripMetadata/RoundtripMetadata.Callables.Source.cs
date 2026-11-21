using MessagePack;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    internal static void CompleteMetadata(BirFileDeclaration file, bool runtimeMode)
    {
        if (file.RoundtripMetadataPending.OrDefault())
        {
            file.RoundtripMetadataPending = default;
            if (runtimeMode) file.Attrs = RuntimeAttributes(file.Attrs);
            else
            {
                var context = NrtMarker(ANullableCtx, HeaderMarker(ANullableCtx, "System.Byte", 1));
                file.Attrs = Add(Add(file.Attrs, context, prepend: true), HeaderMarker(AKFileClass));
            }
        }
        Methods(file.Methods.OrEmpty());
        Fields(file.Fields.OrEmpty(), topLevel: true);
        Properties(file.Properties.OrEmpty());
        foreach (var type in file.Types.OrEmpty())
        {
            Methods(type.Methods.OrEmpty());
            Fields(type.Fields.OrEmpty(), topLevel: false);
            Properties(type.Properties.OrEmpty());
            foreach (var constructor in type.Ctors.OrEmpty())
            {
                if (!constructor.RoundtripMetadataPending.OrDefault()) continue;
                constructor.RoundtripMetadataPending = default;
                if (runtimeMode) constructor.Attrs = RuntimeAttributes(constructor.Attrs);
                constructor.Attrs = StampIdentity(constructor.Attrs, constructor.RoundtripDeclarationIdentity.OrDefault(),
                    runtimeMode, constructor: true, physicalArity: 0);
                constructor.RoundtripDeclarationIdentity = default;
                Parameters(constructor.Params.OrEmpty());
            }
        }
        StampTypeHeaders(file, runtimeMode);

        void Methods(BirFunctionDeclaration[] methods)
        {
            foreach (var method in methods)
            {
                if (!method.RoundtripMetadataPending.OrDefault()) continue;
                method.RoundtripMetadataPending = default;
                int? identityPosition = null;
                if (runtimeMode)
                {
                    method.RetKotlinType = default;
                    method.SuspendResult = default;
                    method.ExistentialInnerConstructorFactory = default;
                    method.KotlinPropertyAccessorCarrier = default;
                    // An authored suspend key preserves a mods object made
                    // empty by DropModifier, regardless of the scalar's value.
                    method.Mods = RemoveExtensionRole(method.Mods, preserveEmpty: method.RoundtripSuspend.IsPresent);
                    method.Attrs = RuntimeAttributes(method.Attrs);
                    method.RetAttrs = RuntimeAttributes(method.RetAttrs);
                }
                else identityPosition = StampMethodMetadata(method);
                method.Attrs = StampIdentity(method.Attrs, method.RoundtripDeclarationIdentity.OrDefault(),
                    runtimeMode, constructor: false, method.TypeParams.OrEmpty().Length,
                    insertAt: identityPosition);
                method.RoundtripDeclarationIdentity = default;
                method.RoundtripMethodBounds = default;
                method.RoundtripSuspend = default;
                Parameters(method.Params.OrEmpty());
            }
        }

        void Parameters(BirParameter[] parameters)
        {
            foreach (var parameter in parameters)
            {
                if (!parameter.RoundtripMetadataPending.OrDefault()) continue;
                parameter.RoundtripMetadataPending = default;
                if (runtimeMode)
                {
                    parameter.KotlinType = default;
                    parameter.Mods = RemoveExtensionRole(parameter.Mods);
                    parameter.Attrs = RuntimeAttributes(parameter.Attrs);
                    continue;
                }
                var attributes = StampSlotPrefix(parameter.Attrs, parameter.NullableGeneric, parameter.SuspendFnType,
                    parameter.KotlinType, parameter.NullableFlags);
                parameter.NullableGeneric = default;
                parameter.KotlinType = default;
                if (HasReceiverFunction(parameter.Type.OrDefault())) attributes = Add(attributes, HeaderMarker(AKExtFn));
                if (parameter.CtxFnType.HasValue) attributes = Add(attributes, HeaderMarker(AKCtxFnType, "System.Int32", parameter.CtxFnType.Value));
                parameter.CtxFnType = default;
                if (parameter.Mods.OrDefault()?.Context.OrDefault() == true)
                {
                    attributes = Add(attributes, HeaderMarker(AKCtxParam));
                    parameter.Mods.Value.Context = default;
                    parameter.Mods = DropEmptyModifiers(parameter.Mods);
                }
                if (parameter.Mods.OrDefault()?.ExtensionReceiver.OrDefault() == true)
                {
                    attributes = Add(attributes, HeaderMarker(AKExtReceiver));
                    parameter.Mods = RemoveExtensionRole(parameter.Mods);
                }
                if (parameter.CollIdentity.HasValue) attributes = Add(attributes, TypeCarrier(AKCollIdentity, parameter.CollIdentity.Value));
                parameter.CollIdentity = default;
                parameter.Attrs = attributes;
            }
        }

        void Fields(BirFieldDeclaration[] fields, bool topLevel)
        {
            foreach (var field in fields)
            {
                if (!field.RoundtripMetadataPending.OrDefault()) continue;
                field.RoundtripMetadataPending = default;
                if (runtimeMode)
                {
                    field.KotlinType = default;
                    field.Attrs = RuntimeAttributes(field.Attrs);
                    continue;
                }
                var attributes = StampSlotPrefix(field.Attrs, field.NullableGeneric, field.SuspendFnType,
                    field.KotlinType, default);
                field.NullableGeneric = default;
                field.KotlinType = default;
                if ((!topLevel || field.CompanionReceiver.HasValue) && field.ReadOnly.OrDefault())
                    attributes = Add(attributes, HeaderMarker(AKReadOnly), prepend: true);
                attributes = Add(attributes, NullableAttribute(field.NullableFlags), prepend: true);
                if (HasReceiverFunction(field.Type.OrDefault())) attributes = Add(attributes, HeaderMarker(AKExtFn));
                if (field.CollIdentity.HasValue) attributes = Add(attributes, TypeCarrier(AKCollIdentity, field.CollIdentity.Value));
                field.CollIdentity = default;
                if (field.CompanionReceiver.HasValue)
                {
                    attributes = Add(attributes, CompanionExtension(field.CompanionReceiver.Value,
                        field.CompanionSourceName.OrDefault(), field.CompanionMemberKind.OrDefault()));
                    field.CompanionReceiver = default; field.CompanionSourceName = default; field.CompanionMemberKind = default;
                }
                if (field.Lateinit.OrDefault()) { attributes = Add(attributes, HeaderMarker(AKLateinit)); field.Lateinit = default; }
                field.Attrs = attributes;
            }
        }

        void Properties(BirPropertyDeclaration[] properties)
        {
            foreach (var property in properties)
            {
                if (!property.RoundtripMetadataPending.OrDefault()) continue;
                property.RoundtripMetadataPending = default;
                if (runtimeMode)
                {
                    property.KotlinType = default;
                    property.Attrs = RuntimeAttributes(property.Attrs);
                    continue;
                }
                var attributes = property.Attrs;
                if (property.NullableGeneric.HasValue) attributes = Add(attributes, TypeCarrier(AKNullableGen, property.NullableGeneric.Value), true);
                property.NullableGeneric = default;
                if (property.KotlinType.HasValue) attributes = Add(attributes, TypeCarrier(AKType, property.KotlinType.Value), true);
                property.KotlinType = default;
                if (property.SuspendFnType.HasValue) attributes = Add(attributes, TypeCarrier(AKSuspendFn, property.SuspendFnType.Value), true);
                attributes = Add(attributes, NullableAttribute(property.NullableFlags), true);
                if (HasReceiverFunction(property.Type.OrDefault())) attributes = Add(attributes, HeaderMarker(AKExtFn));
                if (property.CollIdentity.HasValue) attributes = Add(attributes, TypeCarrier(AKCollIdentity, property.CollIdentity.Value));
                property.CollIdentity = default;
                property.Attrs = attributes;
            }
        }
    }

    // Identity is ordered after the original attributes, lateinit, bounds and
    // inner-factory carriers, before property/function/inline carriers.
    static int StampMethodMetadata(BirFunctionDeclaration method)
    {
        var attributes = method.Attrs;
        if (method.Lateinit.OrDefault()) { attributes = Add(attributes, HeaderMarker(AKLateinit)); method.Lateinit = default; }
        if (method.RoundtripMethodBounds.HasValue)
            attributes = Add(attributes, PlacementCarrier(AKTypeParameterBounds, BirCarrier.EncodeMetadata(method.RoundtripMethodBounds.Value, SourceBoundsSnapshot.WriteMetadata)));
        if (method.ExistentialInnerConstructorFactory.HasValue)
        {
            attributes = Add(attributes, HeaderCarrier(AKInnerConstructorFactory, method.ExistentialInnerConstructorFactory.Value, EmbeddedBirMessagePack.WriteBirInnerConstructorFactory));
            method.ExistentialInnerConstructorFactory = default;
        }
        var identityPosition = attributes.OrEmpty().Length;
        if (method.KotlinPropertyAccessorCarrier.HasValue)
        {
            if (!attributes.OrEmpty().Any(attribute => (attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name == AKPropertyAccessor))
                attributes = Add(attributes, PropertyAccessorCarrier(method.KotlinPropertyAccessorCarrier.Value));
            method.KotlinPropertyAccessorCarrier = default;
        }
        var mods = method.Mods.OrDefault();
        var flags = method.FacadeModalityFlags | (mods?.Infix.OrDefault() == true ? 1 : 0) | (mods?.Operator.OrDefault() == true ? 2 : 0)
            | (method.RoundtripSuspend.OrDefault() || method.SuspendBridge.OrDefault() ? 4 : 0);
        if (attributes.OrEmpty().Any(attribute => (attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name == SourceLowPriority))
        {
            flags |= 8;
            // Removing only low-priority entries retains an explicitly empty vector.
            var removedBefore = attributes.OrEmpty().Take(identityPosition).Count(attribute => (attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name == SourceLowPriority);
            attributes = attributes.Value.Where(attribute => (attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name != SourceLowPriority).ToArray();
            identityPosition -= removedBefore;
        }
        if (flags != 0) attributes = Add(attributes, HeaderMarker(AKFunction, "System.Int32", flags));
        if (method.SuspendResult.HasValue)
        {
            attributes = Add(attributes, TypeCarrier(AKSuspendResult, method.SuspendResult.Value));
            method.SuspendResult = default;
        }
        if (mods?.Inline.OrDefault() == true && method.InlineBir.HasValue)
        {
            attributes = Add(attributes, PlacementCarrier(AKInline, method.InlineBir.Value, ExecutableBirCodec.Codec));
        }
        if (method.CompanionReceiver.HasValue)
        {
            attributes = Add(attributes, CompanionExtension(method.CompanionReceiver.Value,
                method.CompanionSourceName.OrDefault(), method.CompanionMemberKind.OrDefault()));
            method.CompanionReceiver = default; method.CompanionSourceName = default; method.CompanionMemberKind = default;
        }
        method.Attrs = attributes;
        var returns = new List<BirAttribute>();
        if (method.RetKotlinType.HasValue) { returns.Add(TypeCarrier(AKType, method.RetKotlinType.Value)); method.RetKotlinType = default; }
        if (NullableAttribute(method.RetNullableFlags) is { } nullable) returns.Add(nullable);
        if (method.RetSuspendFnType.HasValue) returns.Add(TypeCarrier(AKSuspendFn, method.RetSuspendFnType.Value));
        if (method.RetCtxFnType.HasValue) { returns.Add(HeaderMarker(AKCtxFnType, "System.Int32", method.RetCtxFnType.Value)); method.RetCtxFnType = default; }
        if (HasReceiverFunction(method.Ret.OrDefault())) returns.Add(HeaderMarker(AKExtFn));
        if (method.RetNothing.OrDefault()) returns.Add(HeaderMarker(AKNothing));
        if (method.NullableGenericRet.HasValue) { returns.Add(TypeCarrier(AKNullableGen, method.NullableGenericRet.Value)); method.NullableGenericRet = default; }
        if (method.CollIdentityRet.HasValue) { returns.Add(TypeCarrier(AKCollIdentity, method.CollIdentityRet.Value)); method.CollIdentityRet = default; }
        if (returns.Count != 0) method.RetAttrs = returns.ToArray();
        return identityPosition;
    }

    static OptionalField<BirAttribute[]> StampIdentity(OptionalField<BirAttribute[]> attributes,
        BirRoundtripDeclarationIdentity identity, bool runtimeMode, bool constructor, int physicalArity, int? insertAt = null)
    {
        if (identity?.DeclarationId.HasValue != true)
        {
            if (!runtimeMode && identity?.NullableRepresentationFrame.HasValue == true)
                throw new InvalidOperationException("Nullable representation frame requires declaration identity metadata");
            return attributes;
        }
        if (!runtimeMode && !constructor && identity.NullableRepresentationFrame.HasValue)
        {
            if (!identity.DeclarationSemanticSignature.HasValue) throw new InvalidOperationException("Nullable representation frame requires the original Kotlin signature");
            if (identity.NullableRepresentationFrame.Value.PhysicalArity != physicalArity)
                throw new InvalidOperationException("Nullable representation frame does not match emitted declaration arity");
        }
        var carrier = PlacementCarrier(AKDeclarationIdentity, BirCarrier.EncodeMetadata(identity,
            (ref MessagePackWriter writer, BirRoundtripDeclarationIdentity value) => {
                var source = !runtimeMode && !constructor;
                var allocated = value.DeclarationSignatureAllocation.OrDefault();
                var primary = constructor && value.ConstructorPrimary.HasValue;
                writer.WriteMapHeader(2 + (allocated ? 1 : 0) + (primary ? 1 : 0) + (source ?
                    (value.DeclarationSemanticSignature.HasValue ? 1 : 0) + (value.SemanticReifiedIndices.HasValue ? 1 : 0)
                    + (value.NullableWitnessIndices.HasValue ? 1 : 0) + (value.ReflectedWitnessIndices.HasValue ? 1 : 0)
                    + (value.NullableRepresentationFrame.HasValue ? 1 : 0) : 0));
                writer.Write("id"); IrMessagePack.WriteString(ref writer, value.DeclarationId.Value);
                writer.Write("name"); IrMessagePack.WriteString(ref writer, !runtimeMode && constructor ? ".ctor" : value.DeclarationSourceName.Value);
                if (allocated) { writer.Write("signatureAllocation"); writer.Write(true); }
                if (primary) { writer.Write("constructorPrimary"); writer.Write(value.ConstructorPrimary.Value); }
                if (!source) return;
                if (value.DeclarationSemanticSignature.HasValue) {
                    writer.Write("signature"); SourceDeclarationSignature.WriteMessagePack(ref writer, value.DeclarationSemanticSignature.Value, 1);
                }
                if (value.SemanticReifiedIndices.HasValue) SourceTypeFacts.WriteIndices(ref writer, "reified", value.SemanticReifiedIndices.Value);
                if (value.NullableWitnessIndices.HasValue) SourceTypeFacts.WriteIndices(ref writer, "nullableWitness", value.NullableWitnessIndices.Value);
                if (value.ReflectedWitnessIndices.HasValue) SourceTypeFacts.WriteIndices(ref writer, "reflectedWitness", value.ReflectedWitnessIndices.Value);
                if (value.NullableRepresentationFrame.HasValue) {
                    writer.Write(NullableRepresentationFrame.MetadataKey); SourceTypeFacts.WriteFrame(ref writer, value.NullableRepresentationFrame.Value);
                }
            }));
        if (insertAt is not int index) return Add(attributes, carrier);
        return attributes.OrEmpty().Take(index).Append(carrier).Concat(attributes.OrEmpty().Skip(index)).ToArray();
    }

    static OptionalField<BirAttribute[]> StampSlotPrefix(OptionalField<BirAttribute[]> attributes,
        OptionalField<TypeNode> generic, OptionalField<TypeNode> suspend, OptionalField<TypeNode> kotlin, OptionalField<int[]> nullable)
    {
        if (generic.HasValue) attributes = Add(attributes, TypeCarrier(AKNullableGen, generic.Value), true);
        if (suspend.HasValue) attributes = Add(attributes, TypeCarrier(AKSuspendFn, suspend.Value), true);
        if (kotlin.HasValue) attributes = Add(attributes, TypeCarrier(AKType, kotlin.Value), true);
        return Add(attributes, NullableAttribute(nullable), true);
    }

    static OptionalField<BirAttribute[]> Add(OptionalField<BirAttribute[]> attributes, BirAttribute attribute, bool prepend = false)
        => attribute == null ? attributes : OptionalField<BirAttribute[]>.Of(prepend
            ? new[] { attribute }.Concat(attributes.OrEmpty()).ToArray() : attributes.OrEmpty().Append(attribute).ToArray());

    static BirAttribute TypeCarrier(string name, TypeNode type) => PlacementCarrier(name,
        TypeNodeCarrier.Encode(TypeNodeCarrier.Codec, type), TypeNodeCarrier.Codec);
    static BirAttribute PropertyAccessorCarrier(EventPropertySourceFacts fact)
        => HeaderCarrier(AKPropertyAccessor, fact, EmbeddedBirMessagePack.WriteEventPropertySourceFacts);
    static BirAttribute NrtMarker(string name, BirAttribute attribute)
    { attribute.AttrExternal = true; attribute.AttrAssembly = "System.Runtime"; return attribute; }
    static BirAttribute NullableAttribute(OptionalField<int[]> flags)
    {
        if (!flags.HasValue || flags.Value.Length == 0) return null;
        if (flags.Value.Length == 1) return NrtMarker(ANullable, HeaderMarker(ANullable, "System.Byte", flags.Value[0]));
        return NrtMarker(ANullable, new BirAttribute {
            Attr = new TypeNode.Fqn(ANullable), ArgTypes = new TypeNode[] { new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
            Args = new IBirAttributeValue[] { new BirOpaqueBytes { Bytes = flags.Value.Select(value => (byte)value).ToArray() } },
        });
    }
    static BirAttribute CompanionExtension(TypeNode receiver, string name, string kind)
    {
        if (name == null || kind == null) throw new InvalidOperationException("companion extension has no source name or member kind");
        return PlacementCarrier(AKCompanionExt, BirCarrier.EncodeMetadata((receiver, name, kind),
            static (ref MessagePackWriter writer, (TypeNode receiver, string name, string kind) value) => {
                writer.WriteMapHeader(3); writer.Write("receiver"); TypeNodeMessagePack.Write(ref writer, value.receiver);
                writer.Write("name"); IrMessagePack.WriteString(ref writer, value.name);
                writer.Write("kind"); IrMessagePack.WriteString(ref writer, value.kind);
            }));
    }
    static bool HasReceiverFunction(TypeNode type) => type is TypeNode.Fn { Recv: not null, Suspend: false };
    static OptionalField<BirAttribute[]> RuntimeAttributes(OptionalField<BirAttribute[]> attributes)
    {
        if (!attributes.HasValue) return attributes;
        var kept = attributes.Value.Where(attribute => (attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name is not string name || !IsRuntimeStrippable(name)).ToArray();
        return kept.Length == 0 ? default : OptionalField<BirAttribute[]>.Of(kept);
    }
    static OptionalField<BirModifiers> RemoveExtensionRole(OptionalField<BirModifiers> mods, bool preserveEmpty = false)
    {
        if (!mods.HasValue) return mods;
        mods.Value.ExtensionReceiver = default;
        return preserveEmpty ? mods : DropEmptyModifiers(mods);
    }
    static OptionalField<BirModifiers> DropEmptyModifiers(OptionalField<BirModifiers> mods)
        => mods.HasValue && mods.Value.StructurallyEquals(new BirModifiers()) ? default : mods;
}
