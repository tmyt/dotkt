using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinRuntimeTypeWitness
{
    internal static void SourceSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("System.String");
        BirLocal Predicate() => new() { Name = "predicate", Sty = PredicateType };
        BirClassReference Classifier() => new() { Type = text, KotlinClassifierRef = true };
        BirCallStatic Effect(string name) => new() { Method = name, Ret = NullableAny, Args = Array.Empty<IBirExpression>() };
        var inner = new BirIsInstance { Type = text, E = Effect("nestedEffect"), KotlinTypeWitness = KotlinTypeWitness.ConstantValue(0),
            KotlinRuntimeTypeWitness = Classifier(), KotlinClassifierPredicate = Predicate() };
        var cast = new BirCast { Type = new TypeNode.Array(text), E = inner, KotlinTypeWitness = KotlinTypeWitness.ConstantValue(0),
            KotlinRuntimeTypeWitness = Classifier(), KotlinClassifierPredicate = Predicate(), ReifiedTypeOperand = true };
        var safe = new BirInstanceOrNull { Type = text, E = Effect("safeEffect"), KotlinTypeWitness = KotlinTypeWitness.ConstantValue(0),
            KotlinRuntimeTypeWitness = Classifier(), KotlinClassifierPredicate = Predicate() };
        var body = new IBirStatement[] { new BirReturn { Value = cast }, new BirExpressionStatement { Expr = safe } };
        var method = new BirFunctionDeclaration { Name = "probe", Params = new[] {
            new BirParameter { Name = "dotkt$runtimeClassifier$value$0", Type = NullableAny } }, Body = body };
        var methods = new[] { method };
        var root = new BirFileDeclaration { Methods = methods };
        LowerOperations(root, refs);
        var outer = (BirValueBlock)((BirReturn)body[0]).Value.Value;
        var first = (BirVariable)outer.Stmts.Value[0];
        var nested = (BirValueBlock)first.Init.Value;
        var nestedVariable = (BirVariable)nested.Stmts.Value[0];
        var safeBlock = (BirValueBlock)((BirExpressionStatement)body[1]).Expr.Value;
        Check(ReferenceEquals(root.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
            && nestedVariable.Name.Value == "dotkt$runtimeClassifier$value$1" && first.Name.Value == "dotkt$runtimeClassifier$value$2"
            && ((BirVariable)safeBlock.Stmts.Value[0]).Name.Value == "dotkt$runtimeClassifier$value$3"
            && outer.Stmts.Value[1] is BirIf && outer.Result.Value is BirCast { KotlinRuntimeTypeWitness.IsPresent: false }
            && safeBlock.Result.Value is BirConditional { Type.Value: TypeNode.Fqn { Name: "object" } },
            "postorder lowering must preserve declarations, avoid names and keep checked/safe result shapes");
        var nodes = new List<BirGraphValue>(); root.Visit(nodes.Add);
        Check(nodes.OfType<BirCallStatic>().Count(node => node.Method.OrDefault() == "nestedEffect") == 1
            && nodes.OfType<BirCallStatic>().Count(node => node.Method.OrDefault() == "safeEffect") == 1
            && nodes.OfType<BirIsInstance>().Any(node => node.Type.OrDefault() is TypeNode.Array)
            && nodes.OfType<BirDelegateInvoke>().Any()
            && nodes.Where(ClassifierOperation.Admits).All(node => !new ClassifierOperation(node).Classifier.IsPresent
                && !new ClassifierOperation(node).Predicate.IsPresent),
            "operands must evaluate once and predicate casts must retain a separate physical array test without witness re-entry");
        var calls = nodes.OfType<BirNativeInstance>().ToArray();
        Check(calls.Length != 0 && calls.All(call => call.Dispatch.Value == "callvirt" && call.MemberRef.HasValue
                && call.MemberRef.Value.Name.Value == call.Method.Value)
            && calls.Any(call => call.Method.Value == "get_IsGenericTypeDefinition")
            && calls.Any(call => call.Method.Value == "GetType") && calls.Any(call => call.Method.Value == "IsInstanceOfType"),
            "runtime classifier operations must carry exact native operands, not leave an emitter overload search");
        var predicates = nodes.OfType<BirLocal>().Where(node => node.Name.OrDefault() == "predicate").ToArray();
        Check(predicates.Length > 1 && predicates.Distinct(ReferenceEqualityComparer.Instance).Count() == predicates.Length,
            "each copied predicate occurrence must have independent ownership");
        Console.WriteLine("[typed runtime classifiers] self-test OK (postorder, once evaluation, native refs, predicate/physical guards, copies)");
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Typed runtime classifier: " + message); }
}
