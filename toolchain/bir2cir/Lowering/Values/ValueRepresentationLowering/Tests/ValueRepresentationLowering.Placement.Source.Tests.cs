using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    internal static void PlacementSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning declaration placement: " + message); }
        static void Reject(Action action, string message)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Owning declaration placement accepted " + message);
        }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var variable = new TypeNode.Tv("method", 0);
        var native = new TypeNode.Fqn("System.IDisposable");
        var ownNative = new TypeNode.Fqn("System.IComparable");
        BirFunctionDeclaration Ordinary(string name, string id, TypeNode result) => new() {
            Name = name, DeclarationId = id, Static = true, Ret = result,
            Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>(),
        };
        BirFunctionDeclaration Unchecked(string id)
        {
            var method = Ordinary(id, id, variable);
            method.TypeParams = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { native } } };
            method.Body = new IBirStatement[] { new BirReturn { Value = new BirCast {
                Type = variable, ReifiedTypeOperand = false,
                E = new BirConstant { Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                    Value = new BirLiteral.NullLiteral() },
            } } };
            return method;
        }
        (BirFileDeclaration File, BirClassDefinition Owner, BirFunctionDeclaration Moved,
            BirFunctionDeclaration Staying, BirFunctionDeclaration Twin, GenericRepresentationPolicy Generic,
            ValueRepresentationLowering Values, UncheckedGenericCastReturnErasure.PendingSourceIndex Pending,
            CurrentDeclarationBindings Bindings, FBoundStarProjectionErasure.NativeProofPlan Proofs,
            SuspendColdLowering.SourcePipelineTransitions Transitions) Fixture()
        {
            var prefix = Ordinary("liftedTwin", "same-source", new TypeNode.Fqn("kotlin.Unit"));
            prefix.Generated = true; prefix.SemanticOwner = "MoveOwner";
            var moved = Unchecked("moved"); moved.Generated = true; moved.SemanticOwner = "MoveOwner";
            var staying = Unchecked("staying");
            var twin = Ordinary("declaredTwin", "same-source", new TypeNode.Fqn("kotlin.String"));
            var outer = new BirClassDefinition { Name = "Outer", Kind = "class",
                TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { native } } } };
            var owner = new BirClassDefinition { Name = "MoveOwner", Kind = "class", SemanticOwner = "Outer",
                OuterTypeParamCount = 1, TypeParams = new[] {
                    new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { native } },
                    new BirTypeParameter { Name = "U", Constraints = new TypeNode[] { ownNative } },
                }, Methods = new[] { twin }, Fields = new[] {
                    new BirFieldDeclaration { Name = "native", Type = new TypeNode.Tv("type", 0),
                        Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } },
                } };
            var tail = Ordinary("tail", "tail", new TypeNode.Fqn("kotlin.Unit"));
            var file = new BirFileDeclaration { FileClass = "MovesFile", Methods = new[] { prefix, moved, staying, tail },
                Types = new IBirTypeDeclaration[] { outer, owner } };
            var roots = new[] { file };
            var generic = GenericRepresentationPolicy.CreateSource(roots, new Dictionary<string, string>(), references);
            var values = CreateSource(roots, references, false, new ValueRepresentationSourcePolicy(roots, references, false),
                CaptureSource(roots), generic);
            var pending = UncheckedGenericCastReturnErasure.CaptureSource(roots);
            Require(pending.SourceDeclarations.ByIdentity.Count == 2, "fixture did not capture actual unchecked method facts");
            UncheckedGenericCastReturnErasure.Apply(file, pending);
            generic.PrepareSourceLocations(roots); generic.RefreshPhysicalNativeLocations(roots);
            var bindings = new CurrentDeclarationBindings(roots);
            var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, references, generic);
            proofs.Add(bindings.GetCapturedSource(owner), 0, native);
            proofs.Add(bindings.GetCapturedSource(owner), 1, ownNative);
            var transitions = new SuspendColdLowering.SourcePipelineTransitions(roots, bindings, proofs, generic, null,
                values.RetireSourceDeclaration, Array.Empty<BirFunctionDeclaration>());
            return (file, owner, moved, staying, twin, generic, values, pending, bindings, proofs, transitions);
        }

        var actual = Fixture();
        var ownerBinding = actual.Bindings.GetCapturedSource(actual.Owner);
        var originalSelectedMethod = actual.Bindings.SourceLocalDeclarations[("MovesFile", "moved")];
        TypeOwnershipLowering.ApplyAll(new[] { actual.File }, actual.Transitions.MoveMethods);
        Require(actual.File.Methods.Value is [var only, _] && ReferenceEquals(only, actual.Staying)
            && actual.Owner.Methods.Value.Length == 3 && ReferenceEquals(actual.Owner.Methods.Value[1], actual.Moved)
            && actual.Owner.CapturedTypeParams.Value.Length == 1 && actual.Owner.TypeParams.Value.Length == 1,
            "actual MOVE or captured-prefix split did not occur");
        // Lexical access runs after placement and can append an actual accessor
        // to either the remaining facade or a MOVE destination. Its explicit
        // publication extends only that current tail, not original membership.
        var facadeAccessor = Ordinary("facadeAccessor", "facade-accessor", new TypeNode.Fqn("kotlin.Unit"));
        var ownerAccessor = Ordinary("ownerAccessor", "owner-accessor", new TypeNode.Fqn("kotlin.Unit"));
        actual.File.Methods = actual.File.Methods.Value.Append(facadeAccessor).ToArray();
        actual.Transitions.PublishMethods(actual.File, null, new[] { facadeAccessor });
        actual.Owner.Methods = actual.Owner.Methods.Value.Append(ownerAccessor).ToArray();
        actual.Transitions.PublishMethods(actual.File, actual.Owner, new[] { ownerAccessor });
        Reject(() => actual.Transitions.PublishMethods(actual.File, actual.Owner, new[] { ownerAccessor }),
            "a duplicate publication after placement");
        var movedFact = actual.Pending.SourceDeclarations.ByIdentity["moved"];
        var stayingFact = actual.Pending.SourceDeclarations.ByIdentity["staying"];
        actual.Transitions.Projection.SealSourceContinuations();
        actual.Generic.CompleteSourceContinuation(new[] { actual.File }, null, actual.Transitions.Projection);
        actual.Values.CompleteSourceContinuation(new[] { actual.File }, null, actual.Transitions.Projection);
        UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(actual.Pending, new[] { actual.File },
            null, actual.Transitions.Projection);
        Require(actual.Transitions.Projection.IsPublished(facadeAccessor) && actual.Transitions.Projection.IsPublished(ownerAccessor)
            && !actual.Bindings.SourceLocalDeclarations.ContainsKey(("MovesFile", "facade-accessor"))
            && !actual.Bindings.SourceLocalDeclarations.ContainsKey(("MoveOwner", "owner-accessor")),
            "post-placement publication recaptured source eligibility or lost actual generated methods");
        Require(movedFact.SourceMethod == null && stayingFact.SourceMethod == null
            && ReferenceEquals(actual.Owner.Methods.Value[1], actual.Moved)
            && ReferenceEquals(actual.File.Methods.Value[0], actual.Staying)
            && movedFact.Owner == "MovesFile" && movedFact.TopLevel && movedFact.Return == variable
            && ReferenceEquals(actual.Bindings.SourceLocalDeclarations[("MovesFile", "moved")], originalSelectedMethod),
            "MOVE reselected a source declaration, changed its semantic frame or lost the preceding-removal shift");
        Require(ReferenceEquals(actual.Values.locals["same-source"].Method.SourceOccurrence, actual.Twin)
            && !ReferenceEquals(actual.Values.locals["same-source"].Method.SourceOccurrence, actual.Owner.Methods.Value[2])
            && actual.Values.locals["same-source"].Owner == "MoveOwner",
            "physical traversal order paired the wrong repeated-ID source occurrence");
        Require(ReferenceEquals(ownerBinding.Source, actual.Owner)
            && !actual.Proofs.Bounds(ownerBinding, 0).Any() && !actual.Proofs.Bounds(ownerBinding, 1).Any()
            && actual.Proofs.IsPhysicalConstraint(ownerBinding, 0, native)
            && actual.Proofs.IsPhysicalConstraint(ownerBinding, 1, ownNative)
            && !actual.Proofs.IsPhysicalConstraint(ownerBinding, 1, native)
            && actual.Generic.IsNativeField(actual.Owner.Fields.Value[0]),
            "captured-prefix placement replaced the native declaration/proof/field authority");
        actual.Bindings.CompleteSourceCustody(actual.Transitions.Projection);
        actual.Transitions.Release();

        var missing = Fixture();
        TypeOwnershipLowering.ApplyAll(new[] { missing.File }, (_, _) => { });
        Reject(() => {
            missing.Transitions.Projection.SealSourceContinuations();
            UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(missing.Pending, new[] { missing.File },
                null, missing.Transitions.Projection);
        }, "an unreported MOVE");

        var wrongTarget = Fixture();
        Reject(() => TypeOwnershipLowering.ApplyAll(new[] { wrongTarget.File }, (file, moves) =>
            wrongTarget.Transitions.MoveMethods(file, moves.Select(move => move with {
                DestinationOrdinal = move.DestinationOrdinal + 1,
            }).ToArray())), "a forged destination ordinal");

        var replaced = Fixture();
        TypeOwnershipLowering.ApplyAll(new[] { replaced.File }, replaced.Transitions.MoveMethods);
        replaced.Owner.Methods.Value[1] = (BirFunctionDeclaration)replaced.Moved.DeepClone();
        Reject(replaced.Transitions.Projection.SealSourceContinuations, "a copied-ID replacement after MOVE");
        var reordered = Fixture();
        TypeOwnershipLowering.ApplyAll(new[] { reordered.File }, reordered.Transitions.MoveMethods);
        reordered.File.Methods = reordered.File.Methods.Value.Reverse().ToArray();
        Reject(reordered.Transitions.Projection.SealSourceContinuations, "an unreported retained-vector reorder");
        var unpublished = Fixture();
        TypeOwnershipLowering.ApplyAll(new[] { unpublished.File }, unpublished.Transitions.MoveMethods);
        unpublished.Owner.Methods = unpublished.Owner.Methods.Value
            .Append(Ordinary("unreported", "unreported", new TypeNode.Fqn("kotlin.Unit"))).ToArray();
        Reject(unpublished.Transitions.Projection.SealSourceContinuations, "an unreported destination append after MOVE");
        Console.WriteLine("[owning placement continuation] self-test OK (actual MOVE, original semantic/native frames, repeated IDs and strict custody)");
    }
}
