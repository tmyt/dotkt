using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static void NativeProofSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning native proof: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Owning native proof accepted a missing captured declaration");
        }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var disposable = new TypeNode.Fqn("System.IDisposable");
        var memory = new TypeNode.Fqn("System.IO.MemoryStream");
        var methodTag = new TypeNode.Tv("method", 0);
        var methodValue = new TypeNode.Tv("method", 1);
        var ownerTag = new TypeNode.Tv("type", 0);
        var markerMethod = new TypeNode.Fqn("Marker", new TypeNode[] { methodTag });
        BirTypeParameter Parameter(string name, params TypeNode[] constraints) => new() {
            Name = name, Constraints = constraints.Length == 0 ? default : constraints,
        };
        BirFunctionDeclaration Method(string name) => new() { Name = name, DeclarationId = name,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"),
            TypeParams = new[] { Parameter("Tag"), Parameter("T", markerMethod) }, Body = Array.Empty<IBirStatement>() };
        var construct = Method("construct");
        construct.Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirNew {
            Type = new TypeNode.Fqn("Owner", new TypeNode[] { methodTag, methodValue, methodValue }),
            Args = Array.Empty<IBirExpression>() } } };
        var pure = Method("pure");
        var donor = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(),
            TypeParams = new[] { Parameter("Tag"), Parameter("T", markerMethod) },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirCallStatic {
                Owner = new TypeNode.Fqn("ProofTests"), Method = "construct", DeclarationId = "construct",
                TypeArgs = new TypeNode[] { methodTag, methodValue }, Args = Array.Empty<IBirExpression>() } } } };
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
        var declaration = new BirClassDefinition { Name = "Owner", Kind = "class",
            TypeParams = new[] { Parameter("Tag"), Parameter("T", new TypeNode.Fqn("Marker", new TypeNode[] { ownerTag })),
                Parameter("Storage", new TypeNode.Fqn("Marker", new TypeNode[] { new TypeNode.Star() })) },
            Ctors = new[] { constructor },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(new NullableRepresentationFrame(2,
                Array.Empty<int>(), storageIndices: new[] { 1 })) };
        var nativeField = new BirFieldDeclaration { Name = "value", _nativeLocation = true, Type = ownerTag };
        var root = new BirFileDeclaration { FileClass = "ProofTests", Methods = new[] { construct, pure,
            new BirFunctionDeclaration { Name = "donors", Params = Array.Empty<BirParameter>(),
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = donor } } } },
            Types = new IBirTypeDeclaration[] {
                new BirInterfaceDefinition { Name = "Marker", Kind = "interface", TypeParams = new[] { Parameter("Tag") },
                    Interfaces = new TypeNode[] { disposable } }, declaration,
                new BirClassDefinition { Name = "SelfBase", Kind = "class", TypeParams = new[] { Parameter("T") }, Base = memory },
                new BirClassDefinition { Name = "SelfOwner", Kind = "class", TypeParams = new[] {
                    Parameter("Self", new TypeNode.Fqn("SelfBase", new TypeNode[] { ownerTag })) } },
                new BirClassDefinition { Name = "System.IO.MemoryStream", Kind = "class" },
                new BirClassDefinition { Name = "NativeFieldWitness", Kind = "class",
                    TypeParams = new[] { Parameter("Native"), Parameter("Other") }, Fields = new[] { nativeField } },
            } };
        var original = root.DeepClone();
        var values = GenericRepresentationPolicy.CreateSource(new[] { root }, refs.Aliases, refs);
        var bindings = PrepareCurrentDeclarations(new[] { root }, values, out var preparation);
        var plan = PrepareNativeProofs(bindings, refs, values);
        var donorBinding = bindings.GetSource(donor);
        var ctorBinding = bindings.GetSource(constructor);
        Require(root.StructurallyEquals(original) && plan.Bounds(bindings.GetSource(construct), 1).Contains(disposable)
            && !plan.Bounds(bindings.GetSource(pure), 1).Any() && plan.Bounds(donorBinding, 1).Contains(disposable)
            && plan.ExactTypeArgument("NativeFieldWitness", 0) && !plan.ExactTypeArgument("NativeFieldWitness", 1)
            && plan.ExactTypeArgument("Owner", 1) && !plan.ExactTypeArgument("Owner", 2)
            && !plan.ExactTypeArgument("SelfOwner", 0),
            "fixed-point demand changed source facts, pure forwarding, native fields, donor context or companion rows");

        var dependent = new[] { Parameter("Tag"), Parameter("R", new TypeNode.Fqn("System.Collections.Generic.IComparer`1",
            new TypeNode[] { methodTag })), Parameter("S", new TypeNode.Fqn("System.Collections.Generic.IComparer`1",
            new TypeNode[] { ownerTag })) };
        var frame = new NullableRepresentationFrame(2, Array.Empty<int>(), storageIndices: new[] { 1 });
        var overrideBound = new OwnerConstraintOverrideBounds(new[] { new OwnerConstraintOverrideBounds.Row(0,
            memory, ownerTag, Array.Empty<TypeNode>(), new TypeNode[] { memory }) });
        Require(plan.ReadRows(new[] { Parameter("T", memory) }, false).Count == 0
            && plan.ReadRows(new[] { Parameter("T", memory) }, false, physicalDeclaration: true)[0].Contains(memory)
            && plan.ReadRows(dependent, false, projectsBound: (index, bound) =>
                OwnerConstrainedMethodLowering.ProjectsBound(index, bound, null, null, refs)).Count == 0
            && plan.ReadRows(dependent, false, physicalDeclaration: true).Count == 2
            && plan.ReadRows(new[] { Parameter("Tag"), dependent[2] }, false, projectsBound: (index, bound) =>
                ProjectsCapturedBound(bound, index, 0, null, refs)).Count == 0
            && OwnerConstrainedMethodLowering.ProjectsBound(0, memory, null, overrideBound, refs),
            "source/dependent/override bounds replaced shipped physical proof policy");
        var capturedDemand = (TypeNode.Fqn)plan.ReadRows(dependent, false, physicalDeclaration: true)[1][0];
        var sourceDemand = (TypeNode.Fqn)dependent[1].Constraints.Value[0];
        Require(!ReferenceEquals(capturedDemand.Args, sourceDemand.Args), "proof demand retained a mutable source argument vector");
        sourceDemand.Args[0] = new TypeNode.Fqn("System.Object");
        Require(capturedDemand.Args[0] == methodTag, "a source constraint mutation changed captured demand");

        var surface = PrepareCarrierSurface(preparation, plan, refs, values);
        var projection = CarrierProjectionContext.ForSource(surface.Demand, refs);
        var realization = new CarrierRealizationContext(surface, projection, refs, surface.Demand.ReservedNames);
        var constructionState = new SourceRewriteState(realization, (_, _) => null);
        var projectedMarker = new TypeNode.Fqn("Marker", new TypeNode[] { new TypeNode.Star() });
        var requiredConstruction = new TypeNode.Fqn("Owner", new TypeNode[] {
            new TypeNode.Fqn("System.String"), projectedMarker, projectedMarker });
        Require(projection.Rewrite(projectedMarker) == new TypeNode.Fqn(surface.Demand.Owners["Marker"].ErasedName)
            && ConstructionHasHiddenWitnessSource(requiredConstruction, constructionState),
            "carrier construction discarded its demanded native witness");
        var projected = root.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        Require(ReferenceEquals(ctorBinding.Physical, projected["types"][1]["ctors"][0])
            && ReferenceEquals(donorBinding.Physical, projected["methods"][2]["body"][0]["expr"]),
            "ID-less constructor/donor binding used an inferred declaration rather than the observed actual record");
        Reject(() => { _ = donorBinding.Source; });
        var sourceMethod = projected["methods"][0].AsObject();
        var pureMethod = projected["methods"][1].AsObject();
        var owner = projected["types"][1].AsObject();
        var requiredBound = new TypeNode.Fqn("Marker", new TypeNode[] { ownerTag });
        plan.Add(bindings.GetPhysical(owner), 1, requiredBound);
        var copiedOwner = owner.DeepClone().AsObject();
        Require(plan.IsPhysicalConstraint(copiedOwner["typeParams"][1]["constraints"].AsArray(), requiredBound)
            && !plan.IsPhysicalConstraint(copiedOwner["typeParams"][1]["constraints"].AsArray(), new TypeNode.Fqn("Unrequired"))
            && !plan.IsPhysicalConstraint(new JsonArray(TypeJson.Write(requiredBound)), requiredBound),
            "late cloned constraint lost its declaration/index ownership");
        var methodProof = new TypeNode.Fqn("Marker", new TypeNode[] { methodTag });
        var ownerMethodProof = new TypeNode.Fqn("Marker", new TypeNode[] { ownerTag });
        plan.Add(bindings.GetPhysical(sourceMethod), 1, methodProof);
        plan.Add(bindings.GetPhysical(sourceMethod), 1, ownerMethodProof);
        var physicalMethod = new BirFunctionDeclaration { Name = "physical", DeclarationId = "authored-physical-slot",
            TypeParams = construct.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray() }.ToDom();
        plan.TransferIndependentMethodProofs(sourceMethod, physicalMethod);
        var privateBody = new BirFunctionDeclaration { Name = "private", DeclarationId = "separate-private-storage-body",
            TypeParams = pure.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray() }.ToDom();
        plan.TransferIndependentMethodProofs(pureMethod, privateBody);
        Require(plan.IsPhysicalConstraint(physicalMethod["typeParams"][1]["constraints"].AsArray(), methodProof)
            && plan.IsPhysicalConstraint(physicalMethod.DeepClone()["typeParams"][1]["constraints"].AsArray(), methodProof)
            && !plan.IsPhysicalConstraint(privateBody["typeParams"][1]["constraints"].AsArray(), methodProof)
            && !plan.IsPhysicalConstraint(physicalMethod["typeParams"][1]["constraints"].AsArray(), ownerMethodProof)
            && ReferenceEquals(plan.LocalMethod("authored-physical-slot"), physicalMethod),
            "late physical proof transfer changed an independent private body or method identity");
        var late = new BirClassDefinition { Name = "LateLocal", Kind = "class",
            TypeParams = new[] { Parameter("Tag"), Parameter("T", new TypeNode.Fqn("System.Collections.Generic.IEnumerable`1",
                new TypeNode[] { ownerTag })), Parameter("Storage", disposable) },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame) }.ToDom();
        plan.RegisterPhysicalLocalDefinitions(new Dictionary<string, JsonObject> { ["LateLocal"] = late });
        Require(plan.ExactTypeArgument("LateLocal", 1) && !plan.ExactTypeArgument("LateLocal", 2),
            "late physical declaration rows were projected again or borrowed a same-named source declaration");
        var before = sourceMethod["typeParams"][1]["constraints"].DeepClone();
        sourceMethod["typeParams"][1]["constraints"] = new JsonArray(TypeJson.Write(disposable));
        Require(!plan.Bounds(sourceMethod, 1).Contains(disposable), "late query froze captured constraints instead of reading the live declaration");
        sourceMethod["typeParams"][1]["constraints"] = before;

        // Carrier-surface preparation above legitimately authored methods and
        // source-bound facts. A fresh proof plan must leave THAT current graph
        // unchanged, just as the initial planner left its own input unchanged.
        var currentBeforePlanning = root.DeepClone();
        var missingBindings = PrepareCurrentDeclarations(new[] { root }, values);
        _ = PrepareNativeProofs(missingBindings, refs, values);
        root.ToDom((source, actual) => { if (!ReferenceEquals(source, donor)) missingBindings.ObserveProjection(source, actual); });
        Reject(missingBindings.BindProjected);
        Require(root.StructurallyEquals(currentBeforePlanning), "proof planning mutated the continuing typed graph");
        Console.WriteLine("[owning native proof plan] self-test OK (source/physical constraints, fixed-point demand, native witnesses, ID-less handoff, live late rows and independent transfers)");
    }
}
