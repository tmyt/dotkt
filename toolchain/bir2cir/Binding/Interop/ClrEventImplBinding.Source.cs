using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEventImplBinding
{
    internal static BirFileDeclaration BindImplementations(BirFileDeclaration file,
        ReferenceMetadataIndex refs, LocalEvents locals)
    {
        locals.IndexSource(file);
        foreach (var owner in file.Types.OrEmpty()) BindType(owner, refs, locals);
        return file;
    }

    static BirFunctionDeclaration FindAccessor(IEnumerable<BirFunctionDeclaration> methods, string name, string kind) =>
        methods.FirstOrDefault(method => method.Body.HasValue
            && method.Body.Value.FirstOrDefault() is BirClrEventAccessor marker
            && IsEventAccessor(method.Name.OrDefault(), name, method.Body.Value.Length,
                marker.Tag, marker.Kind.OrDefault(), kind));

    static void BindType(IBirTypeDeclaration type, ReferenceMetadataIndex refs, LocalEvents locals)
    {
        var backings = type.ClrEvents.OrEmpty();
        var forwarders = type is BirClassDefinition cls ? cls.ClrEventForwarders.OrEmpty()
            : Array.Empty<BirClrEventForwarder>();
        if (backings.Length == 0 && forwarders.Length == 0) return;
        var methods = type.Methods.OrEmpty();
        var fields = type.Fields.OrEmpty().ToList();
        var declarations = new List<BirClrEventBacking>();
        foreach (var backing in backings)
        {
            var name = backing.Name.OrDefault();
            if (name == null) continue;
            var isStatic = backing.Static.OrDefault();
            var add = FindAccessor(methods, "add_" + name, "add");
            var remove = FindAccessor(methods, "remove_" + name, "remove");
            var raise = FindAccessor(methods, "raise_" + name, "raise");
            if (add == null || remove == null || raise == null)
                throw new InvalidOperationException($"bir2cir: clrEvent '{name}' is missing a synthesized add/remove/raise accessor — kotc synthesis defect");
            var classOwner = backing.ClassOverrideOwner.OrDefault() as TypeNode.Fqn;
            var openEvent = backing.Modality.OrDefault() switch {
                "open" => true, "final" => false,
                _ => throw new InvalidOperationException($"bir2cir: clrEvent '{name}' has no concrete source modality"),
            };
            if (backing.PropertyAssociation.HasValue)
                foreach (var getter in methods.Where(method =>
                    method.PropertyAssociation.OrDefault() == backing.PropertyAssociation.Value
                    && method.PropertyAccessor.OrDefault() == "get"))
                    getter.ClrFinal = !openEvent && getter.Virtual.OrDefault();
            var baseType = classOwner == null ? null : ClrMemberResolution.ResolveOwnerType(classOwner, refs);
            EventInfo baseEvent = null;
            TypeNode[] baseArguments = Array.Empty<TypeNode>();
            if (baseType != null && !TryResolveDeclaredEvent(baseType,
                classOwner.Args ?? Array.Empty<TypeNode>(), name, out baseEvent, out baseArguments))
                throw new InvalidOperationException($"bir2cir: overridden class event '{classOwner.Name}.{name}' was not found");
            var (delegateType, invokeParameters, signature, nativeSignature) =
                ResolveImplementationDelegate(name, add, backing, refs, locals);
            if (backing.PolicyField.HasValue)
                RewritePolicy(type, fields, name, backing.PolicyField.Value, add, remove, raise,
                    delegateType, invokeParameters, refs);
            else
            {
                var fieldName = name + "$delegate";
                fields.Add(new BirFieldDeclaration { Name = fieldName, Type = Copy(delegateType),
                    Vis = "private", Static = isStatic });
                RewriteFieldLike(add, "add", fieldName, delegateType);
                RewriteFieldLike(remove, "remove", fieldName, delegateType);
                RewriteRaise(raise, fieldName, delegateType, invokeParameters);
            }

            void Slot(BirFunctionDeclaration method, MethodInfo externalSlot, bool reusesSlot, bool needsInterfaceSlot)
            {
                var isVirtual = !isStatic && (reusesSlot || openEvent || needsInterfaceSlot);
                method.Override = !isStatic && reusesSlot;
                method.Virtual = isVirtual;
                method.ClrFinal = isVirtual && !openEvent;
                if (externalSlot == null) return;
                var parameters = externalSlot.GetParameters().Select(parameter =>
                    SupertypeGraph.SubstOwnerTvs(ReferenceMetadataIndex.DeclarationSignatureSlot(parameter), baseArguments)).ToArray();
                for (var i = 0; i < parameters.Length; i++)
                {
                    method.Params.Value[i].Type = Copy(parameters[i]);
                    method.Params.Value[i].FunctionSignatureType = default;
                }
                method.ClrBaseImpls = new[] { new BirMethodImpl {
                    Owner = new TypeNode.Fqn(externalSlot.DeclaringType.FullName,
                        baseArguments.Length == 0 ? null : baseArguments.Select(Copy).ToArray()),
                    Member = externalSlot.Name, Arity = 0, Params = parameters.Select(Copy).ToArray(),
                    Ret = SupertypeGraph.SubstOwnerTvs(
                        ReferenceMetadataIndex.DeclarationSignatureSlot(externalSlot.ReturnParameter), baseArguments),
                } };
            }
            MethodInfo Overridable(MethodInfo method) => baseEvent?.DeclaringType?.IsInterface == false
                && method is { IsVirtual: true, IsFinal: false }
                && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly) ? method : null;
            var localBase = classOwner != null && baseType == null;
            if (localBase)
                foreach (var method in new[] { add, remove })
                {
                    var parameter = method.Params.Value[0];
                    parameter.FunctionSignatureType = default;
                    if (nativeSignature != null) parameter.Type = Copy(nativeSignature);
                    else if (signature != null) parameter.FunctionSignatureType = Copy(signature);
                }
            var baseAdd = Overridable(baseEvent?.GetAddMethod(true));
            var baseRemove = Overridable(baseEvent?.GetRemoveMethod(true));
            var baseRaise = Overridable(baseEvent?.GetRaiseMethod(true));
            Slot(add, baseAdd, localBase || baseAdd != null, true);
            Slot(remove, baseRemove, localBase || baseRemove != null, true);
            Slot(raise, baseRaise, localBase || baseRaise != null, false);
            // Physical-only identity is the same scalar event-accessor identity used by late subscriptions.
            var identity = DeclarationIdentityBinding.PhysicalOnlyId(
                "clr-event-accessor:" + EventAccessorIdentity(type.Name.OrDefault(), raise.Name.OrDefault()), "accessor");
            raise.DeclarationId = identity;
            declarations.Add(new BirClrEventBacking { K = "clrEventDecl", Name = name,
                DelegateType = Copy(delegateType), EventRaiseDeclarationId = identity });
        }
        foreach (var forwarder in forwarders)
        {
            var name = forwarder.Name.OrDefault();
            if (name == null) continue;
            var add = FindAccessor(methods, "add_" + name, "add");
            var remove = FindAccessor(methods, "remove_" + name, "remove");
            if (add == null || remove == null)
                throw new InvalidOperationException($"bir2cir: delegated clrEvent '{name}' is missing a synthesized add/remove forwarder — kotc synthesis defect");
            var (delegateType, _) = ResolveDelegate(name, add.Overrides.OrEmpty(), null, refs, forwarder.OwnerType.OrDefault());
            RewriteForwarder(add, "add", forwarder, delegateType);
            RewriteForwarder(remove, "remove", forwarder, delegateType);
            declarations.Add(new BirClrEventBacking { K = "clrEventDecl", Name = name, DelegateType = Copy(delegateType) });
        }
        type.Fields = fields.ToArray();
        type.ClrEvents = declarations.ToArray();
        if (type is BirClassDefinition classType) classType.ClrEventForwarders = default;
    }

    static TypeNode Copy(TypeNode type) => BirGraphValue.CopyType(type);

    // This is scalar identity serialization, not a graph/metadata roundtrip.
    static string EventAccessorIdentity(string owner, string name)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        { writer.WriteStartArray(); writer.WriteStringValue(owner); writer.WriteStringValue(name); writer.WriteEndArray(); }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    static (TypeNode Delegate, TypeNode[] Parameters, TypeNode Signature, TypeNode NativeSignature)
        ResolveImplementationDelegate(string name, BirFunctionDeclaration add, BirClrEventBacking backing,
            ReferenceMetadataIndex refs, LocalEvents locals)
    {
        if (backing.ClassOverrideOwner.OrDefault() is TypeNode.Fqn owner
            && locals.TryGetSource(owner, name, out var declaration))
        {
            var (type, parameters, signature, nativeSignature) =
                ResolveImplementationDelegate(name, declaration.Add, declaration.Backing, refs, locals);
            var arguments = owner.Args ?? Array.Empty<TypeNode>();
            return (SubstituteOwnerTypeVariables(type, arguments),
                parameters.Select(parameter => SubstituteOwnerTypeVariables(parameter, arguments)).ToArray(),
                signature == null ? null : SubstituteOwnerTypeVariables(signature, arguments),
                nativeSignature == null ? null : SupertypeGraph.SubstOwnerTvs(nativeSignature, arguments));
        }
        var (delegateType, invokeParameters) = ResolveDelegate(name, add.Overrides.OrEmpty(),
            backing.HandlerType.OrDefault(), refs, policyArguments: backing.PolicyField.HasValue);
        if (backing.ClassOverrideOwner.OrDefault() is TypeNode.Fqn externalOwner)
        {
            var type = ClrMemberResolution.ResolveOwnerType(externalOwner, refs);
            if (type == null || !TryResolveDeclaredEvent(type, externalOwner.Args ?? Array.Empty<TypeNode>(), name,
                out var nativeEvent, out var arguments) || nativeEvent.GetAddMethod(true) is not MethodInfo accessor)
                throw new InvalidOperationException($"bir2cir: overridden class event '{externalOwner.Name}.{name}' was not found");
            return (delegateType, invokeParameters, null, SupertypeGraph.SubstOwnerTvs(
                ReferenceMetadataIndex.DeclarationSignatureSlot(accessor.GetParameters().Single()), arguments));
        }
        var parameter = add.Params.OrEmpty().FirstOrDefault();
        var sourceType = parameter?.FunctionSignatureType.OrDefault() ?? parameter?.Type.OrDefault();
        var discriminator = FunctionValueRepresentation.ContainsOrdinaryFunction(sourceType)
            ? FunctionSignatureIdentity.DeclarationType(sourceType) : null;
        return (delegateType, invokeParameters, discriminator, null);
    }

    static void RewriteFieldLike(BirFunctionDeclaration method, string kind, string field, TypeNode delegateType)
    {
        if (method.Params.OrEmpty().FirstOrDefault() is { } parameter) parameter.Type = Copy(delegateType);
        method.Body = new IBirStatement[] { AccessorImpl(kind, field, delegateType, method.Static.OrDefault()) };
        method.SpecialName = true;
        method.Overrides = default;
    }

    static void RewriteRaise(BirFunctionDeclaration method, string field, TypeNode delegateType, TypeNode[] invokeParameters)
    {
        method.Params = invokeParameters.Select((type, index) => new BirParameter { Name = "arg" + index, Type = Copy(type) }).ToArray();
        method.Body = new IBirStatement[] { AccessorImpl("raise", field, delegateType, method.Static.OrDefault()) };
        method.SpecialName = true;
        method.Overrides = default;
    }

    static BirEventAccessorBody AccessorImpl(string kind, string field, TypeNode delegateType, bool isStatic) => new() {
        Kind = kind, Name = field, DelegateType = Copy(delegateType), Static = isStatic,
    };

    static void RewriteForwarder(BirFunctionDeclaration method, string kind, BirClrEventForwarder forwarder, TypeNode delegateType)
    {
        if (method.Params.OrEmpty().FirstOrDefault() is { } parameter) parameter.Type = Copy(delegateType);
        var owner = forwarder.OwnerType.OrDefault()
            ?? throw new InvalidOperationException("bir2cir: delegated clrEvent forwarder is missing its delegate owner type");
        var receiver = forwarder.Recv.OrDefault()
            ?? throw new InvalidOperationException("bir2cir: delegated clrEvent forwarder is missing its delegate receiver");
        var handler = new BirLocal { Name = "value", Sty = Copy(delegateType) };
        IBirExpression operation = kind == "add"
            ? new BirEventAdd { Type = Copy(owner), Event = forwarder.Name, Static = false,
                Recv = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)receiver).DeepClone()),
                Handler = handler, HandlerExact = true }
            : new BirEventRemove { Type = Copy(owner), Event = forwarder.Name, Static = false,
                Recv = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)receiver).DeepClone()),
                Handler = handler, HandlerExact = true };
        method.Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(operation) } };
        method.SpecialName = true;
        method.Overrides = default;
    }

    static void RewritePolicy(IBirTypeDeclaration type, List<BirFieldDeclaration> fields, string eventName, string fieldName,
        BirFunctionDeclaration add, BirFunctionDeclaration remove, BirFunctionDeclaration raise,
        TypeNode delegateType, TypeNode[] invokeParameters, ReferenceMetadataIndex refs)
    {
        bool ObjectArgument(TypeNode argument) => argument switch {
            TypeNode.Nullable nullable => ObjectArgument(nullable.Of),
            TypeNode.Oblivious oblivious => ObjectArgument(oblivious.Of),
            TypeNode.Mod modified => ObjectArgument(modified.Of),
            TypeNode.ByRef or TypeNode.Ptr or TypeNode.FnPtr => false,
            TypeNode.Fqn named => !refs.IsByRefLikeFqn(named), _ => true,
        };
        for (var i = 0; i < invokeParameters.Length; i++)
            if (!ObjectArgument(invokeParameters[i]))
                throw new InvalidOperationException($"bir2cir: custom CLR event '{eventName}' parameter {i} "
                    + "cannot be represented in the ClrEventPolicy.invoke Any? argument array: "
                    + TypeNode.ToJson(invokeParameters[i]));
        var field = fields.Single(candidate => candidate.Name.OrDefault() == fieldName);
        var policyType = field.Type.OrDefault() ?? throw new InvalidOperationException("CLR event policy field has no type");
        var ownerArguments = TypeParameterFrame.Declarations(type)
            .Select((_, index) => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        var owner = new TypeNode.Fqn(type.Name.OrDefault(), ownerArguments.Length == 0 ? null : ownerArguments);
        var isStatic = add.Static.OrDefault();
        IBirExpression Receiver() => isStatic
            ? new BirStaticFieldGet { OwnerType = Copy(owner), Name = fieldName, Sty = Copy(policyType) }
            : new BirFieldGet { OwnerType = Copy(owner), Name = fieldName, Sty = Copy(policyType),
                Recv = new BirThis { Sty = Copy(owner) } };
        BirCallInstance Call(string name, TypeNode parameter, IBirExpression argument)
        {
            var declaration = refs.AuthoredKotlinHelper("kotlin.clr.ClrEventPolicy", name, 0, new[] { parameter }, isStatic: false);
            return new BirCallInstance { OwnerType = Copy(policyType), Recv = OptionalField<IBirExpression>.Of(Receiver()),
                Method = name, Virtual = true, DeclarationId = declaration.DeclarationId,
                Sig = new[] { Copy(parameter) }, Args = new[] { argument },
                Ret = new TypeNode.Fqn("kotlin.Unit"), Sty = new TypeNode.Fqn("kotlin.Unit") };
        }
        void Accessor(BirFunctionDeclaration method, string name)
        {
            method.Params.Value[0].Type = Copy(delegateType);
            method.Body = new IBirStatement[] { new BirExpressionStatement { Expr = Call(name,
                new TypeNode.Tv("type", 0), new BirLocal { Name = "value", Sty = Copy(delegateType) }) } };
            method.SpecialName = true;
            method.Overrides = default;
        }
        Accessor(add, "addHandler");
        Accessor(remove, "removeHandler");
        raise.Params = invokeParameters.Select((parameter, index) => new BirParameter { Name = "arg" + index, Type = Copy(parameter) }).ToArray();
        var arguments = invokeParameters.Select((parameter, index) => (IBirExpression)new BirLocal { Name = "arg" + index, Sty = Copy(parameter) }).ToArray();
        var element = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var arrayType = new TypeNode.Array(element);
        raise.Body = new IBirStatement[] { new BirExpressionStatement { Expr = Call("invoke", arrayType,
            new BirNewArray { Elem = Copy(element), Elems = arguments, Sty = Copy(arrayType) }) } };
        raise.SpecialName = true;
        raise.Overrides = default;
    }
}
