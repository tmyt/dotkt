// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

// Reflection's target MetadataLoadContext cannot construct a generic type over a GenericTypeParameterBuilder.
// Type.MakeGenericSignatureType represents that valid metadata TypeSpec, but TypeBuilder.GetMethod/GetConstructor/
// GetField accept only their private TypeBuilderInstantiation. These wrappers re-anchor an already-selected open
// member on the signature TypeSpec and substitute its declaration parameters mechanically. They do no lookup.
sealed partial class Emitter
{
    static bool IsTargetSignatureInstantiation(Type type) =>
        type?.IsConstructedGenericType == true && type.GetGenericTypeDefinition() is not TypeBuilder
        && ContainsTypeBuilder(type);

    // SignatureType deliberately implements only the reflection surface needed to describe metadata; shape queries
    // such as IsInterface/IsValueType throw. Those properties belong to the generic definition and are unchanged by
    // instantiation, so keep every emitter classification on the definition while retaining the TypeSpec for emission.
    static bool IsInterfaceType(Type type) =>
        (IsTargetSignatureInstantiation(type) ? type.GetGenericTypeDefinition() : type).IsInterface;

    static bool IsValueType(Type type) =>
        (IsTargetSignatureInstantiation(type) ? type.GetGenericTypeDefinition() : type).IsValueType;

    // TypeBuilder.SetParent validates parent.IsInterface before storing it, but SignatureType intentionally throws for
    // attribute queries. TypeBuilder's persisted metadata writer subsequently unwraps UnderlyingSystemType. Supply the
    // generic definition's shape for validation and the original target TypeSpec for encoding.
    static Type ParentType(Type type) =>
        IsTargetSignatureInstantiation(type) ? new SignatureShapeType(type) : type;

    sealed class SignatureShapeType : TypeDelegator
    {
        readonly Type _signature;
        readonly Type _definition;

        public SignatureShapeType(Type signature) : base(signature)
        {
            _signature = signature;
            _definition = signature.GetGenericTypeDefinition();
        }

        // TypeBuilder performs its validation through this wrapper, while the persisted encoder must unwrap the
        // actual SignatureType. Returning the wrapper here makes PAB collapse local generic arguments to the open
        // TypeRef (for example ClrPropertyStub<T> -> ClrPropertyStub<>), producing an unloadable base edge.
        public override Type UnderlyingSystemType => _signature;
        public override Type BaseType => _definition.BaseType;
        public override Assembly Assembly => _definition.Assembly;
        public override Module Module => _definition.Module;
        public override bool IsGenericType => true;
        public override bool IsGenericTypeDefinition => false;
        public override bool IsConstructedGenericType => true;
        public override bool ContainsGenericParameters => _signature.ContainsGenericParameters;
        public override Type[] GenericTypeArguments => _signature.GetGenericArguments();
        public override Type GetGenericTypeDefinition() => _signature.GetGenericTypeDefinition();
        public override Type[] GetGenericArguments() => _signature.GetGenericArguments();
        protected override TypeAttributes GetAttributeFlagsImpl() => _definition.Attributes;
    }

    // PAB 10.0.10 normally replaces a member on a constructed owner with the open declaration by calling
    // GetMemberWithSameMetadataDefinitionAs. A signature adapter is intentionally not a runtime reflection member, so
    // that lookup would discard it (or reject it) before its modifier-aware ParameterInfo graph is read. Suppress only
    // that normalization predicate; GetTypeHandle immediately unwraps UnderlyingSystemType and still encodes the real
    // constructed owner as a TypeSpec.
    sealed class PersistableMemberOwnerType : TypeDelegator
    {
        readonly Type _owner;
        public PersistableMemberOwnerType(Type owner) : base(owner) { _owner = owner; }
        public override Type UnderlyingSystemType => _owner;
        public override bool IsGenericType => _owner.IsGenericType;
        public override bool IsGenericTypeDefinition => _owner.IsGenericTypeDefinition;
        public override bool IsConstructedGenericType => false;
        public override bool ContainsGenericParameters => _owner.ContainsGenericParameters;
        public override Type GetGenericTypeDefinition() => _owner.GetGenericTypeDefinition();
        public override Type[] GetGenericArguments() => _owner.GetGenericArguments();
    }

