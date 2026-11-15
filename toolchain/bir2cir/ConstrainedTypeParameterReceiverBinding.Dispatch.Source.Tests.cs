using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    internal static void SelfTestSourceDispatch()
    {
        static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Owning constrained dispatch: " + reason); }
        var integer = new TypeNode.Fqn("kotlin.Int");
        var erased = new TypeNode.Fqn("object");
        var variable = new TypeNode.Tv("method", 0);
        var contract = new TypeNode.Fqn("Contract", new TypeNode[] { integer });
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        bool IsValue(TypeNode.Fqn type) => type.Name == "kotlin.Int";
        BirLocal Local(string name = "item") => new() { Name = name, Sty = erased };
        var lambdaBody = new IBirStatement[] { new BirReturn { Value = new BirConstant {
            Type = integer, Value = new BirLiteral.IntegerLiteral(1) } } };
        var nested = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(), Body = lambdaBody };
        var receiver = new BirCallStatic { Method = "receiver", Ret = variable, Args = new IBirExpression[] { nested } };
        var argument = new BirCallStatic { Method = "argument", Ret = erased, Args = Array.Empty<IBirExpression>() };
        var args = new IBirExpression[] { argument };
        var signature = new TypeNode[] { new TypeNode.Tv("type", 0) };
        var descriptor = new[] { new BirTypeParameter { Name = "T" } };
        var foreignSignature = SourceTypeVector.Create(signature);
        var selected = new BirCallInstance {
            OwnerType = contract, Method = "read", Recv = receiver, Args = args,
            Sig = new TypeNode[] { erased }, ResolvedMemberParams = signature,
            Ret = OptionalField<TypeNode>.Null, DynRet = integer, TypeArgs = OptionalField<TypeNode[]>.Null,
            DeclarationId = "read-id", DotktDeclarationIdentityBound = true,
            MemberVisibility = "protected", MemberOwnerTypeParams = descriptor,
            MemberMethodTypeParams = OptionalField<BirTypeParameter[]>.Null,
            MemberSignature = signature, MemberReturnType = new TypeNode.Tv("type", 0),
            Super = false, Prop = "get", PropertyName = "read", PropertyAccessor = "get",
            ShapeTypes = signature, _exactExistentialBridgeOwnerCall = false,
            _foreignDeclarationSignature = foreignSignature,
        };
        var native = new BirNativeInstance { Type = contract, Method = "native", Recv = Local(),
            Args = Array.Empty<IBirExpression>(), ResolvedMemberParams = Array.Empty<TypeNode>(),
            ResolvedMemberReturn = OptionalField<TypeNode>.Null, _foreignDeclarationSignature = foreignSignature };
        var generic = new BirGenericInstance { Type = contract, Method = "generic", Recv = Local(),
            Args = Array.Empty<IBirExpression>(), TypeArgs = new TypeNode[] { integer },
            ResolvedMemberParams = Array.Empty<TypeNode>(), _resolvedMethodTypeParams = descriptor };
        var concrete = new BirCallInstance { OwnerType = contract, Method = "concrete", Recv = Local("concrete"),
            Args = Array.Empty<IBirExpression>() };
        var ambiguous = new BirCallInstance { OwnerType = contract, Method = "ambiguous", Recv = Local("ambiguous"),
            Args = Array.Empty<IBirExpression>() };
        var open = new BirCallInstance { OwnerType = new TypeNode.Fqn("Open"), Method = "open", Recv = Local(),
            Args = Array.Empty<IBirExpression>() };
        var captureCall = new BirCallInstance { OwnerType = contract, Method = "capture", Recv = Local(), Args = Array.Empty<IBirExpression>() };
        var donorCall = new BirCallInstance { OwnerType = contract, Method = "donor", Recv = Local(), Args = Array.Empty<IBirExpression>() };
        var suspend = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(),
            CapValues = new IBirExpression[] { captureCall },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = donorCall } } };
        var expressions = new IBirExpression[] { selected, native, generic, concrete, ambiguous, open, suspend };
        var body = expressions.Select(value => (IBirStatement)new BirExpressionStatement {
            Expr = OptionalField<IBirExpression>.Of(value),
        }).Append(new BirVariable { Name = "ambiguous", Type = new TypeNode.Fqn("kotlin.String") }).ToArray();
        var method = new BirFunctionDeclaration { Name = "use", Params = new[] {
            new BirParameter { Name = "item", Type = variable }, new BirParameter { Name = "concrete", Type = integer },
            new BirParameter { Name = "ambiguous", Type = variable },
        }, TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { contract } } }, Body = body };
        var file = new BirFileDeclaration { FileClass = "Dispatch", Methods = new[] { method }, Types = new IBirTypeDeclaration[] {
            new BirInterfaceDefinition { Name = "Contract", TypeParams = descriptor },
            new BirInterfaceDefinition { Name = "Open", TypeParams = descriptor },
        } };
        ApplyAll(new[] { file }, IsValue, refs);
        var result = (BirConstrainedCall)((BirExpressionStatement)body[0]).Expr.Value;
        Require(ReferenceEquals(result.Recv.Value, receiver) && ReferenceEquals(result.Args.Value, args)
            && result.Args.Value[0] is BirCast { E.Value: var movedArgument, Type.Value: var target }
            && ReferenceEquals(movedArgument, argument) && target == integer
            && ReferenceEquals(receiver.Args.Value[0], nested) && ReferenceEquals(nested.Body.Value, lambdaBody),
            "receiver/argument identity, once evaluation or nested callable ownership changed");
        Require(result.RecvType.Value == variable && result.Iface.Value == contract && result.Ret.Value == integer
            && result.TypeArgs.IsNull && ReferenceEquals(result.Sig.Value, signature)
            && ReferenceEquals(result.ResolvedMemberParams.Value, signature)
            && result.DeclarationId.Value == "read-id" && result.DotktDeclarationIdentityBound.Value
            && result.MemberVisibility.Value == "protected" && ReferenceEquals(result.MemberOwnerTypeParams.Value, descriptor)
            && result.MemberMethodTypeParams.IsNull && ReferenceEquals(result.MemberSignature.Value, signature)
            && result.MemberReturnType.Value == new TypeNode.Tv("type", 0)
            && !result.Super.Value && result.Prop.Value == "get" && result.PropertyName.Value == "read"
            && result.PropertyAccessor.Value == "get" && ReferenceEquals(result.ShapeTypes.Value, signature)
            && result._exactExistentialBridgeOwnerCall.IsPresent && !result._exactExistentialBridgeOwnerCall.Value
            && ReferenceEquals(result._foreignDeclarationSignature.Value, foreignSignature),
            "late visibility/property/exact declaration facts, signature precedence or null presence changed");
        Require(((BirExpressionStatement)body[1]).Expr.Value is BirConstrainedCall nativeResult
            && nativeResult.ResolvedMemberReturn.IsNull && ReferenceEquals(nativeResult._foreignDeclarationSignature.Value, foreignSignature)
            && ((BirExpressionStatement)body[2]).Expr.Value is BirConstrainedCall genericResult
            && ReferenceEquals(genericResult._resolvedMethodTypeParams.Value, descriptor)
            && ReferenceEquals(((BirExpressionStatement)body[3]).Expr.Value, concrete)
            && ReferenceEquals(((BirExpressionStatement)body[4]).Expr.Value, ambiguous)
            && ReferenceEquals(((BirExpressionStatement)body[5]).Expr.Value, open)
            && suspend.CapValues.Value[0] is BirConstrainedCall && ReferenceEquals(((BirExpressionStatement)suspend.Body.Value[0]).Expr.Value, donorCall)
            && ReferenceEquals(method.Body.Value, body), "actual call families, physical locals, open arity or donor scope changed");
        ErasedDispatchSourceSelfTest(refs);
        CurrentOwnerDispatchSourceSelfTest(refs);
        Console.WriteLine("[owning constrained dispatch] self-test OK (retained/removed/allocated/foreign proofs, late facts, frames and actual operands)");
    }

    static void CurrentOwnerDispatchSourceSelfTest(ReferenceMetadataIndex refs)
    {
        var variable = new TypeNode.Tv("type", 1);
        var contract = new TypeNode.Fqn("OwnerContract");
        var recv = new BirLocal { Name = "item", Sty = new TypeNode.Fqn("object") };
        var ctorCall = new BirNativeInstance { Type = contract, Method = "read", Recv = recv,
            Args = Array.Empty<IBirExpression>(), Ret = OptionalField<TypeNode>.Null };
        var methodCall = new BirCallInstance { OwnerType = contract, Method = "read", Recv = new BirLocal { Name = "item" },
            Args = Array.Empty<IBirExpression>() };
        var parameters = new[] { new BirParameter { Name = "item", Type = variable } };
        var ctor = new BirConstructor { Params = parameters,
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = ctorCall } } };
        var method = new BirFunctionDeclaration { Name = "read", Params = parameters,
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = methodCall } } };
        var captured = new[] { new BirTypeParameter { Name = "Outer", Constraints = new TypeNode[] { contract } } };
        var own = new[] { new BirTypeParameter { Name = "Inner", Constraints = new TypeNode[] { new TypeNode.Tv("type", 0) } } };
        var owner = new BirClassDefinition { Name = "PlacedOwner", CapturedTypeParams = captured, TypeParams = own,
            Ctors = new[] { ctor }, Methods = new[] { method } };
        ApplyAll(new[] { new BirFileDeclaration { FileClass = "OwnerFile", Types = new IBirTypeDeclaration[] { owner } } }, _ => false, refs);
        if (((BirExpressionStatement)ctor.Body.Value[0]).Expr.Value is not BirConstrainedCall constructorResult
            || constructorResult.RecvType.Value != variable || constructorResult.Iface.Value != contract
            || !constructorResult.Ret.IsNull || !ReferenceEquals(constructorResult.Recv.Value, recv)
            || ((BirExpressionStatement)method.Body.Value[0]).Expr.Value is not BirConstrainedCall methodResult
            || methodResult.RecvType.Value != variable || methodResult.Iface.Value != contract
            || !ReferenceEquals(owner.CapturedTypeParams.Value, captured) || !ReferenceEquals(owner.TypeParams.Value, own)
            || !ReferenceEquals(ctor.Params.Value, parameters) || !ReferenceEquals(method.Params.Value, parameters))
            throw new InvalidOperationException("Owning constrained dispatch lost the current captured/own transitive physical frame");
    }

    static void ErasedDispatchSourceSelfTest(ReferenceMetadataIndex refs)
    {
        static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Owning erased dispatch: " + reason); }
        var variable = new TypeNode.Tv("method", 0);
        var contract = new TypeNode.Fqn("Contract");
        var carrier = new TypeNode.Fqn("Carrier");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var foreign = new TypeNode.Fqn("System.Collections.Generic.IList", new TypeNode[] {
            new TypeNode.Projection("in", integer) });
        BirLocal Receiver() => new() { Name = "item", Sty = variable };
        var erasedReceiver = Receiver();
        var erased = new BirCallInstance { OwnerType = contract, Method = "read", Recv = erasedReceiver, Args = Array.Empty<IBirExpression>() };
        var constrainedReceiver = Receiver();
        var constrained = new BirConstrainedCall { Iface = contract, RecvType = variable, Method = "already",
            Recv = constrainedReceiver, Args = Array.Empty<IBirExpression>(), Ret = OptionalField<TypeNode>.Null };
        var property = new BirPropertyGet { Type = contract, Name = "value", Recv = Receiver() };
        var fieldReceiver = Receiver();
        var field = new BirFieldGet { OwnerType = contract, Name = "value", Recv = fieldReceiver };
        var eventReceiver = Receiver();
        var eventCall = new BirEventAdd { Type = contract, AccessorOwner = contract, Dispatch = "constrained", Recv = eventReceiver };
        var foreignReceiver = Receiver();
        var foreignCall = new BirCallInstance { OwnerType = foreign, Method = "Add", Recv = foreignReceiver,
            Args = Array.Empty<IBirExpression>(), Prop = OptionalField<string>.Null };
        var body = new IBirStatement[] { new BirExpressionStatement { Expr = erased }, new BirExpressionStatement { Expr = constrained },
            new BirExpressionStatement { Expr = property }, new BirExpressionStatement { Expr = field },
            new BirExpressionStatement { Expr = eventCall }, new BirExpressionStatement { Expr = foreignCall } };
        var method = new BirFunctionDeclaration { Name = "erased", Params = new[] { new BirParameter { Name = "item", Type = variable } },
            TypeParams = new[] { new BirTypeParameter { Name = "T", ErasedInnerConstraints = new TypeNode[] { contract, foreign } } }, Body = body };
        var file = new BirFileDeclaration { FileClass = "Erased", Methods = new[] { method } };
        ApplyAll(new[] { file }, _ => false, refs);
        Require(ReferenceEquals(((BirExpressionStatement)body[0]).Expr.Value, erased)
            && erased.Recv.Value is BirCast { E.Value: var moved } && ReferenceEquals(moved, erasedReceiver)
            && ((BirExpressionStatement)body[1]).Expr.Value is BirNativeInstance restored
            && restored.Recv.Value is BirCast { E.Value: var restoredReceiver }
            && ReferenceEquals(restoredReceiver, constrainedReceiver) && restored.Ret.IsNull
            && property._erasedConstraintDispatch.Value
            && field.Recv.Value is BirCast { E.Value: var movedField } && ReferenceEquals(movedField, fieldReceiver)
            && eventCall.Recv.Value is BirCast { E.Value: var movedEvent } && ReferenceEquals(movedEvent, eventReceiver)
            && eventCall.Dispatch.Value == "callvirt"
            && ((BirExpressionStatement)body[5]).Expr.Value is BirNativeInstance projected
            && TypeNodeDataEquality.Equal(projected.Type.Value, foreign) && projected.Prop.IsNull
            && ReferenceEquals(projected.Recv.Value, foreignReceiver), "erased casts/property/event/foreign dispatch or operand ownership changed");

        // The allocated carrier is selected only when the SAME current frame
        // actually declares that physical obligation; a map alone is not proof.
        var receiver = Receiver();
        var allocated = new BirConstrainedCall { Iface = contract, RecvType = variable, Method = "allocated",
            Recv = receiver, Args = Array.Empty<IBirExpression>(), TypeArgs = OptionalField<TypeNode[]>.Null };
        method.TypeParams.Value[0].Constraints = new TypeNode[] { carrier };
        method.Body = new IBirStatement[] { new BirExpressionStatement { Expr = allocated } };
        ApplyAll(new[] { file }, _ => false, refs, new Dictionary<string, string> { ["Contract"] = "Carrier" });
        Require(((BirExpressionStatement)method.Body.Value[0]).Expr.Value is BirCallInstance physical
            && physical.OwnerType.Value == carrier && physical.Virtual.Value && physical.TypeArgs.IsNull
            && physical.Recv.Value is BirCast { E.Value: var original } && ReferenceEquals(original, receiver),
            "allocated carrier proof did not preserve its selected owner, presence or moved receiver");
    }
}
