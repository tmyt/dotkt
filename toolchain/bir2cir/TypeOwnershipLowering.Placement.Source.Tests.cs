using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class TypeOwnershipLowering
{
    internal static void SelfTestLatePlacement()
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Late owning type placement: " + message);
        }
        var bound = new TypeNode.Fqn("Example.Bound", new TypeNode[] { new TypeNode.Tv("type", 0) });
        var outerParameter = new BirTypeParameter { Name = "T", Constraints = new[] { bound } };
        var own = new BirTypeParameter { Name = "T", Constraints = Array.Empty<TypeNode>() };
        var collision = new BirTypeParameter { Name = "dotkt$outer0" };
        var secondCollision = new BirTypeParameter { Name = "dotkt$outer0$" };
        BirFunctionDeclaration Method(string name) => new() { Name = name, Params = Array.Empty<BirParameter>(),
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() };
        var firstMoved = Method("first"); firstMoved.SemanticOwner = "Outer.Inner";
        firstMoved.DeclarationId = "first-id"; firstMoved.Mods = new BirModifiers { Suspend = true };
        firstMoved.LexicalOwnerTypeParamCount = 4;
        var lastMoved = Method("last"); lastMoved.SemanticOwner = "Outer.Inner";
        lastMoved.DeclarationId = "last-id"; lastMoved.Mods = new BirModifiers { Suspend = true };
        lastMoved.LexicalOwnerTypeParamCount = 4;
        var fileMethod = Method("file"); fileMethod.SemanticOwner = "File";
        var plain = Method("plain");
        var initial = Method("existing");
        var outer = new BirClassDefinition { Kind = "class", Name = "Outer",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var inner = new BirClassDefinition { Kind = "class", Name = "Outer.Inner", SemanticOwner = "Outer",
            NestedIn = OptionalField<string>.Null, OuterTypeParamCount = 1, OuterTypeParamOffset = 0,
            TypeParams = new[] { outerParameter, own, collision, secondCollision }, Methods = new[] { initial } };
        var previousNestedOwner = new BirClassDefinition { Kind = "class", Name = "Carrier" };
        var alreadyPlaced = new BirInterfaceDefinition { Kind = "interface", Name = "Outer.Contract",
            SemanticOwner = "Outer", NestedIn = "Carrier", Vis = OptionalField<string>.Null };
        var noOwn = new BirClassDefinition { Kind = "class", Name = "Outer.CapturedOnly", SemanticOwner = "Outer",
            OuterTypeParamCount = 1, TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var empty = Array.Empty<BirTypeParameter>();
        var zero = new BirClassDefinition { Kind = "class", Name = "Zero", OuterTypeParamCount = 0,
            OuterTypeParamOffset = 0, TypeParams = empty };
        var nullCount = new BirClassDefinition { Kind = "class", Name = "NullCount",
            OuterTypeParamCount = OptionalField<int>.Null, OuterTypeParamOffset = 0,
            SemanticOwner = OptionalField<string>.Null, TypeParams = OptionalField<BirTypeParameter[]>.Null };
        var first = new BirFileDeclaration { FileClass = "File",
            Methods = new[] { firstMoved, fileMethod, lastMoved, plain }, Types = new IBirTypeDeclaration[] { outer } };
        var second = new BirFileDeclaration { FileClass = "OtherFile", Methods = OptionalField<BirFunctionDeclaration[]>.Null,
            Types = new IBirTypeDeclaration[] { inner, previousNestedOwner, alreadyPlaced, noOwn, zero, nullCount } };
        var firstTypes = first.Types.Value;
        var secondTypes = second.Types.Value;
        ApplyAll(new[] { first, second });
        Require(ReferenceEquals(first.Types.Value, firstTypes) && ReferenceEquals(second.Types.Value, secondTypes), "type occurrence vectors changed");
        Require(first.Methods.Value is [var remainingFile, var remainingPlain]
            && ReferenceEquals(remainingFile, fileMethod) && ReferenceEquals(remainingPlain, plain)
            && !fileMethod.SemanticOwner.IsPresent,
            "file-class ownership or remaining method order changed");
        Require(inner.Methods.Value is [var existing, var movedLast, var movedFirst]
            && ReferenceEquals(existing, initial) && ReferenceEquals(movedLast, lastMoved) && ReferenceEquals(movedFirst, firstMoved)
            && !firstMoved.SemanticOwner.IsPresent && firstMoved.DeclarationId.Value == "first-id"
            && !initial.LexicalOwnerTypeParamCount.IsPresent && !lastMoved.LexicalOwnerTypeParamCount.IsPresent
            && !firstMoved.LexicalOwnerTypeParamCount.IsPresent,
            "actual method identity, reverse move order or direct handoff cleanup changed");
        Require(inner.NestedIn.Value == "Outer" && !inner.SemanticOwner.IsPresent
            && alreadyPlaced.NestedIn.Value == "Carrier" && !alreadyPlaced.SemanticOwner.IsPresent
            && alreadyPlaced.Vis.IsNull, "explicit nesting or unrelated field presence changed");
        var capture = inner.CapturedTypeParams.Value.Single();
        Require(!ReferenceEquals(capture, outerParameter) && capture.Name.Value == "dotkt$outer0$$"
            && outerParameter.Name.Value == "T" && capture.Constraints.Value.SequenceEqual(new[] { bound })
            && !ReferenceEquals(capture.Constraints.Value, outerParameter.Constraints.Value)
            && ReferenceEquals(inner.TypeParams.Value[0], own) && ReferenceEquals(inner.TypeParams.Value[1], collision)
            && ReferenceEquals(inner.TypeParams.Value[2], secondCollision)
            && !inner.OuterTypeParamCount.IsPresent && !inner.OuterTypeParamOffset.IsPresent,
            "captured descriptor ownership, constraints, slot naming or own suffix identity changed");
        Require(!noOwn.TypeParams.IsPresent && noOwn.CapturedTypeParams.Value.Length == 1
            && ReferenceEquals(zero.TypeParams.Value, empty) && !zero.CapturedTypeParams.IsPresent
            && !zero.OuterTypeParamCount.IsPresent && !zero.OuterTypeParamOffset.IsPresent
            && nullCount.OuterTypeParamCount.IsNull && nullCount.OuterTypeParamOffset.Value == 0
            && nullCount.SemanticOwner.IsNull && nullCount.TypeParams.IsNull && second.Methods.IsNull,
            "captured-only, zero, absent or null field presence changed");
        Console.WriteLine("[late type placement] self-test OK (live occurrences, order, captured descriptors, presence)");
    }
}