    // Mirror PAB's own exemption for members whose owner contains a builder from this emission module. Those owners
    // must retain their SignatureType/TypeBuilderInstantiation identity; all other constructed owners are normalized
    // by PAB and therefore need the transparent owner view above to keep our modifier-aware member adapter intact.
    static bool NeedsPersistableMemberOwner(Type owner) =>
        owner.IsConstructedGenericType && owner.GetGenericTypeDefinition() is not TypeBuilder
        && !owner.GetGenericArguments().Any(ContainsEmissionBuilder);

    static bool ContainsEmissionBuilder(Type type)
    {
        if (type is TypeBuilder || type is GenericTypeParameterBuilder) return true;
        if (type.HasElementType) return ContainsEmissionBuilder(type.GetElementType());
        return type.IsConstructedGenericType
            && (type.GetGenericTypeDefinition() is TypeBuilder
                || type.GetGenericArguments().Any(ContainsEmissionBuilder));
    }

    static MethodInfo MethodDeclaration(MethodInfo method)
    {
        if (method.IsConstructedGenericMethod) method = method.GetGenericMethodDefinition();
        var owner = method.DeclaringType;
        if (owner is not { IsConstructedGenericType: true }) return method;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        // member-lookup-residual: mechanical declaration recovery by module+metadata token, never member selection.
        return owner.GetGenericTypeDefinition().GetMethods(flags)
            .Single(candidate => candidate.Module == method.Module && candidate.MetadataToken == method.MetadataToken);
    }

    static ConstructorInfo ConstructorDeclaration(ConstructorInfo constructor)
    {
        var owner = constructor.DeclaringType;
        if (owner is not { IsConstructedGenericType: true }) return constructor;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        // member-lookup-residual: mechanical declaration recovery by module+metadata token, never member selection.
        return owner.GetGenericTypeDefinition().GetConstructors(flags)
            .Single(candidate => candidate.Module == constructor.Module && candidate.MetadataToken == constructor.MetadataToken);
    }

    static FieldInfo FieldDeclaration(FieldInfo field)
    {
        var owner = field.DeclaringType;
        if (owner is not { IsConstructedGenericType: true }) return field;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        // member-lookup-residual: mechanical declaration recovery by module+metadata token, never member selection.
        return owner.GetGenericTypeDefinition().GetFields(flags)
            .Single(candidate => candidate.Module == field.Module && candidate.MetadataToken == field.MetadataToken);
    }

    MethodInfo AnchorMethod(Type type, MethodInfo method)
    {
        var anchored = IsTargetSignatureInstantiation(type)
            ? new SignatureMethod(type, method)
            // member-lookup-residual: Reflection.Emit's re-anchoring API: it takes the MemberInfo, not a name
            : TypeBuilder.GetMethod(type, method);
        _anchoredMethodDefinitions[anchored] = method;
        return anchored;
    }

    static ConstructorInfo AnchorConstructor(Type type, ConstructorInfo constructor) =>
        IsTargetSignatureInstantiation(type)
            ? new SignatureConstructor(type, constructor)
            // member-lookup-residual: anchoring an ALREADY-resolved member onto a constructed owner, not choosing one
            : TypeBuilder.GetConstructor(type, constructor);

    static FieldInfo AnchorField(Type type, FieldInfo field) =>
        IsTargetSignatureInstantiation(type)
            ? new SignatureField(type, field)
            // member-lookup-residual: Reflection.Emit's re-anchoring API: it takes the MemberInfo, not a name
            : TypeBuilder.GetField(type, field);

    // PersistedAssemblyBuilder reads the public reflection surface below to encode a MemberRef. ECMA-335 requires
    // that signature to remain the member DECLARATION (`!0`/`!!0`), even when its owner is a constructed TypeSpec.
    // Emitter stack typing, however, needs the mechanically substituted view. Keep those two views explicit instead
    // of making the metadata writer accidentally serialize the call-site view.
    static ParameterInfo[] ParametersOf(MethodBase member) => member switch
    {
        SignatureMethod method => method.MappedParameters,
        SignatureConstructor constructor => constructor.MappedParameters,
        _ => member.GetParameters(),
    };

    static Type ReturnTypeOf(MethodInfo method) =>
        method is SignatureMethod signature ? signature.MappedReturnType : method.ReturnType;

