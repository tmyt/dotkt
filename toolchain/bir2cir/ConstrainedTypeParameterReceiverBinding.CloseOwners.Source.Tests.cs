using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    internal static void SelfTestSourceCloseOwners()
    {
        var ordinary = new TypeNode.Fqn("Node", new TypeNode[] { new TypeNode.Fqn("kotlin.Int") });
        var projected = new TypeNode.Fqn("Node", new TypeNode[] { new TypeNode.Star() });
        BirLocal Receiver(string name = "item") => new() { Name = name, Sty = new TypeNode.Fqn("object") };
        BirCallInstance Call(IBirExpression receiver, TypeNode owner = null) => new() {
            OwnerType = owner ?? new TypeNode.Fqn("Node"), Method = "read", Recv = OptionalField<IBirExpression>.Of(receiver),
            Args = Array.Empty<IBirExpression>(), TypeArgs = OptionalField<TypeNode[]>.Null, DeclarationId = "selected-read",
        };
        var direct = Call(Receiver());
        var closed = Call(Receiver(), new TypeNode.Fqn("Node", new TypeNode[] { new TypeNode.Fqn("kotlin.String") }));
        var native = new BirNativeInstance { Type = new TypeNode.Fqn("Node"), Method = "read", Recv = Receiver() };
        var generic = new BirGenericInstance { Type = new TypeNode.Fqn("Node"), Method = "read", Recv = Receiver() };
        var get = new BirPropertyGet { Type = new TypeNode.Fqn("Node"), Recv = Receiver() };
        var set = new BirPropertySet { Type = new TypeNode.Fqn("Node"), Recv = Receiver(), Value = Receiver("argument") };
        var capture = Call(Receiver()); var suspendedBody = Call(new BirLocal { Name = "inner" });
        var suspend = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(),
            CapValues = new IBirExpression[] { capture, null },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = suspendedBody } } };
        var retainedLocal = Receiver("erased"); retainedLocal.ErasedValueVariableFact = new TypeNode.Tv("method", 0);
        var retained = Call(retainedLocal);
        var erased = Call(Receiver("projected"), ordinary);
        var concrete = Call(new BirLocal { Name = "concrete", Sty = new TypeNode.Tv("method", 0) });
        var ambiguous = Call(new BirLocal { Name = "ambiguous" });
        var expressions = new IBirExpression[] { direct, closed, native, generic, get, set, suspend, retained, erased, concrete, ambiguous };
        var statements = expressions.Select(expression => (IBirStatement)new BirExpressionStatement {
            Expr = OptionalField<IBirExpression>.Of(expression),
        }).Concat(new IBirStatement[] { new BirVariable { Name = "ambiguous", Type = new TypeNode.Fqn("kotlin.String") } }).ToArray();
        var method = new BirFunctionDeclaration { Name = "use", Ret = new TypeNode.Fqn("kotlin.Unit"),
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { ordinary } },
                new BirTypeParameter { Name = "P" } },
            Params = new[] { new BirParameter { Name = "item", Type = new TypeNode.Tv("method", 0) },
                new BirParameter { Name = "erased", Type = new TypeNode.Fqn("object") },
                new BirParameter { Name = "projected", Type = new TypeNode.Tv("method", 1) },
                new BirParameter { Name = "concrete", Type = new TypeNode.Fqn("kotlin.String") },
                new BirParameter { Name = "ambiguous", Type = new TypeNode.Tv("method", 0) } },
            Body = statements, MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
                [1] = new TypeNode[] { projected },
            }) };
        var typeReceiver = Call(new BirLocal { Name = "value" }, new TypeNode.Fqn("PairNode"));
        var ownerBound = new TypeNode.Fqn("PairNode", new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Star() });
        var frame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 2, 1, 0 }, storageIndices: new[] { 0 });
        var facts = new SourceTypeFacts(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { ownerBound } }).MergeNullableFrame(frame);
        var constructor = new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("type", 2) } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = typeReceiver } } };
        var owner = new BirClassDefinition { Kind = "class", Name = "PhysicalOwner", KotlinSupertypesPre = facts,
            TypeParams = new[] { new BirTypeParameter { Name = "Storage" }, new BirTypeParameter { Name = "Other" }, new BirTypeParameter { Name = "Source" } },
            Ctors = new[] { constructor } };
        var file = new BirFileDeclaration { FileClass = "Use", Methods = new[] { method }, Types = new IBirTypeDeclaration[] { owner } };
        var parameters = method.Params.Value; var typeParameters = method.TypeParams.Value;
        var argument = set.Value.Value; var capturedValues = suspend.CapValues.Value; var innerBody = suspend.Body.Value;
        CloseOpenOwners(new[] { file });
        if (!ReferenceEquals(method.Body.Value, statements) || !ReferenceEquals(method.Params.Value, parameters)
            || !ReferenceEquals(method.TypeParams.Value, typeParameters) || !ReferenceEquals(file.Methods.Value[0], method)
            || direct.OwnerType.Value != ordinary || direct._erasedConstraintDispatch.Value
            || direct.DeclarationId.Value != "selected-read" || !direct.TypeArgs.IsNull
            || closed.OwnerType.Value is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.String" }] }
            || native.Type.Value != ordinary || generic.Type.Value != ordinary || get.Type.Value != ordinary || set.Type.Value != ordinary
            || !ReferenceEquals(set.Value.Value, argument) || retained.OwnerType.Value != ordinary
            || erased.OwnerType.Value != projected || !erased._erasedConstraintDispatch.Value
            || concrete.OwnerType.Value is not TypeNode.Fqn { Args: null } || concrete._erasedConstraintDispatch.IsPresent
            || ambiguous.OwnerType.Value is not TypeNode.Fqn { Args: null }
            || capture.OwnerType.Value != ordinary || suspendedBody.OwnerType.Value is not TypeNode.Fqn { Args: null }
            || !ReferenceEquals(suspend.CapValues.Value, capturedValues) || suspend.CapValues.Value[1] != null
            || !ReferenceEquals(suspend.Body.Value, innerBody)
            || typeReceiver.OwnerType.Value is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 2 }, TypeNode.Star] }
            || !typeReceiver._erasedConstraintDispatch.Value || !ReferenceEquals(owner.KotlinSupertypesPre.Value, facts)
            || !method.MethodTypeParameterBoundsPre.HasValue)
            throw new InvalidOperationException("Owning physical owner closing lost bound provenance, actual frames, scope or record identity");
        Console.WriteLine("[source physical owner closing] self-test OK (phase1 owners, retained/projected bounds, physical frame, capture scope)");
    }
}
