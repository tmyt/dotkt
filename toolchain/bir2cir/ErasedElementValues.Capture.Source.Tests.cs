using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ErasedElementValues
{
    internal static void CaptureSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning late erased captures: " + message); }
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var variable = new TypeNode.Tv("type", 0);
        var unit = new TypeNode.Fqn("kotlin.Unit");
        BirExpressionStatement Statement(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        var nested = new BirFunctionDeclaration { Name = "nested", DeclarationId = "nested", Params = Array.Empty<BirParameter>(),
            Ret = unit, Body = Array.Empty<IBirStatement>() };
        var donor = new BirNewSuspendLambda { TypeFrame = "dense", Captures = Array.Empty<BirCaptureDescriptor>(),
            Params = Array.Empty<BirLambdaParameter>(), TypeParams = new[] { new BirTypeParameter { Name = "Independent" } },
            Body = Array.Empty<IBirStatement>() };
        var read = new BirFieldGet { Name = "value", OwnerType = new TypeNode.Fqn("Storage"), Recv = new BirThis(), Sty = variable };
        var local = new BirVariable { Name = "alias", Type = variable, Init = read };
        var operand = new BirValueBlock { Type = variable, Stmts = new IBirStatement[] {
            new BirLocalFunction { Id = "nested", Decl = nested }, Statement(donor),
        }, Result = new BirLocal { Name = "alias", Sty = variable } };
        var closure = new BirNewClosure { ClosureType = new TypeNode.Fqn("LaterStorage"), Captures = new IBirExpression[] { operand } };
        var receiverDeclaration = new BirFunctionDeclaration { Name = "receiverLocal", Params = Array.Empty<BirParameter>(), Ret = unit,
            Body = Array.Empty<IBirStatement>() };
        var receiver = new BirValueBlock { Type = variable, Stmts = new IBirStatement[] {
            new BirLocalFunction { Id = "receiverLocal", Decl = receiverDeclaration },
        }, Result = new BirLocal { Name = "alias", Sty = variable } };
        var constrained = new BirConstrainedCall { Iface = new TypeNode.Fqn("Contract"), RecvType = variable,
            Recv = receiver, Method = "use", Args = Array.Empty<IBirExpression>(), Ret = unit,
            DeclarationId = "chosen", DotktDeclarationIdentityBound = true };
        var guard = new BirCast { Type = variable, Sty = variable, ReifiedTypeOperand = false,
            E = new BirLocal { Name = "alias", Sty = variable } };
        var arrayRead = new BirArrayGet { Elem = variable, Array = new BirLocal { Name = "array", Sty = new TypeNode.Array(variable) },
            Index = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) } };
        var method = new BirFunctionDeclaration { Name = "body", Params = Array.Empty<BirParameter>(), Ret = unit,
            Body = new IBirStatement[] { local, Statement(closure), Statement(constrained), Statement(guard), Statement(arrayRead) } };
        var field = new BirFieldDeclaration { Name = "value", Type = ObjectType, KotlinType = variable };
        var owner = new BirClassDefinition { Name = "Storage", Kind = "class", TypeParams = new[] {
            new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { new TypeNode.Fqn("Bound") } },
        }, Fields = new[] { field }, Methods = new[] { method } };
        var root = new BirFileDeclaration { FileClass = "CaptureBodies", Types = new IBirTypeDeclaration[] { owner } };
        var bindings = new CurrentDeclarationBindings(new[] { root });
        var originalNested = bindings.GetSource(nested);
        var originalDonor = bindings.GetSource(donor);
        var originalReceiver = bindings.GetSource(receiverDeclaration);
        var definitions = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal) { ["Storage"] = owner };
        var frames = new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal);
        var methods = owner.Methods.Value;
        var body = method.Body.Value;
        ApplyType(owner, refs, definitions, frames, _ => false, null);
        Require(ReferenceEquals(owner.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
            && local.Type.Value == ObjectType && read.Sty.Value == ObjectType
            && closure.Captures.Value[0] is BirCast captureCast && ReferenceEquals(captureCast.E.Value, operand)
            && ((BirLocal)operand.Result.Value).Sty.Value == ObjectType
            && method.Body.Value[2] is BirExpressionStatement { Expr.Value: BirCallInstance call }
            && call.DeclarationId.Value == "chosen" && call.DotktDeclarationIdentityBound.Value
            && call.Recv.Value is BirCast receiverCast && ReferenceEquals(receiverCast.E.Value, receiver)
            && guard.Type.Value == new TypeNode.Nullable(new TypeNode.Fqn("Bound"))
            && arrayRead.Elem.Value == variable && !read.ErasedValueVariableFact.IsPresent,
            "late storage repair replayed array mode, cloned an operand, lost selected calls or changed origin ownership");
        var physical = root.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        Require(originalNested.IsPhysical && originalDonor.IsPhysical && originalReceiver.IsPhysical
            && ReferenceEquals(bindings.GetPhysical(originalNested.Physical), originalNested)
            && ReferenceEquals(bindings.GetPhysical(originalDonor.Physical), originalDonor)
            && physical["types"][0]["methods"][0]["body"].AsArray().Count == body.Length,
            "late wrappers did not preserve exact registered nested declarations through one frontier");

        var suspendRead = new BirLocal { Name = "captured", Sty = variable };
        var suspendVariable = new BirVariable { Name = "copy", Type = variable, Init = suspendRead };
        var suspendGuard = new BirCast { Type = variable, ReifiedTypeOperand = false, E = new BirLocal { Name = "copy", Sty = variable } };
        var lambda = new BirNewSuspendLambda { Captures = new[] { new BirCaptureDescriptor { Name = "captured", Type = ObjectType, KotlinType = variable } },
            Params = new[] { new BirLambdaParameter { Name = "captured", Type = variable } },
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { new TypeNode.Fqn("SuspendBound") } } },
            Body = new IBirStatement[] { suspendVariable, Statement(suspendGuard) }, CapValues = OptionalField<IBirExpression[]>.Null };
        var captures = lambda.Captures.Value;
        var lambdaBody = lambda.Body.Value;
        ApplySuspendBody(lambda, refs, definitions, frames, _ => false, null);
        Require(ReferenceEquals(lambda.Captures.Value, captures) && ReferenceEquals(lambda.Body.Value, lambdaBody)
            && lambda.Body.Value.Length == 2 && lambda.CapValues.IsNull && suspendVariable.Type.Value == ObjectType
            && suspendRead.Sty.Value == ObjectType && suspendGuard.Type.Value == new TypeNode.Nullable(new TypeNode.Fqn("SuspendBound")),
            "suspend body repair lost its actual own body, capture precedence, declared bounds or optional vectors");
        Console.WriteLine("[owning late erased captures] self-test OK (storage-only mode, explicit body views, selected constraints, operand and frontier identity)");
    }
}
