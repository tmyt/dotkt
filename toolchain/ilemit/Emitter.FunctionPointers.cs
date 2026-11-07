using System.Reflection;
using DotKt.Bir;

sealed partial class Emitter
{
    static Type RebuildSignatureElement(Type shape, Type element) => NativeSignatureReader.ContainsPointer(element)
        ? new NativeElementType(shape, element)
        : shape.IsArray ? (shape.IsSZArray ? element.MakeArrayType() : element.MakeArrayType(shape.GetArrayRank()))
            : shape.IsByRef ? element.MakeByRefType() : element.MakePointerType();

    static Type RemapFunctionPointer(Type type, Func<Type, Type> map)
    {
        var originalReturn = type.GetFunctionPointerReturnType();
        var originalParameters = type.GetFunctionPointerParameterTypes();
        var result = map(originalReturn);
        var parameters = originalParameters.Select(map).ToArray();
        return ReferenceEquals(result, originalReturn) && parameters.Zip(originalParameters, ReferenceEquals).All(same => same)
            ? type : new NativeRemappedFunctionPointerType(type, result, parameters);
    }

    // Mechanical frame substitution on an already-selected declaration. Its
    // convention and modifiers belong to that declaration and are not inferred.
    sealed class NativeRemappedFunctionPointerType(Type source, Type result, Type[] parameters) : TypeDelegator(source)
    {
        public override Type UnderlyingSystemType => this;
        public override bool IsFunctionPointer => true;
        public override bool IsUnmanagedFunctionPointer => typeImpl.IsUnmanagedFunctionPointer;
        public override bool ContainsGenericParameters => result.ContainsGenericParameters || parameters.Any(p => p.ContainsGenericParameters);
        public override Type GetFunctionPointerReturnType() => result;
        public override Type[] GetFunctionPointerParameterTypes() => parameters;
        public override Type[] GetFunctionPointerCallingConventions() => typeImpl.GetFunctionPointerCallingConventions();
        public override Type[] GetRequiredCustomModifiers() => typeImpl.GetRequiredCustomModifiers();
        public override Type[] GetOptionalCustomModifiers() => typeImpl.GetOptionalCustomModifiers();
    }

    Type FunctionPointerType(TypeNode.FnPtr signature)
    {
        // These are already-resolved CIR facts. Unsupported headers must have been rejected by bir2cir.
        // The reflection surface selects the persisted encoder's exact header, not a delegate family.
        var conventions = signature.CallingConvention switch {
            0 or 9 => Type.EmptyTypes,
            1 => new[] { Bcl("System.Runtime.CompilerServices.CallConvCdecl") },
            2 => new[] { Bcl("System.Runtime.CompilerServices.CallConvStdcall") },
            3 => new[] { Bcl("System.Runtime.CompilerServices.CallConvThiscall") },
            4 => new[] { Bcl("System.Runtime.CompilerServices.CallConvFastcall") },
            _ => throw new InvalidOperationException("Unsupported function-pointer header reached CIR emission"),
        };
        if (signature.GenericArity != 0 || signature.RequiredParams != signature.Params.Length)
            throw new InvalidOperationException("Unsupported generic/vararg function pointer reached CIR emission");
        return new NativeFunctionPointerType(Bcl("System.IntPtr"), signature.CallingConvention,
            NativeSignatureType(signature.Ret), signature.Params.Select(NativeSignatureType).ToArray(), conventions);
    }