    static Type FieldTypeOf(FieldInfo field) =>
        field is SignatureField signature ? signature.MappedFieldType : field.FieldType;

    // Runtime 10.0.10's PersistedAssemblyBuilder consumes modifier-aware Types from
    // ParameterInfo.GetModifiedParameterType/FieldInfo.GetModifiedFieldType. MetadataLoadContext represents those as
    // RoModifiedType nodes: they preserve the modifier tree, but deliberately throw from shape APIs such as
    // GetGenericTypeDefinition. PAB needs both surfaces at once. This adapter retains every modifier-bearing child
    // while sourcing the few unsupported structural queries from each node's unmodified identity.
    static Type AdaptModifiedType(Type type) =>
        ReferenceEquals(type, type.UnderlyingSystemType) ? type : new PersistableModifiedType(type);

    sealed class PersistableModifiedType : TypeDelegator
    {
        readonly Type _modified;
        readonly Type _unmodified;

        public PersistableModifiedType(Type modified) : base(modified)
        {
            _modified = modified;
            _unmodified = modified.UnderlyingSystemType;
        }

        public override Type UnderlyingSystemType => _unmodified;
        public override bool IsGenericType => _modified.IsGenericType;
        public override bool IsGenericTypeDefinition => _modified.IsGenericTypeDefinition;
        public override bool IsConstructedGenericType => _modified.IsConstructedGenericType;
        public override bool IsGenericParameter => _modified.IsGenericParameter;
        public override bool IsGenericTypeParameter => _modified.IsGenericTypeParameter;
        public override bool IsGenericMethodParameter => _modified.IsGenericMethodParameter;
        public override bool IsFunctionPointer => _modified.IsFunctionPointer;
        public override bool IsUnmanagedFunctionPointer => _modified.IsUnmanagedFunctionPointer;
        public override bool ContainsGenericParameters => _modified.ContainsGenericParameters;
        public override int GenericParameterPosition => _modified.GenericParameterPosition;
        public override MethodBase DeclaringMethod => _modified.DeclaringMethod;
        public override int GetArrayRank() => _modified.GetArrayRank();
        protected override bool HasElementTypeImpl() => _modified.HasElementType;
        protected override bool IsArrayImpl() => _modified.IsArray;
        protected override bool IsByRefImpl() => _modified.IsByRef;
        protected override bool IsPointerImpl() => _modified.IsPointer;
        protected override bool IsValueTypeImpl() => _modified.IsValueType;
        public override Type GetGenericTypeDefinition() => _unmodified.GetGenericTypeDefinition();
        public override Type[] GetGenericArguments() => _modified.GetGenericArguments().Select(AdaptModifiedType).ToArray();
        public override Type GetElementType()
        {
            var element = _modified.GetElementType();
            return element == null ? null : AdaptModifiedType(element);
        }
        public override Type GetFunctionPointerReturnType() => AdaptModifiedType(_modified.GetFunctionPointerReturnType());
        public override Type[] GetFunctionPointerParameterTypes() =>
            _modified.GetFunctionPointerParameterTypes().Select(AdaptModifiedType).ToArray();
    }

    // MetadataLoadContext likewise refuses MakeGenericMethod when an argument is a local builder parameter. Represent
    // the MethodSpec as a signature-only MethodInfo; PersistedAssemblyBuilder consumes that description directly.
    MethodInfo ConstructedMethod(MethodInfo definition, params Type[] arguments)
    {
        var constructed = definition is SignatureMethod
            ? definition.MakeGenericMethod(arguments)
            : definition.Module is not ModuleBuilder && arguments.Any(ContainsTypeBuilder)
                ? new SignatureMethod(definition.DeclaringType, definition, arguments)
                : definition.MakeGenericMethod(arguments);
        return IsSanctioned(definition) ? Sanction(constructed) : constructed;
    }

