using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StdlibSubstituteTypeParams
{
    internal static void SourceSelfTest()
    {
        static void Need(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning stdlib parameters: " + message); }
        var bound = new TypeNode.Fqn("kotlin.Comparable", new TypeNode[] { new TypeNode.Tv("type", 0) });
        var methodBound = new TypeNode.Fqn("kotlin.Comparable", new TypeNode[] { new TypeNode.Tv("method", 0) });
        BirTypeParameter Parameter(string name, TypeNode constraint) => new() {
            Name = name, Reified = true, Variance = "in", Constraints = new[] { constraint },
            ErasedInnerConstraints = new TypeNode[] { new TypeNode.Tv("type", 0) }, SpecialConstraints = new[] { "class" },
        };
        var captured = Parameter("Captured", bound);
        var own = Parameter("Own", bound);
        var classParameter = Parameter("Class", bound);
        var methodParameter = Parameter("Method", methodBound);
        var lambdaParameter = Parameter("Lambda", methodBound);
        var lambda = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(),
            TypeParams = new[] { lambdaParameter }, Body = Array.Empty<IBirStatement>() };
        var method = new BirFunctionDeclaration { Name = "compare", TypeParams = new[] { methodParameter },
            Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirExpressionStatement { Expr = lambda } } };
        var contract = new BirInterfaceDefinition { Name = "Contract", Kind = "interface", CapturedTypeParams = new[] { captured },
            TypeParams = new[] { own }, Methods = new[] { method } };
        var concrete = new BirClassDefinition { Name = "Range", Kind = "class", TypeParams = new[] { classParameter } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { contract, concrete } };
        var parameters = contract.TypeParams.Value; var methods = contract.Methods.Value;
        var classConstraints = classParameter.Constraints.Value;
        var methodConstraints = methodParameter.Constraints.Value;
        var lambdaConstraints = lambdaParameter.Constraints.Value;
        var inner = own.ErasedInnerConstraints.Value; var specials = own.SpecialConstraints.Value;
        Apply(file);
        Need(!own.Constraints.IsPresent && !own.Variance.IsPresent && own.Reified.Value
            && ReferenceEquals(own.ErasedInnerConstraints.Value, inner) && ReferenceEquals(own.SpecialConstraints.Value, specials),
            "runtime interface rewriting changed unrelated declaration facts");
        Need(ReferenceEquals(classParameter.Constraints.Value, classConstraints)
            && ReferenceEquals(methodParameter.Constraints.Value, methodConstraints)
            && ReferenceEquals(lambdaParameter.Constraints.Value, lambdaConstraints)
            && !classParameter.Variance.IsPresent && !methodParameter.Variance.IsPresent && !lambdaParameter.Variance.IsPresent,
            "runtime substitution removed class/method/lambda bounds required for physical calls");
        Need(captured.Variance.Value == "in" && captured.Constraints.Value.Single() == bound
            && ReferenceEquals(contract.TypeParams.Value, parameters) && ReferenceEquals(parameters[0], own)
            && ReferenceEquals(contract.Methods.Value, methods) && ReferenceEquals(method.TypeParams.Value[0], methodParameter)
            && ReferenceEquals(lambda.TypeParams.Value[0], lambdaParameter),
            "captured prefix, parameter vectors or declaration identity changed");
        Console.WriteLine("[owning runtime generic bounds] self-test OK (same planner, interface-only own bounds, class/method proof retention, captured prefix and actual records)");
    }
}