    // Map signature children without dropping modifiers at nested positions. The ordinary MapType view
    // intentionally erases modifiers for stack typing; the metadata writer needs this separate exact view.
    Type NativeSignatureType(TypeNode type)
    {
        if (type is TypeNode.Mod)
        {
            var required = new List<Type>();
            var optional = new List<Type>();
            while (type is TypeNode.Mod modifier)
            {
                (modifier.Req ? required : optional).Add(MapType(modifier.M));
                type = modifier.Of;
            }
            required.Reverse(); optional.Reverse();
            return new PersistableModifiedType(NativeSignatureType(type), required.ToArray(), optional.ToArray());
        }
        return type switch {
            TypeNode.FnPtr pointer => FunctionPointerType(pointer),
            TypeNode.Array array => new NativeElementType(MapType(type), NativeSignatureType(array.Elem)),
            TypeNode.ByRef byRef => new NativeElementType(MapType(type), NativeSignatureType(byRef.Of)),
            TypeNode.Ptr pointer => new NativeElementType(MapType(type), NativeSignatureType(pointer.Of)),
            TypeNode.Fqn { Args: { } arguments } => new NativeGenericType(MapType(type), arguments.Select(NativeSignatureType).ToArray()),
            _ => MapType(type),
        };
    }

    // TypeDelegator supplies the target universe's reflection shape, never the signature's identity.
    // UnderlyingSystemType=self prevents PAB from substituting the placeholder IntPtr into metadata.
    sealed class NativeFunctionPointerType(Type placeholder, int header, Type result, Type[] parameters,
        Type[] conventions) : TypeDelegator(placeholder)
    {
        public override Type UnderlyingSystemType => this;
        public override string Name => null;
        public override string FullName => null;
        public override string Namespace => null;
        public override string AssemblyQualifiedName => null;
        public override Type BaseType => null;
        public override bool IsFunctionPointer => true;
        public override bool IsUnmanagedFunctionPointer => header != 0;
        public override bool ContainsGenericParameters => result.ContainsGenericParameters
            || parameters.Any(parameter => parameter.ContainsGenericParameters);
        protected override bool IsPrimitiveImpl() => false;
        protected override bool IsValueTypeImpl() => false;
        public override Type GetFunctionPointerReturnType() => result;
        public override Type[] GetFunctionPointerParameterTypes() => parameters;
        public override Type[] GetFunctionPointerCallingConventions() => conventions;
        public override Type MakeArrayType() => new NativeElementType(typeImpl.MakeArrayType(), this);
        public override Type MakeArrayType(int rank) => new NativeElementType(typeImpl.MakeArrayType(rank), this);
        public override Type MakeByRefType() => new NativeElementType(typeImpl.MakeByRefType(), this);
        public override Type MakePointerType() => new NativeElementType(typeImpl.MakePointerType(), this);
    }

    sealed class NativeElementType(Type shape, Type element) : TypeDelegator(shape)
    {
        public override Type UnderlyingSystemType => this;
        public override Type GetElementType() => element;
        public override bool ContainsGenericParameters => element.ContainsGenericParameters;
        public override Type MakeArrayType() => new NativeElementType(typeImpl.MakeArrayType(), this);
        public override Type MakeArrayType(int rank) => new NativeElementType(typeImpl.MakeArrayType(rank), this);
        public override Type MakeByRefType() => new NativeElementType(typeImpl.MakeByRefType(), this);
        public override Type MakePointerType() => new NativeElementType(typeImpl.MakePointerType(), this);
    }

    sealed class NativeGenericType(Type shape, Type[] arguments) : TypeDelegator(shape)
    {
        public override Type UnderlyingSystemType => this;
        public override bool IsGenericType => true;
        public override bool IsGenericTypeDefinition => false;
        public override bool IsConstructedGenericType => true;
        public override Type GetGenericTypeDefinition() => typeImpl.GetGenericTypeDefinition();
        public override Assembly Assembly => GetGenericTypeDefinition().Assembly;
        public override Module Module => GetGenericTypeDefinition().Module;
        protected override TypeAttributes GetAttributeFlagsImpl() => GetGenericTypeDefinition().Attributes;
        protected override bool IsValueTypeImpl() => GetGenericTypeDefinition().IsValueType;
        public override Type[] GetGenericArguments() => arguments;
        public override Type[] GenericTypeArguments => arguments;
        public override bool ContainsGenericParameters => arguments.Any(argument => argument.ContainsGenericParameters);
    }
}
