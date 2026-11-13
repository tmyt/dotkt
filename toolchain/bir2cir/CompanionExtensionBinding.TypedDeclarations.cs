using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CompanionExtensionBinding
{
    static BirTypeParameter[] ReceiverTypeParameters(string receiver,
        IReadOnlyDictionary<string, IBirTypeDeclaration> localTypes, ReferenceMetadataIndex refs)
    {
        var local = localTypes.GetValueOrDefault(receiver);
        var source = local == null ? refs.OwnerTypeParametersInPhysicalFrame(receiver)?.ToArray()
            : local.CapturedTypeParams.OrEmpty().Concat(local.TypeParams.OrEmpty()).ToArray();
        var expected = local == null ? refs.OwnerArity(receiver)
            : local.CapturedTypeParams.OrEmpty().Length + local.TypeParams.OrEmpty().Length;
        if (expected == 0) return Array.Empty<BirTypeParameter>();
        if (source == null || source.Length != expected)
            throw new InvalidOperationException($"generic companion-extension receiver '{receiver}' has no exact type-parameter declarations");
        return source.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
    }

    static BirTypeParameter[] PhysicalReceiverTypeParameters(BirTypeParameter[] source) => source.Select((parameter, index) => {
        var copy = (BirTypeParameter)parameter.DeepClone(); copy.Name = "dotkt$receiver$" + index; copy.Variance = default; return copy;
    }).ToArray();

    static BirTypeParameter[] WrapperBlockTypeParameters(BirTypeParameter[] source, BirTypeParameter[] own)
    {
        var names = own.Select(parameter => parameter.Name.Value).ToHashSet(StringComparer.Ordinal);
        return source.Select((parameter, index) => {
            var copy = (BirTypeParameter)parameter.DeepClone();
            var name = "dotkt$receiver$" + index;
            while (!names.Add(name)) name += "$";
            copy.Name = name; copy.Variance = default;
            if (copy.Constraints.HasValue) copy.Constraints = copy.Constraints.Value.Select(constraint => RemapWrapperType(constraint, 0)).ToArray();
            return copy;
        }).ToArray();
    }

    static BirFunctionDeclaration WrapperImplementation(BirFunctionDeclaration core, string name, string coreName,
        string containerName, BirTypeParameter[] blockParameters, NullableRepresentationFrame receiverFrame)
    {
        var blockArity = blockParameters.Length;
        var wrapper = (BirFunctionDeclaration)core.DeepClone();
        wrapper.Name = name;
        SetPhysicalOnlyIdentity(wrapper, core, "wrapper");
        RemovePropertyIdentity(wrapper);
        wrapper.Generated = true; wrapper.Mods = new BirModifiers(); wrapper.InlineBir = default; wrapper.SuspendBridge = default;
        var own = core.TypeParams.OrEmpty();
        var block = WrapperBlockTypeParameters(blockParameters, own);
        wrapper.TypeParams = block.Concat(own.Select(parameter => {
            var copy = (BirTypeParameter)parameter.DeepClone();
            if (copy.Constraints.HasValue) copy.Constraints = copy.Constraints.Value.Select(constraint => RemapWrapperType(constraint, blockArity)).ToArray();
            return copy;
        })).ToArray();
        var ownFrame = core.NullableRepresentationFrame.OrDefault() ?? new NullableRepresentationFrame(own.Length, Array.Empty<int>());
        if (receiverFrame.PhysicalArity != blockArity || ownFrame.PhysicalArity != own.Length)
            throw new InvalidOperationException("Companion-extension declaration frame disagrees with its physical parameters");
        var frame = receiverFrame.Concat(ownFrame);
        wrapper.NullableRepresentationFrame = frame.RequiresMetadata ? OptionalField<NullableRepresentationFrame>.Of(frame) : default;
        foreach (var parameter in wrapper.Params.OrEmpty())
            if (parameter.Type.HasValue) parameter.Type = RemapWrapperType(parameter.Type.Value, blockArity);
        wrapper.Ret = RemapWrapperType(wrapper.Ret.Value, blockArity);
        var call = new BirCallStatic {
            Owner = new TypeNode.Fqn(containerName), OwnerType = new TypeNode.Fqn(containerName), Method = coreName,
            Sig = wrapper.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray(),
            Args = wrapper.Params.OrEmpty().Select(parameter => (IBirExpression)new BirLocal { Name = parameter.Name }).ToArray(),
            Ret = BirGraphValue.CopyType(wrapper.Ret.Value),
        };
        if (core.DeclarationId.HasValue) { call.DeclarationId = core.DeclarationId; wrapper.ExtensionCoreDeclarationId = core.DeclarationId; }
        else RoundtripMetadata.StampExtensionCore(wrapper, coreName);
        if (own.Length != 0) call.TypeArgs = Enumerable.Range(0, own.Length).Select(index => (TypeNode)new TypeNode.Tv("method", blockArity + index)).ToArray();
        wrapper.Body = wrapper.Ret.Value is TypeNode.Fqn { Name: "kotlin.Unit" or "void" }
            ? new IBirStatement[] { new BirExpressionStatement { Expr = call }, new BirReturn() }
            : new IBirStatement[] { new BirReturn { Value = call } };
        return wrapper;
    }

    static void MaterializeFieldBackedProperty(PropertyParts parts, string owner, string receiver)
    {
        var field = parts.Field;
        var name = parts.SourceName;
        var backing = PhysicalRoot(receiver, name) + "$storage";
        var type = BirGraphValue.CopyType(field.Type.Value);
        var visibility = field.Vis.OrDefault() ?? "public";
        if (!field.CompanionPropertyMutable.HasValue) throw new InvalidOperationException($"companion-extension storage '{owner}.{name}' has no val/var fact");
        var mutable = field.CompanionPropertyMutable.Value;
        var setterVisibility = mutable ? field.CompanionSetterVisibility.OrDefault()
            ?? throw new InvalidOperationException($"companion-extension var '{owner}.{name}' has no setter visibility") : null;
        if (!field.CompanionStorageReadOnly.HasValue) throw new InvalidOperationException($"companion-extension storage '{owner}.{name}' has no mutability fact");
        var readOnly = field.CompanionStorageReadOnly.Value;
        field.Name = backing; field.Vis = "private"; field.Static = true;
        var isConst = field.Const.OrDefault();
        if (readOnly && !isConst) field.InitOnly = true;
        field.CompanionPropertyMutable = default; field.CompanionSetterVisibility = default; field.CompanionStorageReadOnly = default;
        RemoveCompanionFacts(field);
        IBirExpression read = isConst ? field.Init.HasValue ? (IBirExpression)((BirGraphValue)field.Init.Value).DeepClone()
                : throw new InvalidOperationException($"const companion-extension property '{name}' has no value")
            : field.Lateinit.OrDefault() ? new BirLateinitGet {
                Sty = type, OwnerType = new TypeNode.Fqn(owner), Static = true, Name = backing, LateinitSourceName = name,
            } : new BirStaticFieldGet { Sty = type, OwnerType = new TypeNode.Fqn(owner), Name = backing };
        parts.Getter = Accessor(name, "get", visibility, new IBirStatement[] {
            new BirReturn { Value = OptionalField<IBirExpression>.Of(read) },
        }, Array.Empty<BirParameter>(), BirGraphValue.CopyType(type));
        CallableVisibilityLowering.Copy(field, parts.Getter);
        if (field.CompanionGetterExplicitClrName.HasValue) parts.Getter.ExplicitClrName = field.CompanionGetterExplicitClrName;
        if (field.CompanionGetterDeclarationId.HasValue) { parts.Getter.DeclarationId = field.CompanionGetterDeclarationId; parts.Getter.DeclarationSourceName = name; }
        if (isConst || field.Lateinit.OrDefault()) RoundtripMetadata.StampPropertyStorage(parts.Getter, owner, backing);
        if (mutable) parts.Setter = Accessor(name, "set", setterVisibility, new IBirStatement[] {
            new BirExpressionStatement { Expr = new BirStaticFieldSet {
                OwnerType = new TypeNode.Fqn(owner), Name = backing, Value = new BirLocal { Name = "value" },
            } },
        }, new[] { new BirParameter { Name = "value", Type = BirGraphValue.CopyType(type) } }, new TypeNode.Fqn("kotlin.Unit"));
        if (parts.Setter != null) {
            if (field.CompanionSetterExplicitClrName.HasValue) parts.Setter.ExplicitClrName = field.CompanionSetterExplicitClrName;
            if (field.CompanionSetterDeclarationId.HasValue) { parts.Setter.DeclarationId = field.CompanionSetterDeclarationId; parts.Setter.DeclarationSourceName = name; }
            CallableVisibilityLowering.Copy(field, parts.Setter, setterVisibility);
        }
        field.CompanionGetterExplicitClrName = default; field.CompanionSetterExplicitClrName = default;
        field.CompanionGetterDeclarationId = default; field.CompanionSetterDeclarationId = default;
    }

    static BirFunctionDeclaration Accessor(string name, string kind, string visibility, IBirStatement[] body,
        BirParameter[] parameters, TypeNode result) => new() {
        Name = name, PropertyName = name, PropertyAccessor = kind, Static = true, Override = false, Virtual = false,
        Abstract = false, SpecialName = false, Vis = visibility, Params = parameters, Ret = result, Body = body,
        Attrs = Array.Empty<BirAttribute>(),
    };

    static void PrepareImplementationAccessor(BirFunctionDeclaration accessor, string name, List<BirFunctionDeclaration> implementations)
    {
        accessor.Name = name; accessor.Static = true; accessor.Override = false; accessor.Virtual = false;
        accessor.Abstract = false; accessor.SpecialName = false;
        RemoveCompanionFacts(accessor); implementations.Add(accessor);
    }

    static BirFunctionDeclaration Signature(BirFunctionDeclaration source, string name, string markerName, bool specialName)
    {
        var signature = (BirFunctionDeclaration)source.DeepClone();
        signature.Name = name; SetPhysicalOnlyIdentity(signature, source, "signature");
        if (specialName) RemovePropertyIdentity(signature);
        signature.Static = true; signature.Override = false; signature.Virtual = false; signature.Abstract = false;
        signature.SpecialName = specialName; signature.BodyTerminates = true; signature.Mods = new BirModifiers();
        signature.Attrs = new[] { ExtensionMarker(markerName) }; signature.Body = ThrowStubBody();
        signature.Generated = default; signature.InlineBir = default; RemoveCompanionFacts(signature);
        return signature;
    }

    static TypeNode[] AccessorSignature(BirFunctionDeclaration accessor) => accessor.Params.OrEmpty().Select(parameter =>
        parameter.Type.HasValue ? BirGraphValue.CopyType(parameter.Type.Value)
            : throw new InvalidOperationException("companion-extension accessor parameter has no type")).ToArray();

    static void RemovePropertyIdentity(BirFunctionDeclaration value)
    {
        value.PropertyName = default; value.PropertyAccessor = default; value.PropertyAssociation = default;
    }
    static void RemoveCompanionFacts(BirFunctionDeclaration value)
    {
        value.CompanionReceiver = default; value.CompanionSourceName = default; value.CompanionMemberKind = default;
    }
    static void RemoveCompanionFacts(BirFieldDeclaration value)
    {
        value.CompanionReceiver = default; value.CompanionSourceName = default; value.CompanionMemberKind = default;
    }
    static void SetPhysicalOnlyIdentity(BirFunctionDeclaration target, BirFunctionDeclaration source, string role)
    {
        if (source.DeclarationId.HasValue) target.DeclarationId = DeclarationIdentityBinding.PhysicalOnlyId(source.DeclarationId.Value, role);
    }
    static IBirStatement[] ThrowStubBody() => new IBirStatement[] { new BirThrow {
        Value = new BirNewClr { Type = new TypeNode.Fqn("System.NotSupportedException"), ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() },
    } };
    static BirAttribute ExtensionAttribute() => Attribute("System.Runtime.CompilerServices.ExtensionAttribute");
    static BirAttribute ExtensionMarker(string name) => Attribute("System.Runtime.CompilerServices.ExtensionMarkerAttribute", new BirAttributeLiteral {
        Value = new BirLiteral.TextLiteral(name), Type = new TypeNode.Fqn("System.String"),
    });
    static BirAttribute Attribute(string name, params BirAttributeLiteral[] arguments) => new() {
        Attr = new TypeNode.Fqn(name), ArgTypes = arguments.Select(argument => BirGraphValue.CopyType(argument.Type.Value)).ToArray(),
        Args = arguments.Cast<IBirAttributeValue>().ToArray(), AttrExternal = true, AttrAssembly = "System.Runtime",
    };
}
