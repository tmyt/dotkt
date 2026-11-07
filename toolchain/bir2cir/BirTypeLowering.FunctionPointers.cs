using DotKt.Bir;

static partial class BirTypeLowering
{
    static TypeNode.Fn FunctionPointerShape(TypeNode.Fqn pointer)
    {
        if (pointer.Args is not [TypeNode.Fqn { Args: null } convention,
                TypeNode.Fn { Suspend: false, Recv: null, Ctx: null } shape]
            || !FunctionPointerVocabulary.Conventions.Values.Contains(convention.Name))
            throw new InvalidDataException("A CLR function pointer requires an exact convention and non-suspend signature");
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
            TypeNode LowerSlot(TypeNode slot, bool returns = false) => slot is TypeNode.Star or TypeNode.Projection
                ? throw new InvalidDataException("A CLR function pointer requires exact parameter and return types")
                : returns && slot is TypeNode.Fqn { Name: "kotlin.Unit", Args: null }
                    ? VoidType : LowerType(slot, refBuild, force, typeArg: false);
            native = new TypeNode.FnPtr(convention, 0, shape.Params.Length,
                LowerSlot(shape.Ret, returns: true), shape.Params.Select(slot => LowerSlot(slot)).ToArray());
            return true;
        }
        if (type.Name is FunctionPointerVocabulary.Required or FunctionPointerVocabulary.Optional)
        {
            if (type.Args is not [var marker, var underlying])
                throw new InvalidDataException("A native signature modifier requires its marker and modified type");
            // PAB groups required modifiers before optional ones. Do not silently
            // turn an interleaved native identity into a different signature.
            if (type.Name == FunctionPointerVocabulary.Optional
                && underlying is TypeNode.Fqn { Name: FunctionPointerVocabulary.Required })
                throw new InvalidDataException("This required/optional native modifier order is not supported by the target persisted encoder");
            native = new TypeNode.Mod(type.Name == FunctionPointerVocabulary.Required,
                LowerType(marker, refBuild, force, typeArg: false),
                underlying is TypeNode.Fqn { Name: "kotlin.Unit", Args: null } ? VoidType
                    : LowerType(underlying, refBuild, force, typeArg));
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
}
