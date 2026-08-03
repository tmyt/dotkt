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

    static MethodInfo AnchorMethod(Type type, MethodInfo method) =>
        IsTargetSignatureInstantiation(type)
            ? new SignatureMethod(type, method)
            : TypeBuilder.GetMethod(type, method);

    static ConstructorInfo AnchorConstructor(Type type, ConstructorInfo constructor) =>
        IsTargetSignatureInstantiation(type)
            ? new SignatureConstructor(type, constructor)
            : TypeBuilder.GetConstructor(type, constructor);

    static FieldInfo AnchorField(Type type, FieldInfo field) =>
        IsTargetSignatureInstantiation(type)
            ? new SignatureField(type, field)
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

    // MetadataLoadContext likewise refuses MakeGenericMethod when an argument is a local builder parameter. Represent
    // the MethodSpec as a signature-only MethodInfo; PersistedAssemblyBuilder consumes that description directly.
    static MethodInfo ConstructedMethod(MethodInfo definition, params Type[] arguments) =>
        definition is SignatureMethod
            ? definition.MakeGenericMethod(arguments)
            : definition.Module is not ModuleBuilder && arguments.Any(ContainsTypeBuilder)
                ? new SignatureMethod(definition.DeclaringType, definition, arguments)
                : definition.MakeGenericMethod(arguments);

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
        readonly Type[] _ownerArguments;
        readonly Type[] _methodArguments;

        public SignatureMethod(Type declaringType, MethodInfo definition, Type[] methodArguments = null)
        {
            _declaringType = declaringType;
            _definition = definition.IsConstructedGenericMethod ? definition.GetGenericMethodDefinition() : definition;
            _ownerArguments = declaringType.GetGenericArguments();
            _methodArguments = methodArguments;
        }

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
        readonly Type[] _ownerArguments;

        public SignatureConstructor(Type declaringType, ConstructorInfo definition)
        {
            _declaringType = declaringType;
            _definition = definition;
            _ownerArguments = declaringType.GetGenericArguments();
        }

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
        readonly Type[] _ownerArguments;

        public SignatureField(Type declaringType, FieldInfo definition)
        {
            _declaringType = declaringType;
            _definition = definition;
            _ownerArguments = declaringType.GetGenericArguments();
        }

        public override string Name => _definition.Name;
        public override Type DeclaringType => _declaringType;
        public override Type ReflectedType => _declaringType;
        public override Module Module => _definition.Module;
        public override FieldAttributes Attributes => _definition.Attributes;
        public override RuntimeFieldHandle FieldHandle => throw new NotSupportedException();
        internal Type MappedFieldType => SubstituteSignatureType(_definition.FieldType,
            _definition.DeclaringType, _ownerArguments);
        public override Type FieldType => _definition.FieldType;
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