    // PersistedAssemblyBuilder 10.0.10 reads modifier-aware Types from every declaration member handed to
    // DefineMethodOverride. Normalize that final emission boundary so raw MetadataLoadContext members never leak their
    // intentionally partial RoModifiedType reflection surface into PAB. The member has already been selected; this is
    // a one-to-one metadata view, not member resolution.
    static MethodInfo PersistableMethod(MethodInfo method)
    {
        if (method is SignatureMethod signature) return signature.AsPersistable();
        if (method is MethodBuilder || method.Module is ModuleBuilder) return method;
        var methodArguments = method.IsConstructedGenericMethod ? method.GetGenericArguments() : null;
        var owner = method.DeclaringType;
        if (NeedsPersistableMemberOwner(owner))
        {
            method = MethodDeclaration(method);
            owner = new PersistableMemberOwnerType(owner);
        }
        return new SignatureMethod(owner, method, methodArguments);
    }

    void WireMethodOverride(TypeBuilder owner, MethodInfo body, MethodInfo declaration)
    {
        AuditExternal(declaration, "a MethodImpl target");
        owner.DefineMethodOverride(body, PersistableMethod(declaration));
    }

    void EmitMethod(ILGenerator il, OpCode opcode, MethodInfo method)
    {
        AuditExternal(method, "a call operand");
        il.Emit(opcode, PersistableMethod(method));
    }

    static ConstructorInfo PersistableConstructor(ConstructorInfo constructor)
    {
        if (constructor is SignatureConstructor signature) return signature.AsPersistable();
        if (constructor is ConstructorBuilder || constructor.Module is ModuleBuilder) return constructor;
        var owner = constructor.DeclaringType;
        if (NeedsPersistableMemberOwner(owner))
        {
            constructor = ConstructorDeclaration(constructor);
            owner = new PersistableMemberOwnerType(owner);
        }
        return new SignatureConstructor(owner, constructor);
    }

    void EmitConstructor(ILGenerator il, OpCode opcode, ConstructorInfo constructor)
    {
        AuditExternal(constructor, "a newobj operand");
        il.Emit(opcode, PersistableConstructor(constructor));
    }

    static FieldInfo PersistableField(FieldInfo field)
    {
        if (field is SignatureField signature) return signature.AsPersistable();
        if (field is FieldBuilder || field.Module is ModuleBuilder) return field;
        var owner = field.DeclaringType;
        if (NeedsPersistableMemberOwner(owner))
        {
            field = FieldDeclaration(field);
            owner = new PersistableMemberOwnerType(owner);
        }
        return new SignatureField(owner, field);
    }

    void EmitField(ILGenerator il, OpCode opcode, FieldInfo field)
    {
        AuditExternal(field, "a field operand");
        il.Emit(opcode, PersistableField(field));
    }

    static Type SubstituteSignatureType(Type type, Type declaringType, Type[] ownerArguments,
        Type[] methodParameters = null, Type[] methodArguments = null)
    {
        if (type.IsGenericParameter)
        {
            if (type.DeclaringMethod != null)
                return methodArguments != null && type.GenericParameterPosition < methodArguments.Length
                    ? methodArguments[type.GenericParameterPosition] : type;
            return type.GenericParameterPosition < ownerArguments.Length
                ? ownerArguments[type.GenericParameterPosition] : type;
        }
        if (type.HasElementType)
        {
            var element = SubstituteSignatureType(type.GetElementType(), declaringType, ownerArguments,
                methodParameters, methodArguments);
            if (ReferenceEquals(element, type.GetElementType())) return type;
            return type.IsArray ? (type.GetArrayRank() == 1 ? element.MakeArrayType() : element.MakeArrayType(type.GetArrayRank()))
                : type.IsByRef ? element.MakeByRefType() : type.IsPointer ? element.MakePointerType() : type;
        }
        if (!type.IsGenericType) return type;
        var arguments = type.GetGenericArguments()
            .Select(a => SubstituteSignatureType(a, declaringType, ownerArguments, methodParameters, methodArguments))
            .ToArray();
        return arguments.SequenceEqual(type.GetGenericArguments())
            ? type : ConstructedType(type.GetGenericTypeDefinition(), arguments);
    }

    sealed class SignatureParameter : ParameterInfo
    {
        readonly ParameterInfo _source;
        readonly MemberInfo _member;
        readonly Type _type;

        public SignatureParameter(ParameterInfo source, MemberInfo member, Type type)
        {
            _source = source;
            _member = member;
            _type = type;
        }

