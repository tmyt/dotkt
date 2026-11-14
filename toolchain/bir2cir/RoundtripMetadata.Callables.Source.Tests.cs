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
        var constructor = new BirConstructor { RoundtripMetadataPending = true,
            RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity {
                DeclarationId = "constructor", DeclarationSourceName = ".ctor", DeclarationSignatureAllocation = true,
            }, Params = Array.Empty<BirParameter>() };
        var owner = new BirClassDefinition { Name = "Probe", Methods = new[] { first, second }, Ctors = new[] { constructor } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        CompleteMetadata(file, runtimeMode: true);
        var firstPayload = AttributePayload(first.Attrs.Value.Single(attribute => AttributeName(attribute) == AKDeclarationIdentity));
        var secondPayload = AttributePayload(second.Attrs.Value.Single(attribute => AttributeName(attribute) == AKDeclarationIdentity));
        if (firstPayload["id"].GetValue<string>() != "first" || secondPayload["id"].GetValue<string>() != "second"
            || firstPayload.AsObject().Count != 3 || secondPayload.AsObject().Count != 2
            || !ReferenceEquals(first, ReferenceMetadataIndex.SelectDeclarationTwin(new[] { second, first },
                method => AttributePayload(method.Attrs.Value.Single(attribute => AttributeName(attribute) == AKDeclarationIdentity))["id"].GetValue<string>(), "first"))
            || !ReferenceEquals(first.Params.Value, slots) || !ReferenceEquals(slots[0].Type.Value, signature)
            || !slots[0].NullableGeneric.HasValue || slots[0].Mods.IsPresent || slots[0].Attrs.Value.Length != 1
            || !ReferenceEquals(slots[0].Attrs.Value[0], user) || first.RoundtripDeclarationIdentity.IsPresent
            || !first.Mods.HasValue || !first.Mods.Value.StructurallyEquals(new BirModifiers())
            || AttributePayload(constructor.Attrs.Value.Single())["signatureAllocation"].GetValue<bool>() != true)
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
            Params = new[] { new BirParameter { Name = "p", Type = type } } };
        var handoff = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Probe", Kind = "class", Methods = new[] { original } },
        } }.ToDom();
        var declaration = handoff["types"][0]["methods"][0].AsObject();
        var bounds = declaration[NullableGenericErasure.MethodTypeParameterBoundsPre];
        PrepareMetadata(handoff, runtimeMode: false);
        SuspendResidueLowering.DropModifier(handoff);
        if (declaration.ContainsKey(DeclarationIdentityBinding.Key) || declaration.ContainsKey("declarationSourceName")
            || declaration.ContainsKey(NullableGenericErasure.MethodTypeParameterBoundsPre)
            || !ReferenceEquals(bounds, declaration["roundtripMethodBounds"])
            || declaration["mods"].AsObject().Count != 0 || declaration["roundtripSuspend"].GetValue<bool>() != true
            || declaration["params"][0]["roundtripMetadataPending"].GetValue<bool>() != true)
            throw new InvalidOperationException("Metadata preparation lost original consumption clock or actual fact identity");
        var enumeration = new BirEnumDefinition { Kind = "enum", Methods = new[] { original } }.ToDom();
        var file = new JsonObject { ["types"] = new JsonArray(enumeration) };
        PrepareMetadata(file, runtimeMode: false);
        if (enumeration["methods"][0]["roundtripMetadataPending"] != null)
            throw new InvalidOperationException("Normal metadata granted enum members obligations beyond the original traversal");
        PrepareMetadata(file, runtimeMode: true);
        if (enumeration["methods"][0]["roundtripMetadataPending"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Runtime metadata lost its original enum-member visitation");
    }

    static string AttributeName(BirAttribute attribute) => ((TypeNode.Fqn)attribute.Attr.Value).Name;
    static JsonNode AttributePayload(BirAttribute attribute)
        => BirCarrier.DecodeBody(((BirLiteral.TextLiteral)((BirAttributeLiteral)attribute.Args.Value[0]).Value.Value).Value,
            ((BirOpaqueBytes)attribute.Args.Value[1]).Bytes.Value);
}
