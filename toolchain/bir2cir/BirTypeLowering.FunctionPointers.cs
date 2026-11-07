using DotKt.Bir;

static partial class BirTypeLowering
{
    static TypeNode.Fn FunctionPointerShape(TypeNode.Fqn pointer)
    {
        if (pointer.Args is not [TypeNode.Fqn { Args: null } convention,
                TypeNode.Fn { Suspend: false, Recv: null, Ctx: null } shape]
            || !FunctionPointerVocabulary.Conventions.Values.Contains(convention.Name))
            throw new InvalidDataException("A CLR function pointer requires an exact convention and non-suspend signature");
        bool NullableVariable(TypeNode type) => type switch {
            TypeNode.Nullable { Of: TypeNode.Tv } => true,
            TypeNode.Nullable nullable => NullableVariable(nullable.Of),
            TypeNode.Oblivious oblivious => NullableVariable(oblivious.Of),
            TypeNode.Fqn { Args: { } args } => args.Any(NullableVariable),
            TypeNode.Array array => NullableVariable(array.Elem),
            TypeNode.ByRef reference => NullableVariable(reference.Of),
            TypeNode.Fn function => NullableVariable(function.Ret) || function.Params.Any(NullableVariable),
            _ => false,
        };
        if (NullableVariable(shape))
            throw new InvalidDataException("A nullable Kotlin type variable has no exact native function-pointer signature; use the projected CLR Nullable type for a reified nullable value");
        return shape;
    }

    static bool TryLowerNativeSignature(TypeNode.Fqn type, bool refBuild, bool force, bool typeArg, out TypeNode native)
    {
        native = null;
        if (type.Name == FunctionPointerVocabulary.Pointer)
        {
            if (typeArg) throw new InvalidDataException("A CLR function pointer cannot occupy a CLR generic argument");
            var shape = FunctionPointerShape(type);
            var convention = FunctionPointerVocabulary.Conventions.Single(pair => pair.Value == ((TypeNode.Fqn)type.Args[0]).Name).Key;
            var pointer = new TypeNode.FnPtr(convention, 0, shape.Params.Length,
                LowerNativeSlot(shape.Ret, refBuild, force, voidPosition: true),
                shape.Params.Select(slot => LowerNativeSlot(slot, refBuild, force, voidPosition: false)).ToArray());
            ValidateNativePointer(pointer);
            native = pointer;
            return true;
        }
        if (type.Name is FunctionPointerVocabulary.Required or FunctionPointerVocabulary.Optional)
        {
            if (type.Args is not [var marker, var underlying])
                throw new InvalidDataException("A native signature modifier requires its marker and modified type");
            native = new TypeNode.Mod(type.Name == FunctionPointerVocabulary.Required,
                LowerType(marker, refBuild, force, typeArg: false),
                LowerType(underlying, refBuild, force, typeArg));
            return true;
        }
        if (type.Name == FunctionPointerVocabulary.Value)
        {
            if (type.Args is not [var value]) throw new InvalidDataException("A native signature value requires its type");
            native = LowerType(value, refBuild, force, typeArg);
            return true;
        }
        if (FunctionPointerVocabulary.ArrayRanks.TryGetValue(type.Name, out var rank))
        {
            if (type.Args is not [var element]) throw new InvalidDataException("A native array signature requires its element type");
            native = TypeNode.Array.General(LowerType(element, refBuild, force, typeArg: false), rank);
            return true;
        }
        return false;
    }

    static TypeNode LowerNativeSlot(TypeNode type, bool refBuild, bool force, bool voidPosition)
    {
        if (type is TypeNode.Star or TypeNode.Projection)
            throw new InvalidDataException("A CLR function pointer requires exact parameter and return types");
        if (type is TypeNode.Fqn { Name: FunctionPointerVocabulary.Required or FunctionPointerVocabulary.Optional,
                Args: [var marker, var underlying] } modifier)
            return new TypeNode.Mod(modifier.Name == FunctionPointerVocabulary.Required,
                LowerType(marker, refBuild, force, typeArg: false),
                LowerNativeSlot(underlying, refBuild, force, voidPosition));
        return voidPosition && type is TypeNode.Fqn { Name: "kotlin.Unit", Args: null }
            ? VoidType : LowerType(type, refBuild, force, typeArg: false);
    }

    internal static void ValidateNativePointer(TypeNode.FnPtr pointer)
    {
        if (!FunctionPointerVocabulary.Conventions.ContainsKey(pointer.CallingConvention)
            || pointer.GenericArity != 0 || pointer.RequiredParams != pointer.Params.Length)
            throw new InvalidDataException("Unsupported generic/vararg/instance CLR function-pointer calling convention");
        void Slot(TypeNode slot)
        {
            // PAB groups required modifiers before optional ones. Check the
            // physical chain, after vocabulary wrappers and aliases are gone.
            var optional = false;
            while (slot is TypeNode.Mod modifier)
            {
                if (optional && modifier.Req)
                    throw new InvalidDataException("This required/optional native modifier order is not supported by the target persisted encoder");
                optional |= !modifier.Req;
                slot = modifier.Of;
            }
            switch (slot)
            {
                case TypeNode.FnPtr nested: ValidateNativePointer(nested); break;
                case TypeNode.Array array: Slot(array.Elem); break;
                case TypeNode.Ptr address: Slot(address.Of); break;
                case TypeNode.ByRef reference: Slot(reference.Of); break;
                case TypeNode.Fqn { Args: { } args }: foreach (var argument in args) Slot(argument); break;
            }
        }
        Slot(pointer.Ret);
        foreach (var parameter in pointer.Params) Slot(parameter);
    }
}
