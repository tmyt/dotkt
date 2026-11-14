using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CharSeqStringLowering
{
    internal static void SelfTestSource()
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owning CharSequence string lowering: " + message);
        }
        TypeNode cs = SourceType(CharSeq), text = SourceType("kotlin.String"), any = SourceType("object"),
            integer = SourceType("kotlin.Int"), unit = SourceType("kotlin.Unit"), nullableCs = new TypeNode.Nullable(cs);
        BirExpressionStatement Use(IBirExpression expression) => new() { Expr = ExpressionField(expression) };
        BirFunctionDeclaration Method(string name, IBirStatement[] body, TypeNode ret = null, BirParameter[] parameters = null)
            => new() { Name = name, Params = parameters ?? Array.Empty<BirParameter>(), Ret = ret ?? unit, Body = body };
        BirCallStatic Unknown(string name, TypeNode ret = null) => new() {
            Owner = SourceType("Referenced.Text"), Method = name, Sig = Array.Empty<TypeNode>(),
            Args = Array.Empty<IBirExpression>(), Ret = ret ?? any,
        };
        bool StringType(TypeNode type) => type is TypeNode.Fqn { Name: "kotlin.String" };
        bool NullableString(TypeNode type) => type is TypeNode.Nullable n && StringType(n.Of);

        var declarationParameter = new BirParameter { Name = "text", Type = nullableCs };
        var localRead = new BirLocal { Name = "text", Sty = nullableCs };
        var refinedRead = new BirLocal { Name = "opaque", Sty = cs };
        var shadowInitializer = new BirLocal { Name = "opaque", Sty = cs };
        var shadowedRead = new BirLocal { Name = "opaque", Sty = cs };
        var nullableRead = new BirLocal { Name = "text", Sty = nullableCs };
        var nullableAlias = new BirVariable { Name = "alias", Type = nullableCs, Init = nullableRead };
        var fieldRead = new BirFieldGet { Name = "value", OwnerType = SourceType("Owner"),
            Recv = ReadSource("owner"), Ret = nullableCs, Sty = nullableCs };
        var originalCall = Unknown("read");
        var nullableVariable = new BirVariable { Name = "snapshot", Type = nullableCs, Init = originalCall };
        var nullValue = new BirConstant { Type = nullableCs, Value = new BirLiteral.NullLiteral() };
        var localCall = new BirCallStatic {
            Method = "localText", Sig = new[] { nullableCs }, Args = new IBirExpression[] { nullValue },
            Sty = nullableCs, Ret = nullableCs, DynRet = nullableCs, MemberReturnType = nullableCs,
        };
        var externalCall = new BirCallStatic {
            Owner = SourceType("Referenced.Text"), Method = "localText", Sig = new[] { cs },
            Args = new IBirExpression[] { ReadSource("custom") }, Ret = cs,
        };
        var blockRead = new BirLocal { Name = "inside", Sty = cs };
        var block = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = "inside", Type = cs,
                Init = new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("ok") } } },
            Result = blockRead,
        };
        var outsideRead = new BirLocal { Name = "inside", Sty = cs };
        var method = Method("sourceSlots", new IBirStatement[] {
            Use(localRead), Use(refinedRead),
            new BirVariable { Name = "opaque", Type = cs, Init = shadowInitializer }, Use(shadowedRead),
            Use(fieldRead), nullableVariable, nullableAlias, Use(localCall), Use(externalCall),
            Use(block), Use(outsideRead), new BirReturn { Value = (BirConstant)nullValue.DeepClone() },
        }, nullableCs, new[] { declarationParameter,
            new BirParameter { Name = "opaque", Type = any }, new BirParameter { Name = "inside", Type = any },
            new BirParameter { Name = "custom", Type = cs }, new BirParameter { Name = "owner", Type = SourceType("Owner") } });
        method.TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new[] { cs } } };
        var field = new BirFieldDeclaration { Name = "value", Type = nullableCs };
        var type = new BirClassDefinition { Name = "Owner", Kind = "class", Fields = new[] { field } };
        var root = new BirFileDeclaration { FileClass = "CharSeqFixtureKt", Methods = new[] { method },
            Types = new IBirTypeDeclaration[] { type } };
        var parameters = method.Params.Value;
        Require(ReferenceEquals(Apply(root, new HashSet<string> { "localText" }, out var noReturns), root)
            && ReferenceEquals(root.Methods.Value[0], method) && ReferenceEquals(method.Params.Value, parameters)
            && ReferenceEquals(parameters[0], declarationParameter) && ReferenceEquals(type.Fields.Value[0], field),
            "file/declaration/parameter/field ownership must survive in place");
        Require(NullableString(declarationParameter.Type.Value) && NullableString(method.Ret.Value)
            && NullableString(field.Type.Value) && StringType(method.TypeParams.Value[0].Constraints.Value[0]),
            "declaration slots and own generic constraints must collapse together");
        Require(NullableString(localRead.Sty.Value) && refinedRead.Sty.Value == cs && NullableString(fieldRead.Sty.Value)
            && StringType(blockRead.Sty.Value) && outsideRead.Sty.Value == cs && shadowInitializer.Sty.Value == cs
            && StringType(shadowedRead.Sty.Value) && ReferenceEquals(nullableAlias.Init.Value, nullableRead)
            && NullableString(nullableRead.Sty.Value),
            "lexical String evidence, smart-cast refinement and valueBlock scope must remain distinct");
        Require(NullableString(localCall.Sig.Value[0]) && NullableString(localCall.Sty.Value)
            && NullableString(localCall.Ret.Value) && NullableString(localCall.DynRet.Value)
            && NullableString(localCall.MemberReturnType.Value) && NullableString(nullValue.Type.Value)
            && externalCall.Sig.Value[0] == cs && externalCall.Ret.Value == cs,
            "local-only signature/result synchronization must preserve external contracts and null");
        Require(noReturns.Statics.Count == 0 && noReturns.Closures.Count == 0, "ordinary calls are not delegate targets");
        var nullableSnapshot = nullableVariable.Init.Value as BirValueBlock;
        Require(nullableSnapshot != null && nullableSnapshot.Stmts.Value.Length == 1
            && nullableSnapshot.Stmts.Value[0] is BirVariable spill && ReferenceEquals(spill.Init.Value, originalCall)
            && spill.Type.Value is TypeNode.Fqn { Name: "System.Object" }
            && nullableSnapshot.Result.Value is BirConditional { Then.Value: BirConstant { Value.Value: BirLiteral.NullLiteral } } guard
            && NullableString(guard.Type.Value) && guard.Else.Value is BirCallStatic { Method.Value: "toString" },
            "a nullable unknown value must spill once into truthful Object storage and retain null");

        var receiver = Unknown("receiver", text);
        var start = Unknown("start", integer);
        var end = Unknown("end", integer);
        var slicing = new BirCallInstance { OwnerType = cs, Method = "subSequence", Recv = receiver,
            Args = new IBirExpression[] { start, end } };
        var sliced = (BirValueBlock)Apply((BirGraphValue)slicing, null);
        var receiverSpill = (BirVariable)sliced.Stmts.Value[0];
        var startSpill = (BirVariable)sliced.Stmts.Value[1];
        var substring = (BirNativeInstance)sliced.Result.Value;
        var length = (BirBinaryOperation)substring.Args.Value[1];
        Require(sliced.Stmts.Value.Length == 2 && ReferenceEquals(receiverSpill.Init.Value, receiver)
            && ReferenceEquals(startSpill.Init.Value, start) && ReferenceEquals(length.Lhs.Value, end)
            && substring.Method.Value == "Substring" && ((BirLocal)substring.Recv.Value).Name == receiverSpill.Name
            && ((BirLocal)substring.Args.Value[0]).Name == startSpill.Name
            && ((BirLocal)length.Rhs.Value).Name == startSpill.Name,
            "subSequence must evaluate receiver/start/end once in order and reuse only spilled reads");
        var secondSlice = (BirValueBlock)Apply((BirGraphValue)new BirCallInstance {
            OwnerType = cs, Method = "subSequence", Recv = Unknown("receiver", text),
            Args = new IBirExpression[] { Unknown("start", integer), Unknown("end", integer) },
        }, null);
        Require(((BirVariable)secondSlice.Stmts.Value[0]).Name.Value != receiverSpill.Name.Value,
            "subSequence counter must remain monotonic between applications");

        var exemptBody = new BirCallInstance { OwnerType = cs, Method = "length", Prop = "get",
            Recv = new BirLocal { Name = "input", Sty = cs }, Args = Array.Empty<IBirExpression>() };
        var exempt = Method("parameterTarget", new IBirStatement[] { Use(exemptBody), new BirReturn { Value = ReadSource("input") } }, cs,
            new[] { new BirParameter { Name = "input", Type = cs } });
        var returnTarget = Method("returnTarget", new IBirStatement[] { new BirReturn {
            Value = new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("ret") },
        } }, cs);
        var nullableTarget = Method("nullableReturn", new IBirStatement[] { new BirReturn {
            Value = new BirConstant { Type = nullableCs, Value = new BirLiteral.NullLiteral() },
        } }, nullableCs);
        var parameterTarget = new BirNewDelegate { Method = "parameterTarget",
            FuncType = new TypeNode.Fn(false, cs, new[] { cs }),
            DotktDelegateSlot = new TypeNode.Fqn("System.Func`2", new[] { cs, cs }) };
        var returnedDelegate = new BirNewDelegate { Method = "returnTarget",
            FuncType = new TypeNode.Fn(false, cs, Array.Empty<TypeNode>()),
            DotktDelegateSlot = new TypeNode.Fqn("System.Func`1", new[] { cs }) };
        var nullableReturn = new BirNewDelegate { Method = "nullableReturn",
            FuncType = new TypeNode.Fn(false, nullableCs, Array.Empty<TypeNode>()),
            DotktDelegateSlot = new TypeNode.Fqn("System.Func`1", new[] { nullableCs }) };
        var closure = new BirNewClosure { ClosureType = SourceType("ReturnClosure"), Method = "invoke",
            FuncType = new TypeNode.Fn(false, cs, Array.Empty<TypeNode>()), Captures = Array.Empty<IBirExpression>(),
            DotktDelegateSlot = new TypeNode.Fqn("System.Func`1", new[] { cs }) };
        var exemptType = new BirParameter { Name = "nestedContract", Type = new TypeNode.Fqn("parameterTarget", new TypeNode[] {
            new TypeNode.Fn(false, cs, Array.Empty<TypeNode>()),
        }) };
        var delegateRoot = new BirFileDeclaration { FileClass = "DelegateFixtureKt", Methods = new[] { exempt, returnTarget, nullableTarget,
            Method("delegates", new IBirStatement[] { Use(parameterTarget), Use(returnedDelegate), Use(nullableReturn), Use(closure) },
                parameters: new[] { exemptType }) },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition { Name = "ReturnClosure", Kind = "class", Generated = true,
                Methods = new[] { Method("invoke", new IBirStatement[] { new BirReturn {
                    Value = new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("closure") },
                } }, cs) } } } };
        var exemptBefore = exempt.DeepClone();
        var exemptStatement = exempt.Body.Value[0];
        Apply(delegateRoot, null, out var returnedTargets);
        Require(exempt.StructurallyEquals(exemptBefore) && ReferenceEquals(exempt.Body.Value[0], exemptStatement)
            && ReferenceEquals(((BirExpressionStatement)exempt.Body.Value[0]).Expr.Value, exemptBody),
            "a CharSequence-parameter delegate target must retain its entire original subtree");
        Require(returnedTargets.Statics.SetEquals(new[] { "parameterTarget", "returnTarget" })
            && returnedTargets.Closures.SetEquals(new[] { "ReturnClosure" }) && StringType(returnTarget.Ret.Value)
            && StringType(((BirClassDefinition)delegateRoot.Types.Value[0]).Methods.Value[0].Ret.Value)
            && parameterTarget.FuncType.Value is TypeNode.Fn { Ret: TypeNode.Fqn { Name: "kotlin.String" } }
            && ((TypeNode.Fn)((TypeNode.Fqn)exemptType.Type.Value).Args[0]).Ret == cs,
            "return-target facts must be collected before structural function-return collapse; nullable returns stay separate");

        var nested = new TypeNode.Fn(false, new TypeNode.Nullable(new TypeNode.Array(cs)), new[] { cs },
            Recv: cs, Clr: "System.Func`2", Ctx: new TypeNode[] {
                new TypeNode.FnPtr(0, 0, 1, cs, new[] { cs }),
            });
        TypeNode frozen = new TypeNode.Fn(false, cs, new[] { cs });
        TypeNode containerType = new TypeNode.Fqn("Container", new TypeNode[] { cs, nested });
        TypeNode suspendType = new TypeNode.Fn(true, cs, new[] { cs }, Ctx: Array.Empty<TypeNode>());
        var leafCall = new BirCallStatic { Owner = SourceType("Referenced.Text"), Method = "signature",
            Sig = new[] { containerType, suspendType },
            Args = new IBirExpression[] { ReadSource("holder"), ReadSource("suspended") }, Ret = unit };
        var frozenParameter = new BirParameter { Name = "callback",
            Type = new TypeNode.Fn(false, any, new[] { any }), KotlinType = frozen };
        var leafMethod = Method("typeLeaves", new[] { Use(leafCall) }, parameters: new[] {
            new BirParameter { Name = "holder", Type = containerType },
            new BirParameter { Name = "suspended", Type = suspendType }, frozenParameter,
        });
        Apply(leafMethod, null);
        var container = (TypeNode.Fqn)leafCall.Sig.Value[0];
        var loweredFn = (TypeNode.Fn)container.Args[1];
        var loweredPointer = (TypeNode.FnPtr)loweredFn.Ctx[0];
        var loweredSuspend = (TypeNode.Fn)leafCall.Sig.Value[1];
        Require(container.Args[0] == cs && loweredFn.Ret is TypeNode.Nullable { Of: TypeNode.Array a }
            && StringType(a.Elem) && loweredFn.Params[0] == cs && loweredFn.Recv == cs && loweredFn.Clr == nested.Clr
            && StringType(loweredPointer.Ret) && loweredPointer.Params[0] == cs
            && loweredPointer.CallingConvention == 0 && loweredPointer.GenericArity == 0 && loweredPointer.RequiredParams == 1
            && loweredSuspend.Suspend && StringType(loweredSuspend.Ret) && loweredSuspend.Ctx.Length == 0
            && ReferenceEquals(frozenParameter.KotlinType.Value, frozen) && ((TypeNode.Fn)frozen).Ret == cs,
            "only structural Fn/FnPtr return roles collapse, with wrappers/frame fields/frozen source facts preserved");
        var nullContext = new TypeNode.Fn(false, cs, new[] { cs });
        Require(((TypeNode.Fn)LowerFunctionReturns(nullContext)).Ctx == null
            && ((TypeNode.Fqn)LowerFunctionReturns(new TypeNode.Fqn("Empty", Array.Empty<TypeNode>()))).Args.Length == 0
            && ((TypeNode.Fqn)LowerFunctionReturns(SourceType("Bare"))).Args == null,
            "structural function/type vectors must preserve null versus empty presence");
        var payload = new BirInlinePayload { Params = new[] { new BirParameter { Name = "payload", Type = cs } },
            Ret = cs, Body = new IBirStatement[] { new BirReturn { Value = ReadSource("payload") } } };
        var payloadBefore = payload.DeepClone();
        Require(ReferenceEquals(Apply(payload, null), payload) && payload.StructurallyEquals(payloadBefore),
            "saved executable metadata must remain opaque");
        Console.WriteLine("[owning CharSequence string] self-test OK (lexical slots, null/order, delegate contracts, structural function returns, opaque facts)");
    }
}
