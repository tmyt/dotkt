using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReferenceExistentialAbiBinding
{
    internal static void SourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning reference ABI: " + message); }
        var carrier = new TypeNode.Fqn("abi.PhysicalCarrier");
        var semantic = new TypeNode.Fqn("abi.Semantic", new TypeNode[] { new TypeNode.Star() });
        var text = new TypeNode.Fqn("kotlin.String");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 1);
        var physicalResult = new TypeNode.Fn(false, carrier, new TypeNode[] { methodVariable }, ownerVariable);
        // The frontend restores the nested existential, not the enclosing
        // function shape. Keep the caller's frame in its semantic result too.
        var semanticFunctionResult = new TypeNode.Fn(false, semantic, new TypeNode[] { ownerVariable }, methodVariable);
        var rows = new[] {
            new MemberBinding("abi.Owner", "bind", 2, null, false, false, MethodArity: 2,
                ParamTypeNodes: new TypeNode[] { new TypeNode.Array(carrier), ownerVariable }, ReturnTypeNode: physicalResult),
            new MemberBinding("abi.Owner", "read", 0, null, false, true,
                ParamTypeNodes: Array.Empty<TypeNode>(), ReturnTypeNode: carrier),
            new MemberBinding("abi.Owner", "inputOnly", 1, null, false, true, MethodArity: 1,
                ParamTypeNodes: new TypeNode[] { carrier }, ReturnTypeNode: new TypeNode.Tv("method", 0)),
        };
        var refs = ReferenceMetadataIndex.CreateExistentialAbiSourceTestIndex(rows);
        var receiver = new BirLocal { Name = "receiver" };
        var first = new BirLocal { Name = "first" };
        var second = new BirLocal { Name = "second" };
        var suppliedSignature = new TypeNode[] { new TypeNode.Array(semantic), text };
        var bind = new BirCallInstance { OwnerType = new TypeNode.Fqn("abi.Owner", new TypeNode[] { methodVariable }),
            Method = "bind", Recv = E(receiver), Args = new IBirExpression[] { first, second },
            Sig = suppliedSignature, TypeArgs = new TypeNode[] { text, ownerVariable },
            Ret = semanticFunctionResult, Sty = semanticFunctionResult };
        var read = new BirCallStatic { Owner = new TypeNode.Fqn("abi.Owner"), Method = "read",
            Args = Array.Empty<IBirExpression>(), Ret = semantic, Sty = semantic };
        var block = new BirValueBlock { Type = semantic, Result = E(read) };
        var wide = new TypeNode.Fqn("kotlin.Any");
        var local = new BirVariable { Name = "local", Type = wide, Init = E(block) };
        var input = new BirLocal { Name = "input" };
        var frontendResult = new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { text });
        var inputOnly = new BirCallStatic { OwnerType = new TypeNode.Fqn("abi.Owner"), Method = "inputOnly",
            TypeArgs = new TypeNode[] { text }, Args = new IBirExpression[] { input }, Sig = new TypeNode[] { semantic },
            Ret = frontendResult, Sty = frontendResult };
        var nullStamp = new BirCallStatic { CalleeOwner = new TypeNode.Fqn("abi.Owner"), Method = "read",
            Args = Array.Empty<IBirExpression>(), Sty = OptionalField<TypeNode>.Null };
        var missingArguments = new BirCallStatic { Owner = new TypeNode.Fqn("abi.Owner"), Method = "read",
            Ret = semantic, Sty = semantic, Sig = suppliedSignature };
        var body = new IBirStatement[] { new BirExpressionStatement { Expr = E(bind) }, local,
            new BirExpressionStatement { Expr = E(inputOnly) }, new BirExpressionStatement { Expr = E(nullStamp) },
            new BirExpressionStatement { Expr = E(missingArguments) } };
        var method = new BirFunctionDeclaration { Name = "probe", Body = body };
        var file = new BirFileDeclaration { Methods = new[] { method } };
        Apply(file, refs);
        Need(bind.Sig.Value[0] == new TypeNode.Array(carrier) && bind.Sig.Value[1] == text
            && bind.Ret.Value is TypeNode.Fn closed && closed.Ret == carrier
            && closed.Params[0] == ownerVariable && closed.Recv == methodVariable && !bind.Sty.IsPresent,
            "selected physical positions or simultaneous caller/owner/method result closure changed");
        Need(read.Ret.Value == carrier && !read.Sty.IsPresent && local.Type.Value == wide,
            "binding the imported result must not narrow its destination");
        Need(inputOnly.Sig.Value[0] == carrier && inputOnly.Ret.Value == frontendResult && inputOnly.Sty.Value == frontendResult,
            "a physically existential input laundered an unrelated frontend result");
        Need(nullStamp.Ret.Value == carrier && nullStamp.Sty.IsNull && !nullStamp.Sig.Value.Any()
            && !nullStamp.Owner.IsPresent && !nullStamp.OwnerType.IsPresent,
            "reference ABI added absent dispatch facts or changed explicit-null stamp presence");
        Need(missingArguments.Ret.Value == semantic && missingArguments.Sty.Value == semantic
            && ReferenceEquals(missingArguments.Sig.Value, suppliedSignature),
            "missing arguments were reinterpreted as a selected zero-argument declaration");
        Need(ReferenceEquals(file.Methods.Value[0], method) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(bind.Recv.Value, receiver) && ReferenceEquals(bind.Args.Value[0], first)
            && ReferenceEquals(bind.Args.Value[1], second) && ReferenceEquals(block.Result.Value, read)
            && ReferenceEquals(inputOnly.Args.Value[0], input)
            && rows[0].ParamTypeNodes[0] == new TypeNode.Array(carrier) && rows[0].ReturnTypeNode == physicalResult,
            "source/operand ownership or authoritative reference metadata was mutated");
        Console.WriteLine("[owning referenced existential ABI] self-test OK (selected signatures, declared storage, exact frame, presence and operand identity)");
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
    }
}

sealed partial class ReferenceMetadataIndex
{
    // Test-only validated catalog input, following the existing collision/index
    // selftests. No production constructor, scan policy or visibility changes.
    internal static ReferenceMetadataIndex CreateExistentialAbiSourceTestIndex(IEnumerable<MemberBinding> members)
    {
        var metadata = new ReferenceDotKtMetadata();
        metadata.ExistentialPhysicalBySemanticOwner.Add("abi.Semantic", "abi.PhysicalCarrier");
        metadata.DotKtOwners.Add("abi.Owner");
        metadata.MemberBindings.AddRange(members);
        return new ReferenceMetadataIndex(new List<ReferenceAssembly> {
            new("existential-abi-source-test", "existential-abi-source-test", "1", metadata),
        }, Build(Array.Empty<string>())._compileRefs);
    }
}
