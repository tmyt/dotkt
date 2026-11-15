using System.Text;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    internal static void RuntimeLinkageSelfTest()
    {
        var user = HeaderMarker("Probe.User");
        BirFunctionDeclaration RuntimeMethod(string id, string modifier) => new() {
            Name = "overloaded", RoundtripMetadataPending = true,
            RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity {
                DeclarationId = id, DeclarationSourceName = "overloaded", DeclarationSignatureAllocation = id == "first",
                SemanticReifiedIndices = Array.Empty<int>(),
            },
            Attrs = new[] { user, HeaderMarker(AKFunction) },
            Params = new[] { new BirParameter { Name = "value", RoundtripMetadataPending = true,
                Type = new TypeNode.Mod(false, new TypeNode.Fqn(modifier), new TypeNode.Fqn("System.Object")),
                NullableGeneric = new TypeNode.Nullable(new TypeNode.Tv("method", 0)),
                Mods = new BirModifiers { ExtensionReceiver = true }, Attrs = new[] { HeaderMarker(AKType), user } } },
        };
        var first = RuntimeMethod("first", "Probe.First");
        first.Mods = new BirModifiers();
        first.RoundtripSuspend = true;
        var second = RuntimeMethod("second", "Probe.Second");
        var slots = first.Params.Value;
        var signature = slots[0].Type.Value;
        var constructorSlot = new BirParameter { Name = "value", Type = signature };
        var constructorSlots = new[] { constructorSlot };
        var constructor = new BirConstructor { RoundtripMetadataPending = true,
            RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity {
                DeclarationId = "constructor", DeclarationSourceName = "sourceConstructor", DeclarationSignatureAllocation = true,
            }, Params = constructorSlots };
        var normalConstructor = (BirConstructor)constructor.DeepClone();
        var normalSlots = normalConstructor.Params.Value;
        var normalSlot = normalSlots[0];
        var normalSignature = normalSlot.Type.Value;
        CompleteMetadata(new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Probe.Normal", Kind = "class", Ctors = new[] { normalConstructor } },
        } }, runtimeMode: false);
        var normalPayload = AttributePayload(normalConstructor.Attrs.Value.Single());
        if (normalPayload["id"].GetValue<string>() != "constructor" || normalPayload["name"].GetValue<string>() != ".ctor"
            || normalPayload["signatureAllocation"].GetValue<bool>() != true || normalPayload.AsObject().Count != 3
            || !ReferenceEquals(normalConstructor.Params.Value, normalSlots) || !ReferenceEquals(normalSlots[0], normalSlot)
            || !ReferenceEquals(normalSlot.Type.Value, normalSignature) || normalConstructor.RoundtripDeclarationIdentity.IsPresent
            || normalConstructor.RoundtripMetadataPending.IsPresent)
            throw new InvalidOperationException("Normal constructor metadata lost .ctor naming, allocation ownership or exact parameter objects");
        var owner = new BirClassDefinition { Name = "Probe", Methods = new[] { first, second }, Ctors = new[] { constructor } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        CompleteMetadata(file, runtimeMode: true);
        var firstPayload = AttributePayload(first.Attrs.Value.Single(attribute => AttributeName(attribute) == AKDeclarationIdentity));
        var secondPayload = AttributePayload(second.Attrs.Value.Single(attribute => AttributeName(attribute) == AKDeclarationIdentity));
        var constructorPayload = AttributePayload(constructor.Attrs.Value.Single());
        if (firstPayload["id"].GetValue<string>() != "first" || secondPayload["id"].GetValue<string>() != "second"
            || firstPayload.AsObject().Count != 3 || secondPayload.AsObject().Count != 2
            || !ReferenceEquals(first, ReferenceMetadataIndex.SelectDeclarationTwin(new[] { second, first },
                method => AttributePayload(method.Attrs.Value.Single(attribute => AttributeName(attribute) == AKDeclarationIdentity))["id"].GetValue<string>(), "first"))
            || !ReferenceEquals(first.Params.Value, slots) || !ReferenceEquals(slots[0].Type.Value, signature)
            || !slots[0].NullableGeneric.HasValue || slots[0].Mods.IsPresent || slots[0].Attrs.Value.Length != 1
            || !ReferenceEquals(slots[0].Attrs.Value[0], user) || first.RoundtripDeclarationIdentity.IsPresent
            || !first.Mods.HasValue || !first.Mods.Value.StructurallyEquals(new BirModifiers())
            || constructorPayload["id"].GetValue<string>() != "constructor" || constructorPayload["name"].GetValue<string>() != "sourceConstructor"
            || constructorPayload["signatureAllocation"].GetValue<bool>() != true || constructorPayload.AsObject().Count != 3
            || !ReferenceEquals(constructor.Params.Value, constructorSlots) || !ReferenceEquals(constructorSlots[0], constructorSlot)
            || !ReferenceEquals(constructorSlot.Type.Value, signature) || constructor.RoundtripDeclarationIdentity.IsPresent)
            throw new InvalidOperationException("Typed runtime metadata lost exact declaration linkage, opaque slots, or nullable source facts");
        CallableMetadataSelfTest(user);
        Console.WriteLine("[typed callable metadata] self-test OK (runtime linkage, source payloads/order, exact eligibility and clock handoffs)");
    }

    static void CallableMetadataSelfTest(BirAttribute user)
    {
        var sourceType = new TypeNode.Fqn("Probe.Source");
        var source = new SourceDeclarationSignature(new[] { sourceType }, sourceType);
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>());
        var parameter = new BirParameter { RoundtripMetadataPending = true, Name = "p", Type = sourceType,
            KotlinType = sourceType, NullableGeneric = sourceType, SuspendFnType = sourceType,
            NullableFlags = new[] { 1, 2 }, CtxFnType = 2, CollIdentity = sourceType,
            Mods = new BirModifiers { Context = true, ExtensionReceiver = true }, Attrs = new[] { user } };
        var method = new BirFunctionDeclaration { RoundtripMetadataPending = true, Name = "physical",
            Attrs = new[] { user, HeaderMarker(SourceLowPriority) }, RetAttrs = new[] { user },
            RoundtripSuspend = true, Mods = new BirModifiers { Infix = true, Operator = true },
            RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity {
                DeclarationId = "method", DeclarationSourceName = "source", DeclarationSemanticSignature = source,
                DeclarationSignatureAllocation = false, SemanticReifiedIndices = Array.Empty<int>(), NullableWitnessIndices = new[] { 0 },
                NullableRepresentationFrame = frame,
            },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Params = new[] { parameter }, Ret = sourceType,
            Lateinit = true, RoundtripMethodBounds = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new[] { sourceType } }),
            ExistentialInnerConstructorFactory = new BirInnerConstructorFactory { Inner = "Probe.Inner", Declaration = "ctor-id", TypeArgs = new[] { -1, 0 } },
            KotlinPropertyAccessorCarrier = new EventPropertySourceFacts {
                Name = "value", Kind = "get", Association = "property", SourceAssociation = "source", Event = "native-event", Inherited = true,
            },
            RetKotlinType = sourceType, RetNullableFlags = new[] { 2 }, RetSuspendFnType = sourceType,
            RetCtxFnType = 1, RetNothing = true, NullableGenericRet = sourceType, CollIdentityRet = sourceType,
        };
        var lateParameter = new BirParameter { Name = "late", KotlinType = sourceType };
        var late = new BirFunctionDeclaration { Name = "late", Mods = new BirModifiers { Infix = true }, Params = new[] { lateParameter } };
        var property = new BirPropertyDeclaration { RoundtripMetadataPending = true, Name = "p", Type = sourceType,
            KotlinType = sourceType, NullableGeneric = sourceType, SuspendFnType = sourceType, NullableFlags = new[] { 1 }, Attrs = new[] { user } };
        var field = new BirFieldDeclaration { RoundtripMetadataPending = true, Name = "f", Type = sourceType, ReadOnly = true,
            KotlinType = sourceType, NullableGeneric = sourceType, SuspendFnType = sourceType, NullableFlags = new[] { 1 }, Attrs = new[] { user } };
        var methods = new[] { method, late };
        var file = new BirFileDeclaration { RoundtripMetadataPending = true, FileClass = "Probe", Methods = methods,
            Types = new IBirTypeDeclaration[] { new BirClassDefinition { Name = "Probe.Owner", Kind = "class", Methods = Array.Empty<BirFunctionDeclaration>(), Fields = new[] { field } } },
            Properties = new[] { property } };
        var encoded = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(encoded)) file.WriteJson(writer);
        if (!file.StructurallyEquals(BirGraphJson.ReadFile(encoded.WrittenSpan.ToArray()))
            || !file.StructurallyEquals(BirFileCodec.Decode(BirFileCodec.Encode(file))))
            throw new InvalidOperationException("Callable metadata handoffs lost current JSON/MessagePack source facts or explicit vectors");
        CompleteMetadata(file, runtimeMode: false);
        ExpectNames(method.Attrs.Value, "Probe.User", AKLateinit, AKTypeParameterBounds, AKInnerConstructorFactory, AKDeclarationIdentity, AKPropertyAccessor, AKFunction);
        ExpectNames(parameter.Attrs.Value, ANullable, AKType, AKSuspendFn, AKNullableGen, "Probe.User", AKCtxFnType, AKCtxParam, AKExtReceiver, AKCollIdentity);
        ExpectNames(property.Attrs.Value, ANullable, AKSuspendFn, AKType, AKNullableGen, "Probe.User");
        ExpectNames(field.Attrs.Value, ANullable, AKReadOnly, AKType, AKSuspendFn, AKNullableGen, "Probe.User");
        ExpectNames(method.RetAttrs.Value, AKType, ANullable, AKSuspendFn, AKCtxFnType, AKNothing, AKNullableGen, AKCollIdentity);
        foreach (var attribute in parameter.Attrs.Value.Concat(property.Attrs.Value).Concat(field.Attrs.Value).Concat(method.RetAttrs.Value))
            if (AttributeName(attribute) is var name && (name == AKType || name == AKNullableGen || name == AKSuspendFn || name == AKCollIdentity))
                ExpectBinaryTypePayload(attribute, sourceType);
        var suspend = new BirFunctionDeclaration { Name = "suspend", RoundtripMetadataPending = true, SuspendResult = sourceType };
        CompleteMetadata(new BirFileDeclaration { Methods = new[] { suspend } }, runtimeMode: false);
        ExpectBinaryTypePayload(suspend.Attrs.Value.Single(), sourceType);
        if (suspend.SuspendResult.IsPresent) throw new InvalidOperationException("Logical suspend payload was not consumed by its metadata writer");
        var identity = AttributePayload(method.Attrs.Value[4]);
        if (identity["signatureAllocation"] != null || identity["reified"].AsArray().Count != 0
            || identity["nullableFrame"] == null || TypeNode.Read(identity["signature"]["ret"]) != sourceType
            || method.RoundtripMethodBounds.IsPresent || method.RoundtripSuspend.IsPresent
            || method.ExistentialInnerConstructorFactory.IsPresent || method.RetKotlinType.IsPresent
            || parameter.Mods.IsPresent || late.Attrs.IsPresent || !lateParameter.KotlinType.HasValue
            || !ReferenceEquals(file.Methods.Value, methods) || !ReferenceEquals(method.Params.Value[0], parameter))
            throw new InvalidOperationException("Typed callable metadata changed presence, source payload, ownership or late-declaration eligibility");
        if (((BirLiteral.IntegerLiteral)((BirAttributeLiteral)method.Attrs.Value[6].Args.Value[0]).Value.Value).Value != 15)
            throw new InvalidOperationException("Typed callable metadata lost source function flags");
        var accessorBytes = ((BirOpaqueBytes)method.Attrs.Value[5].Args.Value[1]).Bytes.Value;
        if (Encoding.UTF8.GetString(accessorBytes) != "{\"name\":\"value\",\"kind\":\"get\",\"association\":\"property\",\"sourceAssociation\":\"source\",\"event\":\"native-event\",\"inherited\":true}")
            throw new InvalidOperationException("Typed accessor metadata changed selected field presence, values or carrier order");
        PreparationClockSelfTest(sourceType);
    }

    static void PreparationClockSelfTest(TypeNode type)
    {
        var original = new BirFunctionDeclaration { Name = "before", DeclarationId = "id", DeclarationSourceName = "source",
            Mods = new BirModifiers { Suspend = true }, MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new[] { type } }),
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "p", Type = type } } };
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>());
        var signature = new SourceDeclarationSignature(new[] { type }, type);
        var indices = Array.Empty<int>();
        original.DeclarationSignatureAllocation = false;
        original.DeclarationSemanticSignature = signature;
        original.SemanticReifiedIndices = indices;
        original.NullableWitnessIndices = OptionalField<int[]>.Null;
        original.NullableRepresentationFrame = frame;
        var constructor = new BirConstructor { DeclarationId = "ctor", DeclarationSourceName = "sourceCtor",
            DeclarationSignatureAllocation = true, Params = new[] { new BirParameter { Name = "p", Type = type } } };
        var owner = new BirClassDefinition { Name = "Probe", Kind = "class", Methods = new[] { original }, Ctors = new[] { constructor } };
        var nullIdentity = new BirFunctionDeclaration { Name = "nullIdentity", DeclarationId = OptionalField<string>.Null,
            DeclarationSourceName = OptionalField<string>.Null, DeclarationSignatureAllocation = OptionalField<bool>.Null,
            DeclarationSemanticSignature = OptionalField<SourceDeclarationSignature>.Null, SemanticReifiedIndices = OptionalField<int[]>.Null,
            NullableWitnessIndices = OptionalField<int[]>.Null, NullableRepresentationFrame = OptionalField<NullableRepresentationFrame>.Null,
            MethodTypeParameterBoundsPre = OptionalField<SourceBoundsSnapshot>.Null };
        var absentIdentity = new BirFunctionDeclaration { Name = "absent" };
        var fileMethod = new BirFunctionDeclaration { Name = "file", DeclarationId = "file-id" };
        var handoff = new BirFileDeclaration { Methods = new[] { fileMethod, nullIdentity, absentIdentity }, Types = new IBirTypeDeclaration[] { owner } };
        var bounds = original.MethodTypeParameterBoundsPre.Value;
        var methods = owner.Methods.Value;
        var calls = new List<BirFunctionDeclaration>();
        PrepareMetadata(handoff, runtimeMode: false, (file, actualOwner, method, pending) => {
            if (!ReferenceEquals(file, handoff) || !ReferenceEquals(method.RoundtripDeclarationIdentity.Value, pending)
                || !method.DeclarationId.HasValue || pending.DeclarationId.Value != method.DeclarationId.Value
                || (ReferenceEquals(method, fileMethod) ? actualOwner != null : !ReferenceEquals(actualOwner, owner)))
                throw new InvalidOperationException("Metadata identity custody was not reported while actual source/target were attached");
            calls.Add(method);
        });
        SuspendResidueLowering.DropModifier(handoff);
        var identity = original.RoundtripDeclarationIdentity.Value;
        if (!calls.SequenceEqual(new[] { fileMethod, original }) || !ReferenceEquals(owner.Methods.Value, methods)
            || original.DeclarationId.IsPresent || original.DeclarationSourceName.IsPresent
            || original.DeclarationSignatureAllocation.IsPresent || original.DeclarationSemanticSignature.IsPresent
            || original.SemanticReifiedIndices.IsPresent || original.NullableWitnessIndices.IsPresent || original.NullableRepresentationFrame.IsPresent
            || original.MethodTypeParameterBoundsPre.IsPresent || !ReferenceEquals(bounds, original.RoundtripMethodBounds.Value)
            || !original.Mods.Value.StructurallyEquals(new BirModifiers()) || original.RoundtripSuspend.Value != true
            || original.Params.Value[0].RoundtripMetadataPending.Value != true
            || identity.DeclarationSignatureAllocation.IsNull || identity.DeclarationSignatureAllocation.Value != false
            || !ReferenceEquals(identity.DeclarationSemanticSignature.Value, signature) || !ReferenceEquals(identity.SemanticReifiedIndices.Value, indices)
            || !identity.NullableWitnessIndices.IsNull || !ReferenceEquals(identity.NullableRepresentationFrame.Value, frame)
            || constructor.DeclarationId.IsPresent || constructor.DeclarationSourceName.IsPresent || constructor.DeclarationSignatureAllocation.IsPresent
            || constructor.RoundtripDeclarationIdentity.Value.DeclarationId.Value != "ctor"
            || constructor.RoundtripDeclarationIdentity.Value.DeclarationSourceName.Value != "sourceCtor"
            || constructor.RoundtripDeclarationIdentity.Value.DeclarationSignatureAllocation.Value != true
            || constructor.RoundtripDeclarationIdentity.Value.DeclarationSemanticSignature.IsPresent
            || !constructor.Params.Value[0].RoundtripMetadataPending.Value)
            throw new InvalidOperationException("Metadata preparation lost original consumption clock or actual fact identity");
        var nulls = nullIdentity.RoundtripDeclarationIdentity.Value;
        if (!nulls.DeclarationId.IsNull || !nulls.DeclarationSourceName.IsNull || !nulls.DeclarationSignatureAllocation.IsNull
            || !nulls.DeclarationSemanticSignature.IsNull || !nulls.SemanticReifiedIndices.IsNull || !nulls.NullableWitnessIndices.IsNull
            || !nulls.NullableRepresentationFrame.IsNull || !nullIdentity.RoundtripMethodBounds.IsNull
            || nullIdentity.DeclarationId.IsPresent || absentIdentity.RoundtripDeclarationIdentity.IsPresent)
            throw new InvalidOperationException("Metadata preparation collapsed explicit-null identity/frame facts into absence");
        var transported = BirFileCodec.Decode(BirFileCodec.Encode(handoff));
        if (!transported.Methods.Value[1].RoundtripDeclarationIdentity.Value.StructurallyEquals(nulls)
            || transported.Methods.Value[2].RoundtripDeclarationIdentity.IsPresent)
            throw new InvalidOperationException("Metadata custody transport changed explicit-null versus absent identity fields");
        var enumMethod = new BirFunctionDeclaration { Name = "enumMember", Static = true, DeclarationId = "enum-id",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, MethodTypeParameterBoundsPre = bounds };
        var enumFacts = SourceTypeFacts.Empty;
        var enumeration = new BirEnumDefinition { Kind = "enum", Methods = new[] { enumMethod }, KotlinSupertypesPre = enumFacts };
        var enumFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { enumeration } };
        PrepareMetadata(enumFile, runtimeMode: false, (_, _, _, _) =>
            throw new InvalidOperationException("Normal metadata unexpectedly visited a native enum member"));
        if (enumMethod.RoundtripMetadataPending.IsPresent || !enumeration.RoundtripTypeHeaderPending.Value
            || !ReferenceEquals(enumeration.KotlinSupertypesPre.Value, enumFacts)
            || !ReferenceEquals(enumMethod.MethodTypeParameterBoundsPre.Value, bounds))
            throw new InvalidOperationException("Normal metadata granted enum members obligations beyond the original traversal");
        var enumCalls = 0;
        PrepareMetadata(enumFile, runtimeMode: true, (file, actualOwner, method, pending) => {
            if (!ReferenceEquals(file, enumFile) || !ReferenceEquals(actualOwner, enumeration) || !ReferenceEquals(method, enumMethod)
                || method.DeclarationId.Value != "enum-id" || !ReferenceEquals(method.RoundtripDeclarationIdentity.Value, pending))
                throw new InvalidOperationException("Runtime enum metadata lost its actual method custody");
            enumCalls++;
        });
        if (enumCalls != 1 || enumMethod.RoundtripMetadataPending.OrDefault() != true || enumeration.KotlinSupertypesPre.IsPresent
            || enumMethod.DeclarationId.IsPresent || enumMethod.MethodTypeParameterBoundsPre.IsPresent || enumMethod.RoundtripMethodBounds.IsPresent)
            throw new InvalidOperationException("Runtime metadata lost its original enum-member visitation");
    }

    static string AttributeName(BirAttribute attribute) => ((TypeNode.Fqn)attribute.Attr.Value).Name;
    static void ExpectBinaryTypePayload(BirAttribute attribute, TypeNode expected)
    {
        if (attribute.Args.Value[0] is not BirAttributeLiteral { Value.Value: BirLiteral.TextLiteral { Value: TypeNodeCarrier.Codec } }
            || attribute.Args.Value[1] is not BirOpaqueBytes bytes
            || TypeNodeCarrier.Decode(TypeNodeCarrier.Codec, bytes.Bytes.Value) != expected
            || !bytes.Bytes.Value.SequenceEqual(TypeNodeCarrier.Encode(TypeNodeCarrier.Codec, expected)))
            throw new InvalidOperationException("Callable type metadata did not use the exact owning MessagePack carrier");
    }
    static JsonNode AttributePayload(BirAttribute attribute)
        => BirCarrier.DecodeBody(((BirLiteral.TextLiteral)((BirAttributeLiteral)attribute.Args.Value[0]).Value.Value).Value,
            ((BirOpaqueBytes)attribute.Args.Value[1]).Bytes.Value);
}
