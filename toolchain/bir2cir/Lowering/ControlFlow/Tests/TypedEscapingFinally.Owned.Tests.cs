using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypedEscapingFinally
{
    internal static void OwnedSelfTest()
    {
        static void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Owning escaping finally: " + reason); }
        var integer = new TypeNode.Fqn("kotlin.Int");
        BirNewSuspendLambda Lambda(int result) => new() {
            Arity = 0, SuspendRet = integer, Params = Array.Empty<BirLambdaParameter>(),
            Captures = new[] { new BirCaptureDescriptor { Name = "captured", Type = integer } },
            CapValues = new IBirExpression[] { new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(result) } },
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "captured" } } },
        };
        var localLambda = Lambda(1); var returnedLambda = Lambda(2); var cleanupLambda = Lambda(3);
        var declaration = new BirVariable { Name = "callback", Type = new TypeNode.Fn(true, integer, []), Init = localLambda };
        var returned = new BirReturn { Value = returnedLambda };
        var sourceBody = new IBirStatement[] { declaration, returned };
        var handler = new BirCatch { ExcType = new TypeNode.Fqn("System.Exception"), Var = "failure",
            Body = new IBirStatement[] { new BirThrow { Value = new BirLocal { Name = "failure" } } } };
        var sourceCatches = new[] { handler };
        var cleanup = new BirExpressionStatement { Expr = cleanupLambda };
        var source = new BirTry { Body = sourceBody, Catches = sourceCatches,
            Finally = new IBirStatement[] { cleanup, new BirReturn {
                Value = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(99) } } } };
        var body = new IBirStatement[] { source };
        ApplyOwned(TypedProtectedRegions.Statements(body), new TypeNode.Fqn("kotlin.Any"), null, physicalMembers: false);
        Check(body[0] is BirBlock, "an escaping finally was not routed to a shared cleanup");
        var occurrences = new List<BirGraphValue>();
        void Visit(BirGraphValue value) { occurrences.Add(value); value.VisitChildren(Visit); }
        Visit((BirGraphValue)body[0]);
        foreach (var actual in new BirGraphValue[] { declaration, localLambda, returnedLambda, cleanupLambda, returned, handler, cleanup })
            Check(occurrences.Count(value => ReferenceEquals(value, actual)) == 1,
                "a continuing declaration/operand was cloned, dropped or attached twice");
        Check(occurrences.OfType<BirTry>().Any(region => ReferenceEquals(region.Body.OrDefault(), sourceBody)
                && ReferenceEquals(region.Catches.OrDefault(), sourceCatches))
            && returned.Value.Value is BirLocal
            && occurrences.OfType<BirSetLocal>().Any(store => ReferenceEquals(store.Value.OrDefault(), returnedLambda)),
            "the body/catch arrays or pending-return value were not moved to their actual continuing owner");
        foreach (var lambda in new[] { localLambda, returnedLambda, cleanupLambda })
            Check(lambda.Body.Value[0] is BirReturn { Value.Value: BirLocal { Name.Value: "captured" } }
                && lambda.Captures.Value[0].Name.Value == "captured", "routing crossed a nested declaration frame");
        Console.WriteLine("[owning escaping finally] self-test OK (single live operands, declarations, body/catch vectors and cleanup)");
    }
}
