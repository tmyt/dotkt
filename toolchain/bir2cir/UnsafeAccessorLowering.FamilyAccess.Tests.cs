using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    internal static void FamilyAccessSelfTest()
    {
        TypeNode.Fqn Owner(string name, string scope) => new(name, new TypeNode[] { new TypeNode.Tv(scope, 0) });
        BirTypeParameter[] Parameters() => new[] { new BirTypeParameter { Name = "T" } };
        BirCallInstance Call(IBirExpression receiver, string scope, string method = "read") => new() {
            OwnerType = Owner("Base", scope), Recv = OptionalField<IBirExpression>.Of(receiver), Method = method, Virtual = true,
            Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = new TypeNode.Tv(scope, 0),
        };
        var own = Call(new BirThis(), "type");
        var other = Call(SourceLocal("derived"), "method");
        var alias = Call(SourceLocal("alias"), "method");
        var block = new BirValueBlock { Stmts = new IBirStatement[] { new BirVariable {
            Name = "alias", Type = Owner("Derived", "method"), Init = OptionalField<IBirExpression>.Of(SourceLocal("derived")),
        } }, Result = OptionalField<IBirExpression>.Of(alias) };
        var field = new BirFieldGet { OwnerType = Owner("Base", "type"), Recv = OptionalField<IBirExpression>.Of(new BirThis()), Name = "slot" };
        var bound = new BirNewBoundDelegate { OwnerType = Owner("Base", "type"), Recv = OptionalField<IBirExpression>.Of(new BirThis()),
            Method = "read", Sig = Array.Empty<TypeNode>(), FuncType = new TypeNode.Fn(false, new TypeNode.Tv("type", 0), Array.Empty<TypeNode>()) };
        var invalid = Call(SourceLocal("base"), "method");
        var privateCrossing = Call(new BirThis(), "type", "hidden");
        var captured = Call(new BirFieldGet { OwnerType = Owner("Capture", "type"),
            Recv = OptionalField<IBirExpression>.Of(new BirThis()), Name = "outer" }, "type");
        var function = new TypeNode.Fn(false, new TypeNode.Tv("type", 0), new TypeNode[] { new TypeNode.Tv("type", 0) });
        var functionCall = Call(new BirThis(), "type", "transform");
        functionCall.Sig = new TypeNode[] { function }; functionCall.Ret = function;
        functionCall.Args = new IBirExpression[] { new BirConstant {
            Type = new TypeNode.Nullable(function), Value = new BirLiteral.NullLiteral() } };
        var use = new BirFunctionDeclaration { Name = "use", TypeParams = new[] { new BirTypeParameter { Name = "U" } },
            Params = new[] { SourceParameter("derived", Owner("Derived", "method")), SourceParameter("base", Owner("Base", "method")) },
            Body = new IBirStatement[] { SourceTestStatement(own), SourceTestStatement(other), SourceTestStatement(block),
                SourceTestStatement(field), SourceTestStatement(bound), SourceTestStatement(invalid), SourceTestStatement(privateCrossing),
                SourceTestStatement(functionCall) } };
        var invoke = new BirFunctionDeclaration { Name = "invoke", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { SourceTestStatement(captured) } };
        var baseType = new BirClassDefinition { Name = "Base", NestedIn = "Container", Vis = "private", TypeParams = Parameters(),
            Fields = new[] { new BirFieldDeclaration { Name = "slot", Vis = "protected", Type = new TypeNode.Tv("type", 0) } },
            Methods = new[] {
                new BirFunctionDeclaration { Name = "read", Vis = "protected", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Tv("type", 0) },
                new BirFunctionDeclaration { Name = "hidden", Vis = "private", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Tv("type", 0) },
                new BirFunctionDeclaration { Name = "transform", Vis = "protected", Params = new[] { SourceParameter("block", function) }, Ret = function },
            } };
        var derived = new BirClassDefinition { Name = "Derived", NestedIn = "Container", Vis = "private", TypeParams = Parameters(),
            Base = Owner("Base", "type"), Methods = new[] { use } };
        var carrier = new BirClassDefinition { Name = "Capture", NestedIn = "Derived", CapturedTypeParams = Parameters(),
            Fields = new[] { new BirFieldDeclaration { Name = "outer", Type = Owner("Derived", "type") } }, Methods = new[] { invoke } };
        var root = new BirFileDeclaration { FileClass = "FamilyProbe", Methods = Array.Empty<BirFunctionDeclaration>(), Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Container", Vis = "public" }, baseType, derived, carrier,
            new BirInterfaceDefinition { Name = "Face", TypeParams = Parameters() },
            new BirClassDefinition { Name = "Implementation", TypeParams = Parameters(), Interfaces = new TypeNode[] { Owner("Face", "type") } },
            new BirClassDefinition { Name = "System.Func", Vis = "public", TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "R" } } },
        } };
        var direct = new BirGraphValue[] { own, other, alias, field, bound, captured, functionCall };
        var snapshots = direct.Select(n => n.DeepClone()).ToArray();
        ApplyAll(new[] { root }, null, new SourceAccessTestTransitions());
        RequireSourceAccess(ReferenceEquals(SourceTestExpression(use, 0), own) && ReferenceEquals(SourceTestExpression(use, 1), other)
            && ReferenceEquals(((BirValueBlock)SourceTestExpression(use, 2)).Result.Value, alias)
            && ReferenceEquals(SourceTestExpression(use, 3), field) && ReferenceEquals(SourceTestExpression(use, 4), bound)
            && ReferenceEquals(SourceTestExpression(invoke, 0), captured) && ReferenceEquals(SourceTestExpression(use, 7), functionCall),
            "legal family edges lost their actual continuing occurrences");
        for (var index = 0; index < direct.Length; index++)
            RequireSourceAccess(direct[index].StructurallyEquals(snapshots[index]), "legal family edge changed owner/frame/dispatch");
        RequireSourceAccess(SourceTestExpression(use, 5) is BirCallStatic && SourceTestExpression(use, 6) is BirCallStatic,
            "arbitrary base receiver/private crossing acquired family permission");
        var types = SupertypeGraph.CollectSource(new[] { root });
        RequireSourceAccess(SupertypeGraph.ReachesDeclaration(Owner("Implementation", "type"), Owner("Face", "type"), types, null)
            && !SupertypeGraph.ReachesClassDeclaration(Owner("Implementation", "type"), Owner("Face", "type"), types, null),
            "interface implementation granted class-family permission");
        Console.WriteLine("[protected family] self-test OK (lexical scopes, physical function/capture storage, exact frames and negative crossings)");
    }
}
