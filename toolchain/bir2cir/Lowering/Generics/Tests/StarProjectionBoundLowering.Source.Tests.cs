using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StarProjectionBoundLowering
{
    internal static void SourceSelfTest()
    {
        static void Need(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning star bounds: " + message); }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var element = new TypeNode.Fqn("Element"); var any = new TypeNode.Fqn("kotlin.Any");
        var original = new TypeNode.Fqn("Pair", new TypeNode[] { any, any });
        var expected = new TypeNode.Fqn("Pair", new TypeNode[] { element, element });
        var bounds = new Dictionary<string, TypeNode[]> {
            ["Pair"] = new TypeNode[] { element, new TypeNode.Tv("type", 0) },
            ["Recursive"] = new TypeNode[] { new TypeNode.Fqn("Recursive", new TypeNode[] { new TypeNode.Tv("type", 0) }) },
        };
        var recursive = new TypeNode.Fqn("Recursive", new TypeNode[] { any });
        var function = new TypeNode.Fn(true, original, new TypeNode[] { new TypeNode.Array(original),
            new TypeNode.ByRef(original) }, original, Ctx: new TypeNode[] { original });
        var field = new BirFieldDeclaration { Name = "slot", Type = function, KotlinType = function };
        var descriptor = new BirMethodImpl { Owner = original, Member = "read", Params = new TypeNode[] { original }, Ret = original };
        var call = new BirCallStatic { Owner = original, OwnerType = original, Method = "read", Ret = original,
            Args = Array.Empty<IBirExpression>() };
        var method = new BirFunctionDeclaration { Name = "run", Params = Array.Empty<BirParameter>(), Ret = recursive,
            SuspendSourceRet = original, ClrInterfaceImpls = new[] { descriptor },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } } };
        var file = new BirFileDeclaration { Fields = new[] { field }, Methods = new[] { method } };
        var body = method.Body.Value;
        Apply(file, bounds, references);
        Need(field.Type.Value is TypeNode.Fn updated && updated.Ret == expected && updated.Recv == expected
            && updated.Params[0] == new TypeNode.Array(expected) && updated.Params[1] == new TypeNode.ByRef(expected)
            && updated.Ctx.Single() == expected && ReferenceEquals(field.KotlinType.Value, function),
            "nested Fn/array/byref/receiver/context closure or encoded source preservation changed");
        Need(descriptor.Owner.Value == original && descriptor.Params.Value.Single() == expected && descriptor.Ret.Value == expected
            && call.Owner.Value == original && call.OwnerType.Value == expected && call.Ret.Value == expected
            && method.SuspendSourceRet.Value == expected && method.Ret.Value == recursive,
            "static/descriptor owner exclusion, structured frozen field or F-bound termination changed");
        Need(ReferenceEquals(file.Fields.Value[0], field) && ReferenceEquals(file.Methods.Value[0], method)
            && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(((BirExpressionStatement)body[0]).Expr.Value, call),
            "star substitution replaced owning declarations or operands");
        Console.WriteLine("[owning star bounds] self-test OK (same bounds snapshot, left-to-right closure, F-bound termination, exact identity slots and typed signatures)");
    }
}