        public override Type ParameterType => _type;
        public override string Name => _source.Name;
        public override int Position => _source.Position;
        public override ParameterAttributes Attributes => _source.Attributes;
        public override object DefaultValue => _source.DefaultValue;
        public override object RawDefaultValue => _source.RawDefaultValue;
        public override MemberInfo Member => _member;
        // .NET 10.0.10's PersistedAssemblyBuilder reads the modifier-aware parameter type when it serializes a
        // MemberRef. ParameterInfo's base implementation throws, so a signature-only wrapper must forward the
        // declaration view explicitly just as it forwards the custom-modifier arrays below.
        public override Type GetModifiedParameterType() => AdaptModifiedType(_source.GetModifiedParameterType());
        public override Type[] GetRequiredCustomModifiers() => _source.GetRequiredCustomModifiers();
        public override Type[] GetOptionalCustomModifiers() => _source.GetOptionalCustomModifiers();
        public override object[] GetCustomAttributes(bool inherit) => throw new NotSupportedException();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotSupportedException();
        public override bool IsDefined(Type attributeType, bool inherit) => false;
    }

    sealed class SignatureMethod : MethodInfo
    {
        readonly Type _declaringType;
        readonly MethodInfo _definition;

        /// <summary>The DECLARATION this signature view describes — what a comparison must look at.</summary>
        internal MethodInfo Declaration => _definition;
        readonly Type[] _ownerArguments;
        readonly Type[] _methodArguments;

        public SignatureMethod(Type declaringType, MethodInfo definition, Type[] methodArguments = null)
        {
            _declaringType = declaringType;
            _definition = definition.IsConstructedGenericMethod ? definition.GetGenericMethodDefinition() : definition;
            _ownerArguments = declaringType.GetGenericArguments();
            _methodArguments = methodArguments;
        }

        internal SignatureMethod AsPersistable() =>
            NeedsPersistableMemberOwner(_declaringType)
                ? new SignatureMethod(new PersistableMemberOwnerType(_declaringType), MethodDeclaration(_definition), _methodArguments)
                : this;

        Type Map(Type type) => SubstituteSignatureType(type, _definition.DeclaringType, _ownerArguments,
            _definition.GetGenericArguments(), _methodArguments);

        public override string Name => _definition.Name;
        public override Type DeclaringType => _declaringType;
        public override Type ReflectedType => _declaringType;
        public override Module Module => _definition.Module;
        public override MethodAttributes Attributes => _definition.Attributes;
        public override CallingConventions CallingConvention => _definition.CallingConvention;
        public override RuntimeMethodHandle MethodHandle => throw new NotSupportedException();
        internal Type MappedReturnType => Map(_definition.ReturnType);
        internal ParameterInfo[] MappedParameters => _definition.GetParameters()
            .Select(p => (ParameterInfo)new SignatureParameter(p, this, Map(p.ParameterType))).ToArray();
        public override Type ReturnType => _definition.ReturnType;
        public override ICustomAttributeProvider ReturnTypeCustomAttributes => _definition.ReturnTypeCustomAttributes;
        public override ParameterInfo ReturnParameter => new SignatureParameter(_definition.ReturnParameter, this, _definition.ReturnType);
        public override bool IsGenericMethod => _definition.IsGenericMethod;
        public override bool IsGenericMethodDefinition => _definition.IsGenericMethodDefinition && _methodArguments == null;
        public override bool ContainsGenericParameters =>
            _declaringType.ContainsGenericParameters || (_methodArguments ?? _definition.GetGenericArguments()).Any(t => t.ContainsGenericParameters);
        public override MethodImplAttributes GetMethodImplementationFlags() => _definition.GetMethodImplementationFlags();
        public override ParameterInfo[] GetParameters() => _definition.GetParameters()
            .Select(p => (ParameterInfo)new SignatureParameter(p, this, p.ParameterType)).ToArray();
        public override Type[] GetGenericArguments() => _methodArguments ?? _definition.GetGenericArguments();
        public override MethodInfo GetGenericMethodDefinition() =>
            _methodArguments == null ? this : new SignatureMethod(_declaringType, _definition);
        public override MethodInfo MakeGenericMethod(params Type[] typeArguments) =>
            new SignatureMethod(_declaringType, _definition, typeArguments);
        public override MethodInfo GetBaseDefinition() => this;
        public override object Invoke(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) =>
            throw new NotSupportedException();
        public override object[] GetCustomAttributes(bool inherit) => throw new NotSupportedException();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotSupportedException();
        public override bool IsDefined(Type attributeType, bool inherit) => false;
    }

