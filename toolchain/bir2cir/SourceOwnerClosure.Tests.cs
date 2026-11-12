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
            Params = new[] { new BirParameter { Name = "item", Type = variable },
                new BirParameter { Name = "nullable", Type = new TypeNode.Nullable(variable) },
                new BirParameter { Name = "platform", Type = new TypeNode.Oblivious(variable) } },
            Body = new IBirStatement[] {
                Return(Call(new BirLocal { Name = "item", Sty = text })),
                Return(Call(new BirCallStatic { Ret = variable, Owner = new TypeNode.Fqn("Factory"), Method = "get" })),
                Return(Call(new BirLocal { Name = "item" }, Bound(text))),
                Return(lambda),
                Return(Call(new BirLocal { Name = "nullable" })), Return(Call(new BirLocal { Name = "platform" })),
                new BirExpressionStatement { Expr = new BirNativeInstance {
                    Type = new TypeNode.Fqn("Owner"), Recv = new BirLocal { Name = "item" }, Method = "read" } },
                new BirExpressionStatement { Expr = new BirGenericInstance {
                    Type = new TypeNode.Fqn("Owner"), Recv = new BirLocal { Name = "item" }, Method = "read" } },
                new BirExpressionStatement { Expr = new BirPropertyGet {
                    Type = new TypeNode.Fqn("Owner"), Recv = new BirLocal { Name = "item" } } },
                new BirExpressionStatement { Expr = new BirPropertySet {
                    Type = new TypeNode.Fqn("Owner"), Recv = new BirLocal { Name = "item" } } },
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
            Ctors = new[] { new BirConstructor {
                Params = new[] { new BirParameter { Name = "inner", Type = new TypeNode.Tv("type", 1) } },
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = Call(new BirLocal { Name = "inner" }) } },
            } },
        };
        var input = new BirFileDeclaration { FileClass = "Source", Methods = new[] { method }, Types = new IBirTypeDeclaration[] {
            captured,
            new BirInterfaceDefinition { Name = "Contract", Kind = "interface", Methods = new[] { method } },
            new BirEnumDefinition { Name = "Enum", Kind = "enum", Methods = new[] { method } },
        } };
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
            || Returned(4).OwnerType.Value != Bound(integer) || Returned(5).OwnerType.Value != Bound(integer)
            || Returned(0)._erasedConstraintDispatch.Value
            || ((BirCallInstance)((BirReturn)((BirClassDefinition)rewritten.Types.Value[0]).Methods.Value[0].Body.Value[0]).Value.Value)
                .OwnerType.Value != Bound(text))
            throw new InvalidOperationException("Source closure lost declaration, result or captured/transitive frames");
        var rewrittenLambda = (BirNewSuspendLambda)((BirReturn)statements[3]).Value.Value;
        if (((BirCallInstance)rewrittenLambda.CapValues.Value[0]).OwnerType.Value != Bound(integer)
            || rewrittenLambda.CapValues.Value[1] != null || !ReferenceEquals(rewrittenLambda.Body.Value, lambda.Body.Value)
            || suspendedCall.OwnerType.Value != new TypeNode.Fqn("Owner"))
            throw new InvalidOperationException("Source closure crossed the suspend-lambda declaration frame");

        var ambiguous = input with { Methods = new[] { method with { Body = new IBirStatement[] {
            new BirVariable { Name = "item", Type = integer }, Return(Call(new BirLocal { Name = "item" })),
        } } } };
        var unresolved = (BirCallInstance)((BirReturn)SourceOwnerClosure.Apply(ambiguous).Methods.Value[0].Body.Value[1]).Value.Value;
        if (unresolved._erasedConstraintDispatch.IsPresent || unresolved.OwnerType.Value != new TypeNode.Fqn("Owner"))
            throw new InvalidOperationException("Source closure guessed an ambiguous local's declared type");

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
        var scope = new Dictionary<string, TypeNode>(StringComparer.Ordinal) {
            ["array"] = new TypeNode.Array(variable), ["outer"] = text,
        };
        foreach (var (node, expectedType) in new (BirGraphValue, TypeNode)[] {
            (new BirConditional { Then = new BirThrowExpression(), Else = new BirConstant { Type = integer } }, integer),
            (new BirValueBlock { Stmts = new IBirStatement[] { new BirVariable { Name = "inner", Type = variable } },
                Result = new BirLocal { Name = "inner" } }, variable),
            (new BirValueBlock { Result = new BirLocal { Name = "outer" } }, text),
            (new BirArrayGet { Array = new BirLocal { Name = "array" } }, variable),
            (new BirArrayGet { Array = new BirConstant { Type = new TypeNode.Fqn("kotlin.IntArray") } }, integer),
            (new BirNewArray { Elem = variable }, new TypeNode.Fqn("kotlin.Array", new[] { variable })),
            (new BirNewArrayInit { Elem = variable }, new TypeNode.Fqn("kotlin.Array", new[] { variable })),
            (new BirNewArraySized { Elem = variable }, new TypeNode.Fqn("kotlin.Array", new[] { variable })),
            (new BirSpreadConcat { Elem = variable }, new TypeNode.Fqn("kotlin.Array", new[] { variable })),
            (new BirEnumValues { Type = variable }, new TypeNode.Fqn("kotlin.Array", new[] { variable })),
            (new BirNewClosure { ClosureType = new TypeNode.Fqn("CapturedValue", new[] { integer }) }, new TypeNode.Fqn("CapturedValue")),
            (new BirNullableWrap { Elem = variable }, new TypeNode.Nullable(variable)),
            (new BirCast { Type = integer, E = new BirLocal { Name = "outer" } }, integer),
            (new BirDelegateInvoke { FuncType = new TypeNode.Fn(false, variable, Array.Empty<TypeNode>()) }, variable),
        })
            if (BirGraphStaticType.Surface(node, scope) != expectedType
                || StaticType.Surface(node.ToDom(), BirScope.FromVars(scope)) != expectedType)
                throw new InvalidOperationException("Owning structural type facts diverged for " + node.GetType().Name);
        Console.WriteLine("[owning source owner closure] self-test OK (frames, source ownership, scoped suspension, ambiguity, cycles, erased proofs, stamps)");
    }
}
