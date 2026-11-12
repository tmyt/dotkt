using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static class SourceOwnerClosureTests
{
    internal static void Run()
    {
        TypeNode variable = new TypeNode.Tv("method", 0);
        TypeNode integer = new TypeNode.Fqn("kotlin.Int");
        TypeNode text = new TypeNode.Fqn("kotlin.String");
        TypeNode.Fqn Bound(TypeNode argument) => new("Owner", new[] { argument });
        BirCallInstance Call(IBirExpression receiver, TypeNode owner = null) => new() {
            OwnerType = owner ?? new TypeNode.Fqn("Owner"), Method = "read",
            Recv = DotKt.Cir.OptionalField<IBirExpression>.Of(receiver), Args = Array.Empty<IBirExpression>(),
        };
        BirReturn Return(IBirExpression value) => new() { Value = DotKt.Cir.OptionalField<IBirExpression>.Of(value) };
        var captureCall = Call(new BirLocal { Name = "item" });
        var suspendedCall = Call(new BirLocal { Name = "item" });
        var lambda = new BirNewSuspendLambda {
            Params = Array.Empty<BirLambdaParameter>(),
            TypeParams = new[] { new BirTypeParameter { Name = "S", Constraints = new TypeNode[] { Bound(text) } } },
            CapValues = new IBirExpression[] { captureCall, null }, Body = new IBirStatement[] { Return(suspendedCall) },
        };
        var method = new BirFunctionDeclaration {
            Name = "source", TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { Bound(integer) } } },
            Params = new[] { new BirParameter { Name = "item", Type = variable } },
            Body = new IBirStatement[] {
                Return(Call(new BirLocal { Name = "item", Sty = text })),
                Return(Call(new BirCallStatic { Ret = variable, Owner = new TypeNode.Fqn("Factory"), Method = "get" })),
                Return(Call(new BirLocal { Name = "item" }, Bound(text))),
                Return(lambda),
            },
        };
        var captured = new BirClassDefinition {
            Name = "Captured", Kind = "class",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer", Constraints = new TypeNode[] { Bound(text) } } },
            TypeParams = new[] { new BirTypeParameter { Name = "Inner", Constraints = new TypeNode[] { new TypeNode.Tv("type", 0) } } },
            Methods = new[] { new BirFunctionDeclaration {
                Name = "inheritedFrame", Params = new[] { new BirParameter { Name = "inner", Type = new TypeNode.Tv("type", 1) } },
                Body = new IBirStatement[] { Return(Call(new BirLocal { Name = "inner" })) },
            } },
        };
        var input = new BirFileDeclaration { FileClass = "Source", Methods = new[] { method }, Types = new IBirTypeDeclaration[] { captured } };
        var before = input.ToDom();
        var expected = before.DeepClone();
        ConstrainedTypeParameterReceiverBinding.CloseOpenOwners(new[] { expected });
        var rewritten = SourceOwnerClosure.Apply(input);
        if (!JsonNode.DeepEquals(expected, rewritten.ToDom()) || !JsonNode.DeepEquals(before, input.ToDom()))
            throw new InvalidOperationException("Owning source closure changed source rules or mutated its input");
        var statements = rewritten.Methods.Value[0].Body.Value;
        BirCallInstance Returned(int index) => (BirCallInstance)((BirReturn)statements[index]).Value.Value;
        if (Returned(0).OwnerType.Value != Bound(integer) || Returned(1).OwnerType.Value != Bound(integer)
            || Returned(2).OwnerType.Value != Bound(text) || !Returned(0)._erasedConstraintDispatch.HasValue
            || Returned(0)._erasedConstraintDispatch.Value
            || ((BirCallInstance)((BirReturn)((BirClassDefinition)rewritten.Types.Value[0]).Methods.Value[0].Body.Value[0]).Value.Value)
                .OwnerType.Value != Bound(text))
            throw new InvalidOperationException("Source closure lost declaration, result or captured/transitive frames");
        var rewrittenLambda = (BirNewSuspendLambda)((BirReturn)statements[3]).Value.Value;
        if (((BirCallInstance)rewrittenLambda.CapValues.Value[0]).OwnerType.Value != Bound(integer)
            || rewrittenLambda.CapValues.Value[1] != null || !ReferenceEquals(rewrittenLambda.Body.Value, lambda.Body.Value)
            || suspendedCall.OwnerType.Value != new TypeNode.Fqn("Owner"))
            throw new InvalidOperationException("Source closure crossed the suspend-lambda declaration frame");

        foreach (var (constraints, erased, selected) in new[] {
            (new TypeNode[] { Bound(integer), Bound(text) }, Array.Empty<TypeNode>(), false),
            (new TypeNode[] { new TypeNode.Tv("method", 0) }, Array.Empty<TypeNode>(), false),
            (Array.Empty<TypeNode>(), new TypeNode[] { Bound(integer) }, true),
        })
        {
            var source = input with { Methods = new[] { method with {
                TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = constraints, ErasedInnerConstraints = erased } },
                Body = new IBirStatement[] { Return(Call(new BirLocal { Name = "item" })) },
            } } };
            var call = (BirCallInstance)((BirReturn)SourceOwnerClosure.Apply(source).Methods.Value[0].Body.Value[0]).Value.Value;
            if (call._erasedConstraintDispatch.HasValue != selected
                || selected && (call.OwnerType.Value != Bound(integer) || !call._erasedConstraintDispatch.Value))
                throw new InvalidOperationException("Source closure guessed an ambiguous/cyclic bound or lost erased-edge provenance");
        }
        var stamp = new BirCallInstance { Sty = text, Ret = integer, DynRet = variable };
        var empty = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        if (BirGraphStaticType.Surface((IBirExpression)stamp, empty) != text
            || BirGraphStaticType.Surface((IBirExpression)(stamp with { Sty = default }), empty) != integer
            || BirGraphStaticType.Surface((IBirExpression)(stamp with { Sty = default, Ret = default }), empty) != variable)
            throw new InvalidOperationException("Owning source type facts changed result-stamp precedence");
        Console.WriteLine("[owning source owner closure] self-test OK (frames, source ownership, scoped suspension, ambiguity, cycles, erased proofs, stamps)");
    }
}
