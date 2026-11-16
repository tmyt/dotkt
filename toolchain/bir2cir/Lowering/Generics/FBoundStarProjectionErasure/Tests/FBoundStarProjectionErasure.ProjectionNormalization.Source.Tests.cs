using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void SelfTestSourceProjectionNormalization()
    {
        TypeNode Leaf() => new TypeNode.Fqn("kotlin.String");
        TypeNode Applied(bool projected, string name, string variance) => new TypeNode.Fqn(name,
            new[] { projected ? new TypeNode.Projection(variance, Leaf()) : Leaf() });
        TypeNode Shape(bool projected) => new TypeNode.Mod(true,
            new TypeNode.Fqn("Modifier"), new TypeNode.FnPtr(1, 0, 1,
                new TypeNode.ByRef(Applied(projected, "Out", "out")), new TypeNode[] {
                    new TypeNode.Array(new TypeNode.Oblivious(Applied(projected, "In", "in")), 2, false),
                    new TypeNode.Fn(false, new TypeNode.Nullable(Applied(projected, "Out", "out")),
                        new[] { Applied(projected, "In", "in") }, new TypeNode.Ptr(Applied(projected, "Out", "out")),
                        Ctx: new[] { Applied(projected, "Out", "out"), new TypeNode.Tv("type", 0), new TypeNode.Star() }),
                }));
        var projected = Applied(true, "Out", "out");
        var shape = new BirFieldDeclaration { Name = "shape", Type = Shape(true) };
        var opposite = new BirFieldDeclaration { Name = "opposite", Type = Applied(true, "Out", "in") };
        var invariant = new BirFieldDeclaration { Name = "invariant", Type = Applied(true, "Invariant", "out") };
        var oppositeBefore = opposite.Type.Value;
        var invariantBefore = invariant.Type.Value;
        var frozen = new BirFunctionDeclaration { Name = "sourceFacts", Params = Array.Empty<BirParameter>(), Ret = projected,
            NullableGenericRet = projected, RetKotlinType = projected, CompanionReceiver = projected,
            InlineBir = ExecutableBirCodec.EncodeInline(BirCarrier.JsonV1, new BirInlinePayload {
                V = 1, Params = Array.Empty<BirParameter>(), Ret = projected, Body = Array.Empty<IBirStatement>(),
            }) };
        var bytes = frozen.InlineBir.Value;
        var reference = new BirMemberReference { DeclaringType = projected, ReturnType = projected,
            ParameterTypes = new[] { projected } };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn("Out"), Method = "read", MemberRef = reference,
            Sig = new[] { projected }, TypeArgs = OptionalField<TypeNode[]>.Null, Args = Array.Empty<IBirExpression>() };
        frozen.Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } };
        var file = new BirFileDeclaration { FileClass = "Projection", Methods = new[] { frozen }, Fields = new[] { shape, opposite, invariant },
            Types = new IBirTypeDeclaration[] {
                new BirClassDefinition { Kind = "class", Name = "Out", TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } } },
                new BirInterfaceDefinition { Kind = "interface", Name = "In", TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "in" } } },
                new BirClassDefinition { Kind = "class", Name = "Invariant", TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
            } };
        var fields = file.Fields.Value; var body = frozen.Body.Value;
        NormalizeRedundantProjections(new[] { file }, null);
        if (shape.Type.Value != Shape(false) || opposite.Type.Value != oppositeBefore || invariant.Type.Value != invariantBefore
            || !ReferenceEquals(file.Fields.Value, fields) || !ReferenceEquals(frozen.Body.Value, body)
            || !ReferenceEquals(file.Fields.Value[0], shape) || !ReferenceEquals(call.MemberRef.Value, reference)
            || reference.DeclaringType.Value != Applied(false, "Out", "out") || reference.ReturnType.Value != Applied(false, "Out", "out")
            || reference.ParameterTypes.Value[0] != Applied(false, "Out", "out") || call.Sig.Value[0] != Applied(false, "Out", "out")
            || call.OwnerType.Value is not TypeNode.Fqn { Args: null } || !call.TypeArgs.IsNull
            || frozen.Ret.Value != Applied(false, "Out", "out")
            || !ReferenceEquals(frozen.NullableGenericRet.Value, projected) || !ReferenceEquals(frozen.RetKotlinType.Value, projected)
            || !ReferenceEquals(frozen.CompanionReceiver.Value, projected) || !ReferenceEquals(frozen.InlineBir.Value, bytes))
            throw new InvalidOperationException("Owning variance normalization changed structural type coverage, source facts, presence or occurrences");
        Console.WriteLine("[source projection normalization] self-test OK (full structured types, exact variance, source/opaque boundaries)");
    }
}
