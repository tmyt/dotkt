using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

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
        ApplyAll(new[] { first, second }, (_, _) => { });
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
        SelfTestPlacementMoves();
        Console.WriteLine("[late type placement] self-test OK (live occurrences, ordered move batches, captured descriptors, presence)");
    }

    static void SelfTestPlacementMoves()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Late placement move notification: " + message); }
        static BirFunctionDeclaration Method(string name, string owner = null) => new() {
            Name = name, SemanticOwner = owner == null ? default : OptionalField<string>.Of(owner),
            Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>(),
        };
        var first = Method("first", "Destination");
        var second = Method("second", "Destination");
        var third = Method("third", "OtherDestination");
        var tail = Method("tail", "Destination");
        var staying = Method("staying", "SourceFile");
        var plain = Method("plain"); plain.SemanticOwner = OptionalField<string>.Null;
        var prefix = Method("prefix");
        var shadowMethods = new[] { Method("shadow-prefix") };
        var shadow = new BirClassDefinition { Name = "Destination", Methods = shadowMethods };
        var outer = new BirClassDefinition { Name = "Outer", TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var frame = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "U" } };
        var destination = new BirClassDefinition { Name = "Destination", NestedIn = "Outer",
            OuterTypeParamCount = 1, TypeParams = frame, Methods = new[] { prefix } };
        var other = new BirClassDefinition { Name = "OtherDestination", Methods = OptionalField<BirFunctionDeclaration[]>.Null };
        var source = new BirFileDeclaration { FileClass = "SourceFile",
            Methods = new[] { first, staying, second, plain, third }, Types = new IBirTypeDeclaration[] { shadow } };
        var host = new BirFileDeclaration { FileClass = "Host", Methods = OptionalField<BirFunctionDeclaration[]>.Null,
            Types = new IBirTypeDeclaration[] { outer, destination, other } };
        var later = new BirFileDeclaration { FileClass = "Later", Methods = new[] { tail } };
        var emptyMethods = Array.Empty<BirFunctionDeclaration>();
        var empty = new BirFileDeclaration { FileClass = "Empty", Methods = emptyMethods };
        var firstParameters = first.Params.Value;
        var notifications = 0;
        ApplyAll(new[] { source, host, later, empty }, (actualSource, moves) => {
            Require(ReferenceEquals(destination.TypeParams.Value, frame) && destination.OuterTypeParamCount.Value == 1
                && !destination.CapturedTypeParams.IsPresent,
                "move notification ran after the semantic/captured parameter layout split");
            Require(moves.All(move => !move.Method.SemanticOwner.IsPresent
                && ReferenceEquals(move.Destination.Methods.Value[move.DestinationOrdinal], move.Method)
                && !actualSource.Methods.Value.Any(method => ReferenceEquals(method, move.Method))),
                "reported occurrence was cloned, not yet appended, or still attached to its source vector");
            if (notifications == 0)
                Require(ReferenceEquals(actualSource, source) && moves.Count == 3
                    && ReferenceEquals(moves[0].Method, third) && moves[0].SourceOrdinal == 4
                    && ReferenceEquals(moves[0].Destination, other) && moves[0].DestinationOrdinal == 0
                    && ReferenceEquals(moves[1].Method, second) && moves[1].SourceOrdinal == 2
                    && ReferenceEquals(moves[1].Destination, destination) && moves[1].DestinationOrdinal == 1
                    && ReferenceEquals(moves[2].Method, first) && moves[2].SourceOrdinal == 0
                    && ReferenceEquals(moves[2].Destination, destination) && moves[2].DestinationOrdinal == 2
                    && actualSource.Methods.Value.SequenceEqual(new[] { staying, plain }),
                    "batch lost original source ordinals, reverse mutation order, destination prefix or remaining order");
            else
                Require(notifications == 1 && ReferenceEquals(actualSource, later) && moves.Count == 1
                    && ReferenceEquals(moves[0].Method, tail) && moves[0].SourceOrdinal == 0
                    && ReferenceEquals(moves[0].Destination, destination) && moves[0].DestinationOrdinal == 3
                    && actualSource.Methods.Value.Length == 0,
                    "later-file batch lost the current append ordinal or was emitted for a no-move file");
            notifications++;
        });
        Require(notifications == 2 && ReferenceEquals(shadow.Methods.Value, shadowMethods)
            && ReferenceEquals(destination.Methods.Value[0], prefix)
            && destination.Methods.Value.SequenceEqual(new[] { prefix, second, first, tail })
            && other.Methods.Value.Single() == third && ReferenceEquals(first.Params.Value, firstParameters)
            && !staying.SemanticOwner.IsPresent && plain.SemanticOwner.IsNull && host.Methods.IsNull
            && ReferenceEquals(empty.Methods.Value, emptyMethods),
            "last-wins cross-file selection, actual continuing identity, or null/empty/no-move presence changed");
    }
}
