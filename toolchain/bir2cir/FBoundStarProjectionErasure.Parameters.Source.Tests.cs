using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ParameterProjectionSourceSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning parameter projection: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("System.String");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        TypeNode.Fqn Box() => new("Box", new TypeNode[] { text });
        TypeNode.Fqn Inner() => new("Inner", new TypeNode[] { text });
        BirParameter Parameter(string name, TypeNode type) => new() { Name = name, Type = type };
        BirFunctionDeclaration Method(string name, params BirParameter[] parameters) => new() {
            Name = name, DeclarationId = name, Params = parameters, Ret = unit, Body = Array.Empty<IBirStatement>(),
        };
        BirClassDefinition Generic(string name) => new() { Name = name, Kind = "class", Vis = "public",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        BirAttribute Alias() => new() { Attr = new TypeNode.Fqn("kotlin.clr.ClrTypeAlias"), Args = new IBirAttributeValue[] {
            new BirConstant { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral("System.Object") },
        } };

        var box = Generic("Box");
        var inner = Generic("Inner");
        inner.SemanticOwner = "Owner";
        inner.Mods = new BirModifiers { Inner = true };
        inner.OuterTypeParamCount = 1;
        var normal = Parameter("normal", Box());
        var nullableSource = new TypeNode.Fqn("Semantic", new TypeNode[] { text });
        var nullable = Parameter("nullable", Box()) with { NullableGeneric = nullableSource, KotlinType = OptionalField<TypeNode>.Null };
        var saved = new TypeNode.Fqn("AlreadySaved");
        var firstWriter = Parameter("firstWriter", Box()) with { KotlinType = saved, NullableGeneric = nullableSource };
        // The explicit lexical-role fact is retained through Rewrite before the
        // independent ordinary value-slot policy selects its physical ABI.
        var lexical = Parameter("lexical", Box()) with { Outer = true };
        var absent = new BirParameter { Name = "absent" };
        var nullType = new BirParameter { Name = "null", Type = OptionalField<TypeNode>.Null };
        var ordinary = Method("ordinary", normal, nullable, firstWriter, lexical, absent, nullType);
        var methodVector = ordinary.Params.Value;
        var top = Method("top", Parameter("value", Box()));
        var accessor = Method("setValue", Parameter("value", Box())) with { PropertyName = "value", PropertyAccessor = "set" };
        var generated = Method("generated", Parameter("value", Box())) with { Generated = true };
        var intrinsic = Method("intrinsic", Parameter("value", Box())) with { Attrs = new[] {
            new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrIntrinsic") } } };
        var native = Method("native", Parameter("value", Box())) with { Overrides = new[] {
            new BirOverride { Owner = new TypeNode.Fqn("System.IDisposable"), Member = "Dispose" } } };
        var aliasOverride = Method("aliasOverride", Parameter("value", Box())) with { Overrides = new[] {
            new BirOverride { Owner = new TypeNode.Fqn("Alias"), Member = "source" } } };
        var inherited = Method("inherited", Parameter("value", Box())) with { Overrides = new[] {
            new BirOverride { Owner = new TypeNode.Fqn("SourceContract"), Member = "source" } } };
        var noIdentity = Method("noIdentity", Parameter("value", Box())) with { DeclarationId = default };
        var exclusions = new[] { accessor, generated, intrinsic, native, aliasOverride, noIdentity };

        var ctorInner = Parameter("inner", Inner());
        var ctorOuter = Parameter("outer", Inner()) with { Outer = true };
        var ctor = new BirConstructor { Params = new[] { ctorInner, ctorOuter }, Body = Array.Empty<IBirStatement>() };
        var ctorVector = ctor.Params.Value;
        var lambda = new BirLambdaParameter { Name = "value", Type = Inner() };
        var suspend = new BirLambdaParameter { Name = "value", Type = new TypeNode.ByRef(Box()) };
        var inline = new BirLambdaParameter { Name = "value", Type = Inner() };
        var closure = new BirClosureDefinition { Name = "Closure", Params = new[] { lambda }, Ret = unit,
            Body = Array.Empty<IBirStatement>() };
        var suspendExpression = new BirNewSuspendLambda { Params = new[] { suspend }, SuspendRet = unit,
            Body = Array.Empty<IBirStatement>() };
        var inlineExpression = new BirInlineLambda { Params = new[] { inline }, Body = Array.Empty<IBirStatement>() };
        ordinary.Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = new BirNewClosure { ClosureType = new TypeNode.Fqn("Closure"), SynthClass = closure } },
            new BirExpressionStatement { Expr = suspendExpression }, new BirExpressionStatement { Expr = inlineExpression },
        };
        var body = ordinary.Body.Value;
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Ctors = new[] { ctor },
            Methods = new[] { ordinary, inherited }.Concat(exclusions).ToArray(),
            InheritedClassMethods = new[] { new BirInheritedClassMethod { Member = "inheritedRow",
                Params = new[] { Parameter("value", new TypeNode.ByRef(Box())) }, Ret = unit } } };
        var generatedCtorParameter = Parameter("value", Inner());
        var generatedNested = new BirLambdaParameter { Name = "nested", Type = Inner() };
        var generatedOwner = new BirClassDefinition { Name = "Generated", Kind = "class", Generated = true,
            Methods = new[] { Method("generatedOwner", Parameter("value", Box())) },
            Ctors = new[] { new BirConstructor { Params = new[] { generatedCtorParameter }, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = new BirInlineLambda { Params = new[] { generatedNested } } },
            } } } };
        var aliasOwner = new BirClassDefinition { Name = "Alias", Kind = "class", Attrs = new[] { Alias() },
            Methods = new[] { Method("aliasOwner", Parameter("value", Box())) } };
        var definitionParameter = Parameter("definition", new TypeNode.ByRef(Box()));
        var definition = new BirInterfaceDefinition { Name = "SourceContract", Kind = "interface",
            Params = new[] { definitionParameter } };
        var file = new BirFileDeclaration { FileClass = "Parameters", Methods = new[] { top }, Types = new IBirTypeDeclaration[] {
            box, inner, owner, generatedOwner, aliasOwner, definition,
        } };
        var roots = new[] { file };
        var values = GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs);
        var bindings = PrepareCurrentDeclarations(roots, values, out var preparation);
        var demand = PrepareCarrierDemand(preparation, PrepareNativeProofs(bindings, refs, values), refs, values);
        var context = CarrierProjectionContext.ForSource(demand, refs);
        var indices = new Dictionary<string, int[]> { ["ordinary"] = new[] { 9 } };
        var declarations = file.Types.Value;
        var methods = owner.Methods.Value;
        ProjectParameterDeclarations(roots, context, indices);
        var boxCarrier = demand.Owners["Box"].ErasedName;
        var innerCarrier = demand.Owners["Inner"].ErasedName;
        Require(normal.Type.Value is TypeNode.Fqn f && f.Name == boxCarrier
            && top.Params.Value[0].Type.Value is TypeNode.Fqn topType && topType.Name == boxCarrier
            && inherited.Params.Value[0].Type.Value is TypeNode.Fqn inheritedType && inheritedType.Name == boxCarrier,
            "ordinary source, facade or source override ABI was not projected");
        Require(nullable.KotlinType.Value is TypeNode.Fqn { Name: "Semantic" } nullableMetadata
            && !ReferenceEquals(nullableMetadata.Args, nullableSource.Args) && ReferenceEquals(firstWriter.KotlinType.Value, saved)
            && normal.KotlinType.Value is TypeNode.Fqn { Name: "Box" }
            && !absent.Type.IsPresent && nullType.Type.IsNull && !absent.KotlinType.IsPresent && !nullType.KotlinType.IsPresent,
            "nullableGeneric precedence, independent saved vectors, first writer or presence changed");
        Require(exclusions.All(method => method.Params.Value[0].Type.Value is TypeNode.Fqn { Name: "Box" }
                && !method.Params.Value[0].KotlinType.IsPresent)
            && generatedOwner.Methods.Value[0].Params.Value[0].Type.Value is TypeNode.Fqn { Name: "Box" }
            && aliasOwner.Methods.Value[0].Params.Value[0].Type.Value is TypeNode.Fqn { Name: "Box" },
            "accessor, generated, intrinsic, native, binding-alias or identity-less exclusion changed");
        Require(ctorInner.Type.Value is TypeNode.Fqn ci && ci.Name == innerCarrier
            && ctorOuter.Type.Value is TypeNode.Fqn { Name: "Inner" }
            && generatedCtorParameter.Type.Value is TypeNode.Fqn { Name: "Inner" } && !generatedCtorParameter.KotlinType.IsPresent
            && lambda.Type.Value is TypeNode.Fqn lp && lp.Name == innerCarrier
            && inline.Type.Value is TypeNode.Fqn ip && ip.Name == innerCarrier
            && generatedNested.Type.Value is TypeNode.Fqn gn && gn.Name == innerCarrier
            && suspend.Type.Value is TypeNode.ByRef { Of: TypeNode.Fqn sp } && sp.Name == boxCarrier
            && definitionParameter.Type.Value is TypeNode.ByRef { Of: TypeNode.Fqn dp } && dp.Name == boxCarrier
            && owner.InheritedClassMethods.Value[0].Params.Value[0].Type.Value is TypeNode.ByRef { Of: TypeNode.Fqn ir }
                && ir.Name == boxCarrier,
            "constructor/outer, generated ctor-only exclusion, lambda or other declaration params role was skipped");
        Require(ReferenceEquals(file.Types.Value, declarations) && ReferenceEquals(owner.Methods.Value, methods)
            && ReferenceEquals(ordinary.Params.Value, methodVector) && ReferenceEquals(ctor.Params.Value, ctorVector)
            && ReferenceEquals(ordinary.Body.Value, body) && ReferenceEquals(closure.Params.Value[0], lambda)
            && ordinary.DeclarationId.Value == "ordinary" && ordinary.Ret.Value == unit
            && indices["ordinary"].SequenceEqual(new[] { 9, 3 }),
            "declaration/vector identity, return, exact physical index or append order changed");
        nullableSource.Args[0] = new TypeNode.Fqn("Mutated");
        Require(((TypeNode.Fqn)nullable.KotlinType.Value).Args[0] == text,
            "saved source fact shares a mutable type argument with its producer");
        ProjectParameterDeclarations(roots, context, indices);
        Require(indices["ordinary"].SequenceEqual(new[] { 9, 3 }) && ReferenceEquals(firstWriter.KotlinType.Value, saved),
            "repeat application duplicated indices or replaced the first source fact");
    }
}
