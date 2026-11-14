using System.Text.Json.Nodes;
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
        var oldClassifier = PhysicalClassifier;
        var oldProofs = NativeProofs;
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
            var bindings = new CurrentDeclarationBindings(new[] { root });
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
            Reject(() => ConfigurePreparedMethods(bindings, refs, _ => null));

            // This mutation is actual accessor authoring, not another method
            // inventory. Source signatures must include these new ID-less rows.
            var accessors = FBoundStarProjectionErasure.AddFieldAccessorsSource("Owner", 1, definition, bindings);
            var signatures = FBoundStarProjectionErasure.CaptureSourceSignatures(bindings);
            Require(Methods.Count == 6 && accessors.Count == 2 && bindings.SourceLocalDeclarations.Count == 6,
                "accessor preparation widened the original registration/index membership");
            ownerBound.Params.Value[0].Type = new TypeNode.Fqn("AfterCapture");
            inputType.Args[0] = new TypeNode.Fqn("MutatedArgument");
            var projected = root.ToDom(bindings.ObserveProjection);
            bindings.BindProjected();
            var actualOwner = projected["types"][0].AsObject();
            var actualMethod = actualOwner["methods"][0].AsObject();
            var mappedSignatures = signatures.BindPhysical();
            Require(mappedSignatures.Count == definition.Methods.Value.Length
                && !mappedSignatures.ContainsKey(projected["methods"][0].AsObject())
                && mappedSignatures[actualMethod] is [TypeNode.Fqn { Name: "Box", Args: [TypeNode.Tv { Scope: "type", I: 0 }] }]
                && mappedSignatures[actualOwner["methods"][5].AsObject()].Length == 0
                && mappedSignatures[actualOwner["methods"][6].AsObject()] is [TypeNode.Fqn { Name: "Box", Args: [TypeNode.Tv { I: 0 }] }],
                "signature capture was late, aliased mutable type vectors, included facade methods or lost appended records");
            Reject(() => signatures.BindPhysical());
            var queried = new List<JsonObject>();
            ConfigurePreparedMethods(bindings, refs, _ => null, (method, _) => { queried.Add(method); return Array.Empty<TypeNode>(); });
            Require(Methods.Count == 6 && ReferenceEquals(Methods[0].Method, actualMethod)
                && ReferenceEquals(Methods[0].Owner, actualOwner), "physical configure rebuilt or cleared the registrations");
            actualMethod["typeParams"][0]["constraints"] = new JsonArray(TypeJson.Write(ownerVariable));
            Apply(FBoundStarProjectionErasure.ContainsOwnerTv, type => type);
            Require(actualMethod["typeParams"][0]["constraints"].AsArray().Count == 0
                && TypeJson.Read(actualMethod["typeParams"][0][FBoundStarProjectionErasure.ErasedInnerConstraintKey][0]) == ownerVariable
                && queried.Any(method => ReferenceEquals(method, actualMethod))
                && !queried.Any(method => ReferenceEquals(method, actualOwner["methods"][5])),
                "late consumers did not use the same live method/owner registrations");

            var late = new BirFunctionDeclaration { Name = "late", Generated = true,
                TypeParams = new[] { Parameter("T", ownerVariable) }, Params = Array.Empty<BirParameter>(),
                Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() }.ToDom();
            actualOwner["methods"].AsArray().Add(late);
            Record(late, actualOwner);
            Require(ReferenceEquals(Methods[^1].MethodBinding, bindings.GetPhysical(late)) && Methods.Count == 7,
                "late physical Record was not an explicit admission into the same authority");
            var latePrior = late[NullableGenericErasure.MethodTypeParameterBoundsPre].DeepClone();
            late["typeParams"][0]["constraints"] = new JsonArray(TypeJson.Fqn("System.IDisposable"));
            Record(late, actualOwner);
            Require(Methods.Count == 8 && JsonNode.DeepEquals(latePrior, late[NullableGenericErasure.MethodTypeParameterBoundsPre]),
                "late first-writer or duplicate registration semantics changed");
            Reset(refs, _ => null);
            Require(Methods.Count == 0 && MethodBindings == null && !PreparedMethods, "ordinary Reset retained a prior module registration");
        }
        finally
        {
            Methods.Clear(); Methods.AddRange(oldMethods); MethodBindings = oldBindings; PreparedMethods = oldPrepared;
            References = oldReferences; PhysicalClassifier = oldClassifier; NativeProofs = oldProofs;
        }
        Console.WriteLine("[owning method preparation] self-test OK (membership, source bounds, signatures, exact continuation and live late registration)");
    }
}
