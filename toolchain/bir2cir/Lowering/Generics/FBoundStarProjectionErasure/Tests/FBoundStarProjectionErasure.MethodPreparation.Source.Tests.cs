using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void MethodPreparationSourceSelfTest() => OwnerConstrainedMethodLowering.MethodPreparationSourceSelfTest();
}

static partial class OwnerConstrainedMethodLowering
{
    internal static void MethodPreparationSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning method preparation: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Method preparation accepted an invalid frontier phase");
        }
        var oldMethods = Methods.ToArray();
        var oldBindings = MethodBindings;
        var oldPrepared = PreparedMethods;
        var oldReferences = References;
        try
        {
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var ownerVariable = new TypeNode.Tv("type", 0);
            var methodVariable = new TypeNode.Tv("method", 0);
            var inputType = new TypeNode.Fqn("Box", new TypeNode[] { ownerVariable });
            BirTypeParameter Parameter(string name, params TypeNode[] bounds) => new() { Name = name, Constraints = bounds };
            BirFunctionDeclaration Method(string name, params BirTypeParameter[] parameters) => new() {
                Name = name, DeclarationId = name, Params = new[] { new BirParameter { Name = "value", Type = inputType } },
                TypeParams = parameters, Ret = inputType, Body = Array.Empty<IBirStatement>(),
            };
            var ownerBound = Method("owner", Parameter("T", ownerVariable));
            var dependentBound = new TypeNode.Fqn("Dependent", new TypeNode[] { methodVariable });
            var dependent = Method("dependent", Parameter("Tag", new TypeNode.Fqn("System.IDisposable")), Parameter("T", dependentBound));
            var prior = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = Array.Empty<TypeNode>() });
            dependent.MethodTypeParameterBoundsPre = prior;
            var framed = Method("framed", Parameter("T", ownerVariable), Parameter("Storage", new TypeNode.Array(methodVariable)));
            var frame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
            framed.NullableRepresentationFrame = frame;
            framed.MethodTypeParameterBoundsPre = prior;
            var overridden = Method("override");
            overridden._ownerConstraintOverrideBounds = new OwnerConstraintOverrideBounds(Array.Empty<OwnerConstraintOverrideBounds.Row>());
            var pure = Method("pure", Parameter("T", new TypeNode.Fqn("Self", new TypeNode[] { methodVariable })));
            var top = Method("top", Parameter("T", ownerVariable));
            var definition = new BirClassDefinition { Name = "Owner", Kind = "class",
                TypeParams = new[] { Parameter("O") }, Methods = new[] { ownerBound, dependent, framed, overridden, pure },
                Fields = new[] { new BirFieldDeclaration { Name = "field", Type = inputType } } };
            var root = new BirFileDeclaration { FileClass = "File", Methods = new[] { top }, Types = new IBirTypeDeclaration[] { definition } };
            var roots = new[] { root };
            var values = GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs);
            var bindings = FBoundStarProjectionErasure.PrepareCurrentDeclarations(roots, values, out var preparation);
            var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, refs, values);
            FBoundStarProjectionErasure.PrepareMethodBounds(bindings, refs);
            Require(Methods.Count == 5
                && new[] { ownerBound, dependent, framed, overridden, top }.Select((method, index) =>
                    ReferenceEquals(Methods[index].MethodBinding.Source, method)).All(value => value)
                && ReferenceEquals(Methods[0].OwnerBinding.Source, definition)
                && ReferenceEquals(Methods[4].OwnerBinding.Source, root)
                && !pure.MethodTypeParameterBoundsPre.IsPresent,
                "selector, definition-before-file order or owner membership changed");
            Require(dependent.MethodTypeParameterBoundsPre.Value.Bounds[0].Length == 0
                && dependent.MethodTypeParameterBoundsPre.Value.Bounds[1].Single() == dependentBound
                && !ReferenceEquals(((TypeNode.Fqn)dependent.MethodTypeParameterBoundsPre.Value.Bounds[1][0]).Args, dependentBound.Args)
                && ReferenceEquals(framed.MethodTypeParameterBoundsPre.Value, prior)
                && ReferenceEquals(framed.NullableRepresentationFrame.Value, frame)
                && overridden.MethodTypeParameterBoundsPre.IsPresent == false,
                "first writer, empty vectors, materialized source bounds or frame were recaptured");
            Record(ownerBound, definition, bindings);
            Require(Methods.Count == 6 && ReferenceEquals(Methods[5].MethodBinding, Methods[0].MethodBinding),
                "registration removed duplicate/order semantics");
            Reject(() => CompleteMethodPreparation(new CurrentDeclarationBindings(roots), refs));

            // This mutation is actual accessor authoring, not another method
            // inventory. Source signatures must include these new ID-less rows.
            var accessors = FBoundStarProjectionErasure.AddFieldAccessorsSource("Owner", 1, definition, bindings);
            var signatures = FBoundStarProjectionErasure.CaptureSourceSignatures(bindings);
            Require(Methods.Count == 6 && accessors.Count == 2 && bindings.SourceLocalDeclarations.Count == 6,
                "accessor preparation widened the original registration/index membership");
            var signatureBinding = bindings.GetCapturedSource(ownerBound);
            var sourceSignature = signatures.GetSourceSignature(signatureBinding);
            Require(ReferenceEquals(sourceSignature, signatures.GetSourceSignature(signatureBinding))
                && sourceSignature is [TypeNode.Fqn { Name: "Box", Args: [TypeNode.Tv { Scope: "type", I: 0 }] }]
                && !ReferenceEquals(((TypeNode.Fqn)sourceSignature[0]).Args, inputType.Args),
                "typed signature lookup recaptured a row or retained its mutable source arguments");
            // The facade method is captured for other authorities but was not
            // part of this type-method signature snapshot. Never infer a row.
            Reject(() => signatures.GetSourceSignature(bindings.GetCapturedSource(top)));
            var demand = FBoundStarProjectionErasure.PrepareCarrierDemand(preparation, proofs, refs, values);
            var surface = new FBoundStarProjectionErasure.CarrierSurfacePreparation(demand, signatures);
            var context = new FBoundStarProjectionErasure.CarrierRealizationContext(surface,
                FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(demand, refs), refs);
            ConfigurePreparedMethodsSource(bindings, refs);
            ownerBound.TypeParams.Value[0].Constraints = new TypeNode[] { ownerVariable };
            ApplySource(context, FBoundStarProjectionErasure.ContainsOwnerTv, type => type, _ => null);
            Require(ownerBound.TypeParams.Value[0].Constraints.Value.Length == 0
                && ownerBound.TypeParams.Value[0].ErasedInnerConstraints.Value.Single() == ownerVariable
                && Methods.Count == 6 && !accessors.Any(accessor => Methods.Any(row => ReferenceEquals(row.MethodBinding.Source, accessor))),
                "source continuation did not use live registered constraints or admitted appended accessors");
            ownerBound.Params.Value[0].Type = new TypeNode.Fqn("AfterCapture");
            inputType.Args[0] = new TypeNode.Fqn("MutatedArgument");
            Require(definition.Methods.Value.All(method =>
                    signatures.GetSourceSignature(bindings.GetCapturedSource(method)) != null)
                && ReferenceEquals(signatures.GetSourceSignature(signatureBinding), sourceSignature)
                && sourceSignature is [TypeNode.Fqn { Name: "Box", Args: [TypeNode.Tv { Scope: "type", I: 0 }] }]
                && signatures.GetSourceSignature(bindings.GetCapturedSource(accessors[0])).Length == 0
                && signatures.GetSourceSignature(bindings.GetCapturedSource(accessors[1])) is
                    [TypeNode.Fqn { Name: "Box", Args: [TypeNode.Tv { I: 0 }] }],
                "signature capture was late, aliased mutable type vectors or lost appended records");
            Reject(() => signatures.GetSourceSignature(bindings.GetCapturedSource(top)));
            Require(Methods.Count == 6 && ReferenceEquals(Methods[0].Method, ownerBound)
                && ReferenceEquals(Methods[0].Owner, definition), "source continuation rebuilt or cleared the registrations");
            Require(ownerBound.TypeParams.Value[0].Constraints.Value.Length == 0
                && ownerBound.TypeParams.Value[0].ErasedInnerConstraints.Value[0] == ownerVariable,
                "terminal custody lost the source continuation's physical constraint result");
            var ledger = new SourceDeclarationProjection(roots, bindings);
            ledger.SealSourceContinuations();
            FBoundStarProjectionErasure.CompleteRealization(context);
            Reject(() => signatures.CompleteSourceLifetime());
            Reject(() => signatures.GetSourceSignature(signatureBinding));
            Reject(() => CompleteMethodPreparation(bindings, refs));
            Require(Methods.Count == 0 && MethodBindings == null && !PreparedMethods && ReferenceEquals(References, refs),
                "completion kept prepared declarations or changed the actual reference authority");
            bindings.CompleteSourceCustody(ledger);
            ledger.Release();
            Reset(refs);
            Require(Methods.Count == 0 && MethodBindings == null && !PreparedMethods, "ordinary Reset retained a prior module registration");
        }
        finally
        {
            Methods.Clear(); Methods.AddRange(oldMethods); MethodBindings = oldBindings; PreparedMethods = oldPrepared;
            References = oldReferences;
        }
        Console.WriteLine("[owning method preparation] self-test OK (membership, source bounds, signatures, live source constraints and exact continuation)");
    }
}
