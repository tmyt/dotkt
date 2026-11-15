using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static void DeclarationSourceSelfTest()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Typed suspend declarations: " + message);
        }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var metadataFile = new BirFileDeclaration { FileClass = "MetadataOnly",
            Methods = Array.Empty<BirFunctionDeclaration>(), Types = Array.Empty<IBirTypeDeclaration>() };
        var metadataContext = new SuspendModuleContext(new[] { metadataFile },
            ReferenceMetadataIndex.Build(Array.Empty<string>()), new HashSet<string>(), false,
            new Dictionary<string, string>(), _ => false, null, null, null, new DeclarationTestTransitions());
        ApplyNamedSource(metadataContext);
        SuspendLambdaLowering.ApplyAll(metadataContext);
        Require(metadataContext.Stage0Complete && metadataContext.NamedEntries.Count == 0
            && metadataFile.Methods.Value.Length == 0 && metadataFile.Types.Value.Length == 0,
            "a module without suspend declarations or values required an unused continuation ABI");
        var any = new TypeNode.Fqn("kotlin.Any");
        var sourceReturn = new TypeNode.Nullable(new TypeNode.Tv("method", 0));
        var source = new BirFunctionDeclaration {
            Name = "visit", DeclarationId = "visit-source", DeclarationSourceName = "visit",
            ExplicitClrName = "selectedVisit", Mods = new BirModifiers { Suspend = true, Operator = true },
            Static = false, Abstract = true, Virtual = true, Ret = any, SuspendRet = any,
            SuspendResult = sourceReturn, NullableGenericSuspendRet = sourceReturn,
            Params = new[] { new BirParameter { Name = "value", Type = any, KotlinType = sourceReturn } },
            TypeParams = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { any },
                ErasedInnerConstraints = new TypeNode[] { new TypeNode.Fqn("kotlin.Comparable", new TypeNode[] { new TypeNode.Tv("method", 0) }) } } },
            Overrides = new[] { new BirOverride { DeclarationId = "selected-base" } },
            PhysicalSlotBridge = true, ClrInterfaceSlotBridge = true, Vis = "public",
            ClrInterfaceImpls = new[] { new BirMethodImpl { Owner = new TypeNode.Fqn("SourceContract"), Member = "visit",
                Params = new TypeNode[] { any }, Ret = any } },
            ClrBaseImpls = OptionalField<BirMethodImpl[]>.Null,
            Body = Array.Empty<IBirStatement>(), Attrs = Array.Empty<BirAttribute>(),
        };
        RoundtripMetadata.AddSourceMethodIdentity(source, "sourceVisit");
        var sibling = (BirFunctionDeclaration)source.DeepClone();
        sibling.DeclarationId = "second-source"; sibling.ExplicitClrName = "secondVisit";
        var owner = new BirClassDefinition { Name = "SourceOwner", Kind = "class", Abstract = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { source, sibling } };
        var file = new BirFileDeclaration { FileClass = "SourceFile", Methods = Array.Empty<BirFunctionDeclaration>(),
            Types = new IBirTypeDeclaration[] { owner } };
        var transitions = new DeclarationTestTransitions();
        var positions = new Dictionary<string, int[]> { ["visit-source"] = new[] { 0 } };
        var identities = new Dictionary<string, string> { ["visit-source"] = "original-source" };
        var context = new SuspendModuleContext(new[] { file }, references, new HashSet<string>(), false,
            new Dictionary<string, string> { ["kotlin.coroutines.Continuation"] = "ContinuationCarrier" },
            _ => false, null, positions, identities, transitions);
        Require(context.NamedEntries.Count == 2 && ReferenceEquals(context.NamedEntries[0].Method, source)
            && ReferenceEquals(context.NamedEntries[1].Method, sibling), "physical signature equality collapsed actual declarations");
        ApplyNamedSource(context);
        var methods = owner.Methods.Value;
        Require(methods.Length == 4 && methods[0].Name.Value == "selectedVisit$dotkt_suspend"
            && methods[1].Name.Value == "visit" && methods[2].Name.Value == "secondVisit$dotkt_suspend"
            && methods[3].Name.Value == "visit", "cold/Task output or source-vector order changed");
        Require(transitions.Retired.Count == 2 && ReferenceEquals(transitions.Retired[0], source)
            && transitions.Published.Count == 4 && ReferenceEquals(file.Types.Value[0], owner),
            "actual declaration retirement/append identity changed");
        Require(ReferenceEquals(positions["visit-source"], positions["visit-source|cold"])
            && identities["visit-source|cold"] == "original-source|cold", "physical projection maps were rebuilt or lost");
        var cold = methods[0]; var task = methods[1];
        Require(cold.DeclarationId.Value == "visit-source|cold" && task.DeclarationId.Value == "visit-source"
            && cold.Overrides.Value[0].DeclarationId.Value == "selected-base"
            && cold.Overrides.Value[0].PhysicalDeclarationId.Value == "selected-base|cold"
            && !task.Overrides.Value[0].PhysicalDeclarationId.IsPresent, "semantic/physical declaration IDs crossed");
        Require(cold.Params.Value.Length == 2 && task.Params.Value.Length == 1
            && cold.Params.Value[1].Name.Value == "dotkt$sm$completion"
            && !ReferenceEquals(cold.Params.Value[0], source.Params.Value[0])
            && !ReferenceEquals(cold.Params.Value[0], task.Params.Value[0]), "independent parameter births/completion order changed");
        Require(cold.Abstract.Value && cold.Virtual.Value && !cold.Static.Value
            && task.Abstract.Value && task.SuspendBridge.Value && !cold.Mods.IsPresent
            && task.Mods.Value.Operator.Value && !task.Mods.Value.Suspend.IsPresent,
            "abstract/physical/Kotlin modality was conflated");
        Require(cold.SuspendSourceParams.Value.Length == 1 && task.SuspendSourceRet.Value == any
            && !ReferenceEquals(cold.SuspendSourceParams.Value, task.SuspendSourceParams.Value)
            && task.NullableGenericRet.Value == sourceReturn && task.Ret.Value is TypeNode.Fqn { Name: TaskBclFqn },
            "saved source frame or selected Task result changed");
        Require(cold.ClrInterfaceImpls.Value[0].Member.Value == "visit$dotkt_suspend"
            && cold.ClrInterfaceImpls.Value[0].Params.Value.Length == 2
            && task.ClrInterfaceImpls.Value[0].Ret.Value is TypeNode.Fqn { Name: TaskBclFqn, Args.Length: 1 }
            && !cold.ClrBaseImpls.IsPresent && !task.ClrBaseImpls.IsPresent,
            "physical MethodImpl descriptor projection/presence changed");

        var bodyStatement = new BirExpressionStatement { Expr = new BirLocal { Name = "value" } };
        var movable = new BirFunctionDeclaration { Body = new IBirStatement[] { bodyStatement } };
        var moved = context.AcquireBody(new SourceFunctionInput.Named(file, owner, movable, "", []));
        Require(ReferenceEquals(moved[0], bodyStatement) && movable.Body.Value.Length == 0,
            "app body was cloned instead of moved");
        var lambda = new BirNewSuspendLambda { Body = new IBirStatement[] { bodyStatement } };
        var lambdaBody = context.AcquireBody(new SourceFunctionInput.Lambda(file, owner, lambda, "lambda", [], 0, false, false));
        Require(ReferenceEquals(lambdaBody[0], bodyStatement) && lambda.Body.Value.Length == 0,
            "lambda body was cloned or retained in two continuing owners");
        var retained = new BirFunctionDeclaration { Body = new IBirStatement[] { bodyStatement } };
        var local = new SuspendModuleContext(new[] { file }, references, new HashSet<string> { ContinuationImplFqn }, false,
            new Dictionary<string, string> { ["kotlin.coroutines.Continuation"] = "ContinuationCarrier" },
            _ => false, null, null, null, transitions);
        var independent = local.AcquireBody(new SourceFunctionInput.Named(file, owner, retained, "", []));
        Require(ReferenceEquals(retained.Body.Value[0], bodyStatement) && !ReferenceEquals(independent[0], bodyStatement)
            && transitions.Cloned.Contains(bodyStatement), "retained source body was mutated or independent birth was not reported");
        Console.WriteLine("[typed suspend declarations] self-test OK (actual registry, cold/Task source identities, MethodImpls, order, explicit births and body moves)");
    }

    sealed class DeclarationTestTransitions : SourceDeclarationTransitions
    {
        internal readonly List<BirGraphValue> Cloned = new();
        internal readonly List<BirFunctionDeclaration> Retired = new(), Published = new();
        internal override BirGraphValue CloneIndependent(BirGraphValue source) { Cloned.Add(source); return source.DeepClone(); }
        internal override void RetireDiscardedSubtree(BirGraphValue source) { }
        internal override void ProjectMethod(BirFunctionDeclaration origin, BirFunctionDeclaration projection) { }
        internal override void RetireMethod(BirFileDeclaration file, IBirTypeDeclaration owner, BirFunctionDeclaration method)
        {
            if (!(owner?.Methods.OrEmpty() ?? file.Methods.OrEmpty()).Any(current => ReferenceEquals(current, method)))
                throw new InvalidOperationException("Suspend retirement was reported after removal");
            Retired.Add(method);
        }
        internal override void PublishMethods(BirFileDeclaration file, IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
                if (!(owner?.Methods.OrEmpty() ?? file.Methods.OrEmpty()).Any(current => ReferenceEquals(current, method)))
                    throw new InvalidOperationException("Suspend append was reported before attachment");
            Published.AddRange(methods);
        }
        internal override void PublishTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> types) { }
        internal override void RetireTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> types) { }
    }
}
