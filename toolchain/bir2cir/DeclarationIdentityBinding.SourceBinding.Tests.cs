using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationIdentityBinding
{
    static void SelfTestSourceBinding()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Selected reference binding: " + message); }
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var metadata = new ReferenceDotKtMetadata();
        metadata.MemberBindings.Add(new MemberBinding("Producer", "physicalRead", 1, null, false, true,
            ParamTypeNodes: new TypeNode[] { integer }, DeclarationId: "ordinary", DeclarationSourceName: "read",
            DeclarationPhysicalOwner: "PhysicalProducer"));
        metadata.MemberBindings.Add(new MemberBinding("Producer", "intrinsic", 1, "System.Math.Abs", false, true,
            ParamTypeNodes: new TypeNode[] { integer }, DeclarationId: "intrinsic"));
        metadata.MemberBindings.Add(new MemberBinding("Producer", "physicalFactory", 1, null, false, true,
            ParamTypeNodes: new TypeNode[] { integer }, DeclarationId: "factory", CollectionFactoryKind: "list"));
        metadata.MemberBindings.Add(new MemberBinding("kotlin.Array", "physicalMember", 0, null, false, false,
            ParamTypeNodes: Array.Empty<TypeNode>(), DeclarationId: "alias"));
        var refs = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        var function = new TypeNode.Fn(false, integer, new TypeNode[] { text });
        var signature = new TypeNode[] { new TypeNode.Tv("method", 1) };
        var direct = new BirNewDelegate { Method = "intrinsic", DeclarationId = "intrinsic", FuncType = function,
            Sig = signature, TypeArgs = OptionalField<TypeNode[]>.Null,
            ResolvedMemberParams = OptionalField<TypeNode[]>.Null };
        var outer = new BirCallStatic { Method = "read", DeclarationId = "ordinary", Prop = "get",
            Args = new IBirExpression[] { direct } };
        var factory = new BirCallStatic { Method = "factory", DeclarationId = "factory", DeclarationReferencedFactory = false };
        var intrinsic = new BirCallInstance { Method = "intrinsic", DeclarationId = "intrinsic" };
        var bound = new BirNewBoundDelegate { Method = "intrinsic", DeclarationId = "intrinsic" };
        var alias = new BirCallInstance { Method = "member", DeclarationId = "alias", Prop = "get" };
        var local = new BirNewDelegate { Method = "local", DeclarationId = "ordinary" };
        var context = new BirCallStatic { Method = "coroutineContext", Prop = "get", DeclarationId = "context-unbound" };
        var unknown = new BirCallStatic { Method = "unknown", DeclarationId = "unknown" };
        IBirExpression[] expressions = { outer, factory, intrinsic, bound, alias, context, unknown };
        var statements = expressions.Select(expression => new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expression) }).ToArray();
        var method = new BirFunctionDeclaration { Name = "test", Body = statements.Cast<IBirStatement>().ToArray() };
        var root = new BirFileDeclaration { FileClass = "Consumer", Methods = new[] { method } };
        var methods = root.Methods.Value;
        var body = method.Body.Value;
        BindReferenced(root, refs, new HashSet<string>(), deferUnknown: true);
        Require(ReferenceEquals(root.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(statements[0].Expr.Value, outer) && outer.Method.Value == "physicalRead"
            && outer.Owner.Value == new TypeNode.Fqn("PhysicalProducer") && outer.CalleeOwner.Value == outer.Owner.Value
            && !outer.Prop.IsPresent && outer.DeclarationId.Value == "ordinary", "owning postorder/physical association/early ID retention changed");
        Require(outer.Args.Value[0] is BirClrStaticDelegate target && target.Method.Value == "Abs"
            && target.ClrType.Value == new TypeNode.Fqn("System.Math")
            && target.ArgTypes.Value.Single() == new TypeNode.Tv("method", 1)
            && !ReferenceEquals(target.ArgTypes.Value, signature)
            && target.TypeArgs.IsNull && target.ResolvedMemberParams.IsNull
            && ReferenceEquals(target.FuncType.Value, function), "intrinsic delegate lost exact signature, presence or independent argument vector");
        Require(factory.DeclarationReferencedFactory.Value && !factory.Owner.IsPresent && factory.Method.Value == "factory"
            && intrinsic.DeclarationReferencedIntrinsic.Value && intrinsic.Method.Value == "intrinsic"
            && bound.DeclarationReferencedIntrinsic.Value && alias.Method.Value == "member" && alias.Prop.Value == "get"
            && context.Method.Value == "coroutineContext" && context.DeclarationId.Value == "context-unbound"
            && unknown.DeclarationId.Value == "unknown", "factory/intrinsic/alias/context/deferred-unbound policies diverged");

        var localRoot = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = local } } } } };
        BindReferenced(localRoot, refs, new HashSet<string> { "ordinary" }, deferUnknown: true);
        Require(local.Method.Value == "local" && !local.CalleeOwner.IsPresent, "local selected identity was rebound externally");

        var empty = BindStaticIntrinsicDelegate(new BirNewDelegate { FuncType = function, Sig = Array.Empty<TypeNode>() }, "intrinsic", "System.Math.Abs");
        var receiverFunction = new TypeNode.Fn(false, integer, new TypeNode[] { integer }, text);
        var fallback = BindStaticIntrinsicDelegate(new BirNewDelegate { FuncType = receiverFunction }, "intrinsic", "System.Math.Abs");
        Require(empty.ArgTypes.Value.Length == 0 && fallback.ArgTypes.Value.SequenceEqual(new TypeNode[] { text, integer }),
            "empty selected signature or explicit extension receiver order was inferred differently");

        var late = new BirCallStatic { Method = "factory", DeclarationId = "factory", DeclarationReferencedFactory = false };
        var lateRoot = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = late } } } } };
        BindReferenced(lateRoot, refs, new HashSet<string>());
        Require(late.Method.Value == "physicalFactory" && !late.DeclarationReferencedFactory.IsPresent && !late.DeclarationId.IsPresent,
            "declined factory was re-marked or late physical binding kept the consumed identity");
        var preserve = new BirCallStatic { Method = "read", DeclarationId = "ordinary" };
        var preservedRoot = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = preserve } } } } };
        BindReferenced(preservedRoot, refs, new HashSet<string>(), preserveForScalarResolution: true);
        Require(preserve.DeclarationId.Value == "ordinary", "scalar-resolution identity was consumed too early");
        Console.WriteLine("[typed selected reference binding] self-test OK (shared plan, exact identity, owning postorder, intrinsic/factory/context and signatures)");
    }
}
