using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendLambdaLowering
{
    internal static void SourceSelfTest()
    {
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var integer = new TypeNode.Fqn("kotlin.Int");
        var b = new TypeNode.Tv("type", 1);
        var function = new TypeNode.Fn(true, b, Array.Empty<TypeNode>());
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed suspend lambdas: " + message); }
        BirNewSuspendLambda Lambda(string capture, IBirExpression value, params IBirStatement[] body) => new() {
            Arity = 0, Captures = new[] { new BirCaptureDescriptor { Name = capture, Type = b } },
            CapValues = new[] { value }, Params = Array.Empty<BirLambdaParameter>(), SuspendRet = b, FuncType = function,
            TypeParams = new[] { new BirTypeParameter { Name = "B" } }, TypeArgs = new TypeNode[] { b }, Body = body,
        };
        var nested = Lambda("nested", new BirLocal { Name = "cap", Sty = b }, new BirReturn { Value = new BirLocal { Name = "nested" } });
        var outer = Lambda("cap", new BirLocal { Name = "value", Sty = b }, new BirExpressionStatement { Expr = nested },
            new BirReturn { Value = new BirLocal { Name = "cap" } });
        var source = new BirFunctionDeclaration { Name = "make", Static = false, Ret = function,
            Params = new[] { new BirParameter { Name = "value", Type = b } }, Body = new IBirStatement[] { new BirReturn { Value = outer } } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", TypeParams = new[] {
            new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" },
        }, Methods = new[] { source } };
        var file = new BirFileDeclaration { FileClass = "LambdaKt", Types = new IBirTypeDeclaration[] { owner } };
        var transitions = new LambdaSourceTestTransitions();
        var module = new SuspendColdLowering.SuspendModuleContext(new[] { file }, references, new HashSet<string>(), false,
            new Dictionary<string, string> { ["kotlin.coroutines.Continuation"] = "ContinuationCarrier" },
            _ => false, null, null, null, transitions);
        SuspendColdLowering.ApplyNamedSource(module);
        Require(module.Stage0Complete && module.NamedEntries.Count == 0, "lambda-only module did not run all-root Stage0");
        ApplyAll(module);
        Require(ReferenceEquals(file.Types.Value[0], owner) && ReferenceEquals(owner.Methods.Value[0], source)
            && file.Types.Value.Length == 3 && file.Types.Value[1].Name.Value == "Owner_make_lambda1$sm"
            && file.Types.Value[2].Name.Value == "Owner_make_lambda2$sm", "nested-first append order or continuing owner/method identity changed");
        var construction = ((BirReturn)source.Body.Value[0]).Value.Value as BirNew;
        Require(construction?.Type.Value is TypeNode.Fqn { Args.Length: 2 } applied
            && applied.Args[0] is TypeNode.Tv { Scope: "type", I: 0 } && applied.Args[1] is TypeNode.Tv { Scope: "type", I: 1 }
            && construction.Args.Value.Length == 2 && construction.Args.Value[0] is BirLocal { Name.Value: "value" }
            && construction.ArgTypes.Value[0] == b,
            "complete owner prefix or exact construction values/signature changed");
        Require(outer.Body.Value.Length == 0 && nested.Body.Value.Length == 0
            && !outer.CapValues.IsPresent && !nested.CapValues.IsPresent
            && transitions.Retired.Contains(outer) && transitions.Retired.Contains(nested)
            && transitions.Published.SequenceEqual(file.Types.Value.Skip(1), ReferenceEqualityComparer.Instance),
            "lambda retirement retained moved operands or publication did not use exact attached types");

        // A donor captures only N, while the selected physical caller's erased
        // bounds still mention M. Complete that dependency before remapping the
        // declaration; caller-owned construction facts must not be rebound twice.
        var parameter = new BirLambdaParameter { Name = "p", Type = new TypeNode.Tv("method", 0) };
        var captureValue = new BirLocal { Name = "callerValue", Sty = new TypeNode.Tv("method", 1) };
        var donor = new BirNewSuspendLambda { Arity = 1, TypeFrame = "dense",
            _suspendDeclarationTypeArgs = new TypeNode[] { new TypeNode.Tv("method", 7) },
            TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) },
            TypeParams = new[] { new BirTypeParameter { Name = "DonorN" } },
            Params = new[] { parameter }, Captures = new[] { new BirCaptureDescriptor { Name = "captured", Type = new TypeNode.Tv("method", 0) } },
            CapValues = new IBirExpression[] { captureValue }, SuspendRet = new TypeNode.Tv("method", 0),
            FuncType = new TypeNode.Fn(true, new TypeNode.Tv("method", 1), new TypeNode[] { new TypeNode.Tv("method", 1) }),
            Sty = new TypeNode.Fqn("caller.Stamp"), Body = Array.Empty<IBirStatement>() };
        var callerFrame = new[] { new BirTypeParameter { Name = "M" },
            new BirTypeParameter { Name = "N", ErasedInnerConstraints = new TypeNode[] { new TypeNode.Tv("method", 0) } } };
        var values = donor.CapValues.Value;
        var stamp = donor.Sty.Value;
        var prefix = NormalizeOwnerCapturePrefix(donor, "Owner", callerFrame, module);
        Require(prefix == 2 && donor.TypeParams.Value.Length == 4 && donor.TypeParams.Value[2].Name.Value == "DonorN"
            && donor.TypeParams.Value[3].Name.Value == "M"
            && donor.TypeParams.Value[2].ErasedInnerConstraints.Value[0] is TypeNode.Tv { Scope: "method", I: 1 }
            && donor.TypeArgs.Value[2] is TypeNode.Tv { Scope: "method", I: 1 }
            && donor.TypeArgs.Value[3] is TypeNode.Tv { Scope: "method", I: 0 }, "caller constraint dependency closure lost its dense correspondence");
        Require(ReferenceEquals(donor.Params.Value[0], parameter) && ReferenceEquals(donor.CapValues.Value, values)
            && ReferenceEquals(donor.CapValues.Value[0], captureValue) && ReferenceEquals(donor.Sty.Value, stamp)
            && captureValue.Sty.Value is TypeNode.Tv { Scope: "method", I: 1 }
            && !donor.TypeFrame.IsPresent && !donor._suspendDeclarationTypeArgs.IsPresent,
            "normalization replaced a live parameter or rebound a caller-owned construction fact");

        var nestedParameter = new BirLambdaParameter { Name = "own", Type = new TypeNode.Tv("type", 0) };
        var nestedDeclaration = new BirNewSuspendLambda { Arity = 1, TypeArgs = new TypeNode[] { new TypeNode.Tv("type", 0) },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { nestedParameter }, CapValues = new IBirExpression[] { new BirLocal { Name = "v", Sty = new TypeNode.Tv("type", 0) } },
            Captures = new[] { new BirCaptureDescriptor { Name = "v", Type = new TypeNode.Tv("type", 0) } },
            SuspendRet = new TypeNode.Tv("type", 0),
            FuncType = new TypeNode.Fn(true, new TypeNode.Tv("type", 0), new TypeNode[] { new TypeNode.Tv("type", 0) }),
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "own" } } } };
        var read = new BirCallStatic { Ret = new TypeNode.Tv("type", 0),
            Sig = new TypeNode[] { new TypeNode.Tv("type", 0) }, Args = new IBirExpression[] { nestedDeclaration } };
        TypedExecutableFrames.SubstituteInPlace(read, Array.Empty<TypeNode>(), 0, new TypeNode[] { integer });
        Require(read.Ret.Value == integer && read.Sig.Value[0] is TypeNode.Tv { Scope: "type", I: 0 }
            && ReferenceEquals(read.Args.Value[0], nestedDeclaration) && ReferenceEquals(nestedDeclaration.Params.Value[0], nestedParameter)
            && nestedParameter.Type.Value is TypeNode.Tv { Scope: "type", I: 0 }
            && nestedDeclaration.TypeArgs.Value[0] == integer
            && nestedDeclaration._suspendDeclarationTypeArgs.Value[0] is TypeNode.Tv { Scope: "type", I: 0 },
            "in-place owner application crossed a referenced or nested declaration frame");
        Console.WriteLine("[typed suspend lambdas] self-test OK (lambda-only Stage0, nested append order, exact MOVE/retirement, complete owner prefix, caller dependencies, in-place frame ownership)");
    }

    sealed class LambdaSourceTestTransitions : SuspendColdLowering.SourceDeclarationTransitions
    {
        internal readonly HashSet<BirGraphValue> Retired = new(ReferenceEqualityComparer.Instance);
        internal readonly List<IBirTypeDeclaration> Published = new();
        internal override BirGraphValue CloneIndependent(BirGraphValue source) => source.DeepClone();
        internal override void RetireDiscardedSubtree(BirGraphValue source)
        {
            if (source is BirNewSuspendLambda lambda && (lambda.Body.OrEmpty().Length != 0 || lambda.CapValues.IsPresent))
                throw new InvalidOperationException("A consumed lambda still owns moved executable operands");
            Retired.Add(source);
        }
        internal override void RetireMethod(BirFileDeclaration file, IBirTypeDeclaration owner, BirFunctionDeclaration method)
            => throw new InvalidOperationException("A lambda-only test retired a named method");
        internal override void ProjectMethod(BirFunctionDeclaration source, BirFunctionDeclaration projection)
            => throw new InvalidOperationException("A lambda-only test projected a named method");
        internal override void PublishMethods(BirFileDeclaration file, IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> appended)
            => throw new InvalidOperationException("A lambda-only test published a named method");
        internal override void PublishTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended)
        {
            foreach (var type in appended)
                if (!file.Types.OrEmpty().Any(actual => ReferenceEquals(actual, type)))
                    throw new InvalidOperationException("A lambda type was published before attachment");
            Published.AddRange(appended);
        }
        internal override void RetireTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed)
            => throw new InvalidOperationException("A lambda-only test retired an intrinsic closure type");
    }
}
