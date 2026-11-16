using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    static BirClassDefinition BuildRootContinuationSource(string tcsBcl)
    {
        var variable = new TypeNode.Tv("type", 0);
        var root = new TypeNode.Fqn(RootContinuationFqn, new TypeNode[] { variable });
        var tcs = new TypeNode.Fqn(tcsBcl, new TypeNode[] { variable });
        var result = new TypeNode.Fqn("kotlin.Result", new TypeNode[] { variable });
        var throwable = new TypeNode.Fqn("kotlin.Throwable");
        var nullableThrowable = new TypeNode.Nullable(throwable);
        const string fieldName = "$tcs", exceptionName = "$exception";
        const int failure = 9101, kotlinCancellation = 9102, fault = 9103, done = 9104;
        BirLocal Local(string name) => new() { Name = name };
        BirFieldGet Sink() => new() { OwnerType = root, Recv = new BirThis(), Name = fieldName };
        BirNativeInstance SinkCall(string method, TypeNode[] parameters, params IBirExpression[] arguments) => new() {
            Type = tcs, Method = method, Recv = Sink(), ArgTypes = parameters, Args = arguments, Ret = BoolTn };
        BirExpressionStatement Expr(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        BirBranchIf Branch(IBirExpression condition, int label) => new() {
            Id = label, On = false, Cond = OptionalField<IBirExpression>.Of(condition) };
        var cancellation = new TypeNode.Fqn("System.OperationCanceledException");
        var token = new TypeNode.Fqn("System.Threading.CancellationToken");
        var resumeBody = new IBirStatement[] {
            new BirVariable { Name = exceptionName, Type = nullableThrowable,
                Init = new BirCallInstance { OwnerType = result, Virtual = false, Recv = Local("result"),
                    Method = "exceptionOrNull", Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = nullableThrowable } },
            new BirBlock { Body = new IBirStatement[] {
                Branch(new BirBinaryOperation { Op = "===", Lhs = Local(exceptionName),
                    Rhs = new BirConstant { Type = nullableThrowable, Value = new BirLiteral.NullLiteral() } }, failure),
                Expr(SinkCall("TrySetResult", new TypeNode[] { variable }, new BirCast { Type = variable,
                    E = new BirCallInstance { OwnerType = result, Virtual = false, Recv = Local("result"),
                        Method = "value", Prop = "get", Args = Array.Empty<IBirExpression>(), Ret = AnyTn } })),
                new BirGoto { Id = done }, new BirLabel { Id = failure },
                Branch(new BirIsInstance { Type = cancellation, E = Local(exceptionName) }, kotlinCancellation),
                Expr(SinkCall("TrySetCanceled", new TypeNode[] { token }, new BirPropertyGet {
                    Type = cancellation, Name = "CancellationToken", Static = false,
                    Recv = new BirCast { Type = cancellation, E = Local(exceptionName) }, Ret = token })),
                new BirGoto { Id = done }, new BirLabel { Id = kotlinCancellation },
                Branch(new BirIsInstance { Type = new TypeNode.Fqn("kotlin.coroutines.cancellation.CancellationException"),
                    E = Local(exceptionName) }, fault),
                Expr(SinkCall("TrySetCanceled", Array.Empty<TypeNode>())),
                new BirGoto { Id = done }, new BirLabel { Id = fault },
                Expr(SinkCall("TrySetException", new TypeNode[] { throwable }, new BirCast { Type = throwable, E = Local(exceptionName) })),
                new BirLabel { Id = done },
            } },
        };
        return new BirClassDefinition {
            Name = RootContinuationFqn, Kind = "class", Abstract = false, Vis = "internal",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Base = OptionalField<TypeNode>.Null,
            Interfaces = new TypeNode[] { new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { variable }) },
            Fields = new[] { new BirFieldDeclaration { Name = fieldName, Type = tcs, Vis = "private", Attrs = Array.Empty<BirAttribute>() } },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "tcs", Type = tcs } },
                BaseArgs = OptionalField<IBirExpression[]>.Null, ThisArgs = OptionalField<IBirExpression[]>.Null, Vis = "internal",
                Body = new IBirStatement[] { new BirSetField { OwnerType = root, Recv = new BirThis(), Name = fieldName, Value = Local("tcs") } } } },
            Methods = new[] {
                new BirFunctionDeclaration { Name = "resumeWith", Static = false, Override = false, Virtual = true,
                    Abstract = false, ObjectOverride = false, Vis = "public",
                    Params = new[] { new BirParameter { Name = "result", Type = result } },
                    Ret = VoidTn, Body = resumeBody, Attrs = Array.Empty<BirAttribute>() },
                new BirFunctionDeclaration { Name = "context", PropertyName = "context", PropertyAccessor = "get", PropertyAssociation = "continuation-context",
                    Static = false, Override = false, Virtual = true, Abstract = false, ObjectOverride = false, Vis = "public",
                    Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn(CoroutineContextFqn),
                    Body = new IBirStatement[] { new BirReturn { Value = new BirStaticFieldGet {
                        OwnerType = new TypeNode.Fqn("kotlin.coroutines.EmptyCoroutineContext"), Name = "INSTANCE" } } },
                    Attrs = Array.Empty<BirAttribute>() },
            },
            Properties = new[] { new BirPropertyDeclaration { Name = "context", Type = new TypeNode.Fqn(CoroutineContextFqn),
                KotlinAccessors = new[] { "get" }, PropertyAssociation = "continuation-context" } },
            Attrs = Array.Empty<BirAttribute>(),
        };
    }
}
