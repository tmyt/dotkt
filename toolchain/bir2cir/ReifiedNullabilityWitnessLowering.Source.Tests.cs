using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReifiedNullabilityWitnessLowering
{
    internal static void SourceSelfTest()
    {
        SourceMethodWitnessSelfTest();
        SourceGeneratedDelegateSelfTest();
        SourceCaptureWitnessSelfTest();
        SourceSuspendWitnessSelfTest();
        SourceRepresentationWitnessSelfTest();
    }

    static BirConstant SourceNull() => new() {
        Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), Value = new BirLiteral.NullLiteral(),
    };
    static BirTypeParameter SourceParameter(string name) => new() { Name = name, Reified = true };
    static BirIsInstance SourceTest(TypeNode type) => new() { Type = type, E = SourceNull() };
    static IBirStatement SourceReturn(IBirExpression value) => new BirReturn { Value = OptionalField<IBirExpression>.Of(value) };
    static BirFunctionDeclaration SourceMethod(string name, string id, BirTypeParameter[] parameters, params IBirStatement[] body) => new() {
        Name = name, DeclarationId = id, Static = true, TypeParams = parameters, Params = Array.Empty<BirParameter>(),
        Ret = new TypeNode.Fqn("kotlin.Boolean"), Body = body,
    };
    static BirFileDeclaration SourceFile(params BirFunctionDeclaration[] methods) => new() {
        FileClass = "WitnessProbe", Types = Array.Empty<IBirTypeDeclaration>(), Methods = methods,
    };
    static void SourceRun(BirFileDeclaration file) => Apply(file, NullableWitnessDemand.Collect(new[] { file }, null), null, null, null);
    static void SourceCheck(bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException("typed nullable-witness self-test: " + context);
    }

    static void SourceMethodWitnessSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var test = SourceTest(variable);
        var declared = new BirParameter { Name = Prefix + "0", Type = new TypeNode.Fqn("kotlin.Int") };
        var method = SourceMethod("test", "witness:test", new[] { SourceParameter("T") }, SourceReturn(test));
        method.Params = new[] { declared };
        var call = new BirCallStatic {
            Owner = new TypeNode.Fqn("WitnessProbe"), Method = "test", DeclarationId = "witness:test",
            TypeArgs = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String")) },
            Args = new IBirExpression[] { KotlinTypeWitness.ConstantValue(0) }, Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Int") },
            Ret = new TypeNode.Fqn("kotlin.Boolean"),
        };
        var caller = SourceMethod("caller", "witness:caller", Array.Empty<BirTypeParameter>(), SourceReturn(call));
        var file = SourceFile(method, caller);
        SourceRun(file);
        SourceCheck(ReferenceEquals(file.Methods.Value[0], method) && ReferenceEquals(method.Params.Value[0], declared)
            && ReferenceEquals(((BirReturn)method.Body.Value[0]).Value.Value, test), "continuing declaration/parameter/body identity");
        SourceCheck(method.Params.Value.Length == 2 && method.Params.Value[1].Name.Value == Prefix + "0$"
            && method.SemanticReifiedIndices.Value.SequenceEqual(new[] { 0 }) && method.NullableWitnessIndices.Value.SequenceEqual(new[] { 0 })
            && !method.TypeParams.Value[0].Reified.IsPresent, "hidden parameter collision and declaration metadata");
        var flags = (BirFieldGet)test.KotlinTypeWitness.Value;
        var classifier = (BirFieldGet)test.KotlinRuntimeTypeWitness.Value;
        var predicate = (BirFieldGet)test.KotlinClassifierPredicate.Value;
        SourceCheck(!ReferenceEquals(flags.Recv.Value, classifier.Recv.Value) && !ReferenceEquals(classifier.Recv.Value, predicate.Recv.Value)
            && flags.Type.HasValue && flags.Sty.HasValue && test.NullWitness.HasValue, "independent witness operands and exact field facts");
        SourceCheck(call.NullablePendingWitnesses.Value is [BirNew nullableKnown]
            && nullableKnown.Args.Value[0] is BirBinaryOperation { Op.Value: "|",
                Lhs.Value: BirFieldGet { Name.Value: "Item1", Recv.Value: BirNew flagsKnown },
                Rhs.Value: BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 1 } } }
            && nullableKnown.Args.Value[1] is BirFieldGet { Name.Value: "Item2", Recv.Value: BirNew classifierKnown }
            && nullableKnown.Args.Value[2] is BirFieldGet { Name.Value: "Item3", Recv.Value: BirNew predicateKnown }
            && flagsKnown.Args.Value is [BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 0 } },
                BirClassReference { Type.Value: TypeNode.Fqn { Name: "kotlin.String" }, KotlinClassifierRef.Value: true },
                BirConstant { Value.Value: BirLiteral.NullLiteral }]
            && flagsKnown.StructurallyEquals(classifierKnown) && flagsKnown.StructurallyEquals(predicateKnown)
            && !ReferenceEquals(flagsKnown, classifierKnown) && !ReferenceEquals(flagsKnown, predicateKnown)
            && !ReferenceEquals(classifierKnown, predicateKnown)
            && !ReferenceEquals(flagsKnown.Args.Value[1], classifierKnown.Args.Value[1])
            && !ReferenceEquals(classifierKnown.Args.Value[1], predicateKnown.Args.Value[1]), "known nullable flags/classifier/predicate");
        // Exercise the still-live DOM consumer after the single frontier, not a typed round-trip adapter.
        var physical = call.ToDom();
        MaterializeCallWitnesses(physical);
        SourceCheck(physical["nullablePendingWitnesses"] == null && physical["args"] is System.Text.Json.Nodes.JsonArray { Count: 2 }
            && physical["sig"] is System.Text.Json.Nodes.JsonArray { Count: 2 }, "late witness consumption and signature finalization");
        var rejected = false;
        try { SourceRun(file); } catch (InvalidOperationException error) when (error.Message.Contains("assigned nullable witnesses twice", StringComparison.Ordinal)) { rejected = true; }
        SourceCheck(rejected, "pending witness non-reentrance");
    }

    static void SourceGeneratedDelegateSelfTest()
    {
        var test = SourceTest(new TypeNode.Tv("method", 0));
        var target = SourceMethod("generated", "witness:generated", new[] { SourceParameter("U") }, SourceReturn(test));
        target.Generated = true;
        var construction = new BirNewDelegate {
            Method = "generated", CalleeOwner = new TypeNode.Fqn("WitnessProbe"),
            TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) },
            FuncType = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Boolean"), Array.Empty<TypeNode>()),
        };
        var caller = SourceMethod("caller", "witness:generated-caller", new[] { SourceParameter("A"), SourceParameter("B") }, SourceReturn(construction));
        var file = SourceFile(target, caller);
        SourceRun(file);
        SourceCheck(file.Methods.Value is [var retained] && ReferenceEquals(retained, caller), "generated target removed by actual declaration identity");
        var closure = ((BirReturn)caller.Body.Value[0]).Value.Value as BirNewClosure;
        SourceCheck(closure?.SynthClass.HasValue == true && closure.SynthClass.Value._syntheticFrameBound.OrDefault()
            && closure.TypeArgs.Value[0] is TypeNode.Tv { Scope: "method", I: 1 }
            && closure.SynthClass.Value.TypeParams.Value.Length == 1 && closure.SynthClass.Value.Fields.Value.Length == 1,
            "generated target's dense frame remains separate from caller method position");
        var operation = (BirIsInstance)((BirReturn)closure.SynthClass.Value.Body.Value[0]).Value.Value;
        SourceCheck(operation.Type.Value is TypeNode.Tv { Scope: "type", I: 0 }
            && ((BirFieldGet)operation.KotlinTypeWitness.Value).Recv.Value is BirFieldGet
            && !ReferenceEquals(operation, test) && !ReferenceEquals(closure.SynthClass.Value.TypeParams.Value[0], target.TypeParams.Value[0]),
            "generated closure owns cloned target body and remapped witness occurrences");
    }

    static void SourceCaptureWitnessSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var closureTest = SourceTest(variable);
        var closureSource = new BirClosureDefinition {
            Name = "WitnessClosure", TypeParams = new[] { SourceParameter("T") }, Fields = Array.Empty<BirCaptureDescriptor>(),
            Params = Array.Empty<BirLambdaParameter>(), Ret = new TypeNode.Fqn("kotlin.Boolean"), Body = new[] { SourceReturn(closureTest) },
        };
        var closure = new BirNewClosure {
            ClosureType = new TypeNode.Fqn("WitnessClosure"), TypeArgs = new TypeNode[] { variable },
            Captures = Array.Empty<IBirExpression>(), SynthClass = closureSource,
        };
        var samTest = SourceTest(new TypeNode.Tv("type", 0));
        var delegated = new BirConstructor { Params = Array.Empty<BirParameter>(), ThisArgs = Array.Empty<IBirExpression>() };
        var initialized = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
        var invoke = SourceMethod("invoke", "witness:sam-invoke", Array.Empty<BirTypeParameter>(), SourceReturn(samTest));
        var samSource = new BirClassDefinition {
            Name = "WitnessSam", TypeParams = new[] { SourceParameter("T") }, Fields = Array.Empty<BirFieldDeclaration>(),
            Ctors = new[] { delegated, initialized }, Methods = new[] { invoke }, _syntheticFrameBound = true,
        };
        var sam = new BirNewSam { SamType = new TypeNode.Fqn("WitnessSam"), TypeArgs = new TypeNode[] { variable }, Captures = Array.Empty<IBirExpression>(), SynthClass = samSource };
        var caller = SourceMethod("caller", "witness:captures", new[] { SourceParameter("T") },
            new BirExpressionStatement { Expr = closure }, SourceReturn(sam));
        var file = SourceFile(caller);
        SourceRun(file);
        SourceCheck(ReferenceEquals(closure.SynthClass.Value, closureSource) && ReferenceEquals(sam.SynthClass.Value, samSource)
            && ReferenceEquals(samSource.Methods.Value[0], invoke) && ReferenceEquals(samSource.Ctors.Value[0], delegated), "actual synthetic owner/member identity");
        SourceCheck(closureSource.Fields.Value.Length == 1 && closure.Captures.Value.Length == 1 && closureTest.KotlinTypeWitness.HasValue
            && samSource.Fields.Value.Length == 1 && sam.Captures.Value.Length == 1 && samTest.KotlinTypeWitness.HasValue,
            "source closure and dense SAM capture correspondence");
        SourceCheck(delegated.ThisArgs.Value.Length == 1 && delegated.DelegationSig.Value.Length == 1
            && initialized.Body.Value is [BirSetField] && initialized.Params.Value.Length == 1, "constructor delegation and direct field initialization");

        var generatedTest = SourceTest(new TypeNode.Tv("type", 0));
        var generated = new BirClassDefinition {
            Name = "WitnessGenerated", Generated = true, TypeParams = new[] { SourceParameter("T") },
            Methods = new[] { SourceMethod("test", "witness:generated-type-test", Array.Empty<BirTypeParameter>(), SourceReturn(generatedTest)) },
            Fields = Array.Empty<BirFieldDeclaration>(), Ctors = new[] { new BirConstructor { Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() } },
        };
        var allocation = new BirNew { Type = new TypeNode.Fqn("WitnessGenerated", new TypeNode[] { new TypeNode.Fqn("kotlin.String") }), Args = Array.Empty<IBirExpression>() };
        var generatedFile = SourceFile(SourceMethod("factory", "witness:factory", Array.Empty<BirTypeParameter>(), SourceReturn(allocation)));
        generatedFile.Types = new IBirTypeDeclaration[] { generated };
        SourceRun(generatedFile);
        SourceCheck(ReferenceEquals(generatedFile.Types.Value[0], generated) && generated.Fields.Value.Length == 1
            && generated.Ctors.Value[0].Params.Value.Length == 1 && allocation.NullablePendingWitnesses.Value.Length == 1
            && generatedTest.KotlinRuntimeTypeWitness.HasValue, "generated type field/constructor/allocation connected mutation");
    }

    static void SourceSuspendWitnessSelfTest()
    {
        var test = SourceTest(new TypeNode.Tv("type", 0));
        var existing = new BirCaptureDescriptor { Name = Prefix + "0", Type = new TypeNode.Fqn("kotlin.String") };
        var lambda = new BirNewSuspendLambda {
            TypeFrame = "dense", TypeParams = new[] { SourceParameter("U") }, TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) },
            _suspendDeclarationTypeArgs = new TypeNode[] { new TypeNode.Tv("type", 0) },
            Captures = new[] { existing }, CapValues = Array.Empty<IBirExpression>(), Body = new[] { SourceReturn(test) },
        };
        var caller = SourceMethod("caller", "witness:suspend", new[] { SourceParameter("A"), SourceParameter("B") }, SourceReturn(lambda));
        var file = SourceFile(caller);
        SourceRun(file);
        SourceCheck(ReferenceEquals(((BirReturn)caller.Body.Value[0]).Value.Value, lambda)
            && ReferenceEquals(lambda.Captures.Value[0], existing) && lambda.Captures.Value.Length == 2
            && lambda.CapValues.Value.Length == 2 && lambda.CapValues.Value[0] == null
            && lambda.Captures.Value[1].Name.Value == Prefix + "0$", "dense suspend positional capture padding and identity");
        SourceCheck(caller.NullableWitnessIndices.Value.SequenceEqual(new[] { 1 })
            && ((BirFieldGet)test.KotlinTypeWitness.Value).Recv.Value is BirLocal { Name.Value: "dotkt$nullableWitness$0$" }
            && lambda.CapValues.Value[1] is BirLocal { Name.Value: "dotkt$nullableWitness$1" }, "dense suspend source/type and caller/method index spaces");

        var sourceTest = SourceTest(new TypeNode.Tv("method", 4));
        var sourceLambda = new BirNewSuspendLambda {
            TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) },
            _suspendDeclarationTypeArgs = new TypeNode[] { new TypeNode.Tv("method", 4) },
            Captures = Array.Empty<BirCaptureDescriptor>(), CapValues = Array.Empty<IBirExpression>(), Body = new[] { SourceReturn(sourceTest) },
        };
        var sourceCaller = SourceMethod("sourceCaller", "witness:source-suspend", new[] { SourceParameter("T") }, SourceReturn(sourceLambda));
        SourceRun(SourceFile(sourceCaller));
        SourceCheck(sourceCaller.NullableWitnessIndices.Value.SequenceEqual(new[] { 0 }) && sourceLambda.Captures.Value.Length == 1
            && ((BirFieldGet)sourceTest.KotlinTypeWitness.Value).Recv.Value is BirLocal { Name.Value: "dotkt$nullableWitness$0" },
            "non-dense suspend uses saved declaration correspondence, not construction index as source identity");
    }

    static void SourceRepresentationWitnessSelfTest()
    {
        var ordinary = SourceTest(new TypeNode.Tv("method", 1));
        var nullable = SourceTest(new TypeNode.Tv("method", 0));
        var method = SourceMethod("represented", "witness:represented", new[] { SourceParameter("T?"), SourceParameter("T") },
            new BirExpressionStatement { Expr = ordinary }, SourceReturn(nullable));
        method.NullableRepresentationFrame = new NullableRepresentationFrame(1, new[] { 0 }, new[] { 1, 0 });
        // Demand was collected in the source frame before physical companion expansion.
        var source = SourceMethod("source", "witness:represented", new[] { SourceParameter("T") }, SourceReturn(SourceTest(new TypeNode.Tv("method", 0))));
        var demand = NullableWitnessDemand.Collect(new[] { SourceFile(source) }, null);
        Apply(SourceFile(method), demand, null, null, null);
        SourceCheck(method.NullableWitnessIndices.Value.SequenceEqual(new[] { 0 }) && method.Params.Value.Length == 1
            && ((BirFieldGet)ordinary.KotlinTypeWitness.Value).Recv.Value is BirLocal
            && ((BirFieldGet)nullable.KotlinTypeWitness.Value).Recv.Value is BirNew tuple
            && tuple.Args.Value[0] is BirBinaryOperation { Op.Value: "|" }, "explicit physical-order nullable companion mapping");
    }
}