    sealed class SignatureConstructor : ConstructorInfo
    {
        readonly Type _declaringType;
        readonly ConstructorInfo _definition;

        /// <summary>The DECLARATION this signature view describes — what a comparison must look at.</summary>
        internal ConstructorInfo Declaration => _definition;
        readonly Type[] _ownerArguments;

        public SignatureConstructor(Type declaringType, ConstructorInfo definition)
        {
            _declaringType = declaringType;
            _definition = definition;
            _ownerArguments = declaringType.GetGenericArguments();
        }

        internal SignatureConstructor AsPersistable() =>
            NeedsPersistableMemberOwner(_declaringType)
                ? new SignatureConstructor(new PersistableMemberOwnerType(_declaringType), ConstructorDeclaration(_definition))
                : this;

        Type Map(Type type) => SubstituteSignatureType(type, _definition.DeclaringType, _ownerArguments);
        internal ParameterInfo[] MappedParameters => _definition.GetParameters()
            .Select(p => (ParameterInfo)new SignatureParameter(p, this, Map(p.ParameterType))).ToArray();
        public override string Name => _definition.Name;
        public override Type DeclaringType => _declaringType;
        public override Type ReflectedType => _declaringType;
        public override Module Module => _definition.Module;
        public override MethodAttributes Attributes => _definition.Attributes;
        public override CallingConventions CallingConvention => _definition.CallingConvention;
        public override RuntimeMethodHandle MethodHandle => throw new NotSupportedException();
        public override MethodImplAttributes GetMethodImplementationFlags() => _definition.GetMethodImplementationFlags();
        public override ParameterInfo[] GetParameters() => _definition.GetParameters()
            .Select(p => (ParameterInfo)new SignatureParameter(p, this, p.ParameterType)).ToArray();
        public override object Invoke(BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) =>
            throw new NotSupportedException();
        public override object Invoke(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) =>
            throw new NotSupportedException();
        public override object[] GetCustomAttributes(bool inherit) => throw new NotSupportedException();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotSupportedException();
        public override bool IsDefined(Type attributeType, bool inherit) => false;
    }

    sealed class SignatureField : FieldInfo
    {
        readonly Type _declaringType;
        readonly FieldInfo _definition;

        /// <summary>The DECLARATION this signature view describes — what a comparison must look at.</summary>
        internal FieldInfo Declaration => _definition;
        readonly Type[] _ownerArguments;

        public SignatureField(Type declaringType, FieldInfo definition)
        {
            _declaringType = declaringType;
            _definition = definition;
            _ownerArguments = declaringType.GetGenericArguments();
        }

        internal SignatureField AsPersistable() =>
            NeedsPersistableMemberOwner(_declaringType)
                ? new SignatureField(new PersistableMemberOwnerType(_declaringType), FieldDeclaration(_definition))
                : this;

        public override string Name => _definition.Name;
        public override Type DeclaringType => _declaringType;
        public override Type ReflectedType => _declaringType;
        public override Module Module => _definition.Module;
        public override FieldAttributes Attributes => _definition.Attributes;
        public override RuntimeFieldHandle FieldHandle => throw new NotSupportedException();
        internal Type MappedFieldType => SubstituteSignatureType(_definition.FieldType,
            _definition.DeclaringType, _ownerArguments);
        public override Type FieldType => _definition.FieldType;
        public override Type GetModifiedFieldType() => AdaptModifiedType(_definition.GetModifiedFieldType());
        public override Type[] GetRequiredCustomModifiers() => _definition.GetRequiredCustomModifiers();
        public override Type[] GetOptionalCustomModifiers() => _definition.GetOptionalCustomModifiers();
        public override object GetValue(object obj) => throw new NotSupportedException();
        public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, CultureInfo culture) =>
            throw new NotSupportedException();
        public override object[] GetCustomAttributes(bool inherit) => throw new NotSupportedException();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotSupportedException();
        public override bool IsDefined(Type attributeType, bool inherit) => false;
    }
}
