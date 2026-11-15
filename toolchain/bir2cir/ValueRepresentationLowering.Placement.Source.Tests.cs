using System.Text.Json.Nodes;
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
            generic.PrepareDomLocations(roots); generic.RefreshPhysicalNativeLocations(roots);
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
        var dom = actual.File.ToDom(actual.Transitions.Projection.ObserveProjection);
        actual.Transitions.Projection.ProjectRetiredForests();
        actual.Bindings.BindProjected();
        var retired = actual.Transitions.Projection.RetiredMethods;
        actual.Generic.BindDomDeclarations(new[] { dom }, declarations: actual.Transitions.Projection);
        actual.Values.BindDomDeclarations(new[] { dom }, retired, actual.Transitions.Projection);
        var uncheckedIndex = UncheckedGenericCastReturnErasure.BindDomDeclarations(actual.Pending, new[] { dom },
            retired, declarations: actual.Transitions.Projection);
        Require(ReferenceEquals(uncheckedIndex.ByIdentity["moved"].Method, dom["types"][1]["methods"][1])
            && ReferenceEquals(uncheckedIndex.ByIdentity["staying"].Method, dom["methods"][0])
            && uncheckedIndex.ByIdentity["moved"].Owner == "MovesFile" && uncheckedIndex.ByIdentity["moved"].TopLevel
            && uncheckedIndex.ByIdentity["moved"].Return == variable
            && ReferenceEquals(actual.Bindings.SourceLocalDeclarations[("MovesFile", "moved")], originalSelectedMethod),
            "MOVE reselected a source declaration, changed its semantic frame or lost the preceding-removal shift");
        Require(ReferenceEquals(actual.Values.locals["same-source"].Method.Dom, dom["types"][1]["methods"][0])
            && !ReferenceEquals(actual.Values.locals["same-source"].Method.Dom, dom["types"][1]["methods"][2])
            && actual.Values.locals["same-source"].Owner == "MoveOwner",
            "physical traversal order paired the wrong repeated-ID source occurrence");
        Require(ReferenceEquals(actual.Bindings.GetPhysical(dom["types"][1].AsObject()), ownerBinding)
            && !actual.Proofs.Bounds(ownerBinding, 0).Any() && !actual.Proofs.Bounds(ownerBinding, 1).Any()
            && actual.Proofs.IsPhysicalConstraint(dom["types"][1]["capturedTypeParams"][0]["constraints"].AsArray(), native)
            && actual.Proofs.IsPhysicalConstraint(dom["types"][1]["typeParams"][0]["constraints"].AsArray(), ownNative)
            && !actual.Proofs.IsPhysicalConstraint(dom["types"][1]["typeParams"][0]["constraints"].AsArray(), native)
            && actual.Generic.IsNativeField(dom["types"][1]["fields"][0].AsObject()),
            "captured-prefix placement replaced the native declaration/proof/field authority");
        actual.Transitions.Release();

        var missing = Fixture();
        TypeOwnershipLowering.ApplyAll(new[] { missing.File }, (_, _) => { });
        var missingDom = missing.File.ToDom(missing.Transitions.Projection.ObserveProjection);
        missing.Transitions.Projection.ProjectRetiredForests();
        Reject(() => UncheckedGenericCastReturnErasure.BindDomDeclarations(missing.Pending, new[] { missingDom },
            missing.Transitions.Projection.RetiredMethods, declarations: missing.Transitions.Projection), "an unreported MOVE");

        var wrongTarget = Fixture();
        Reject(() => TypeOwnershipLowering.ApplyAll(new[] { wrongTarget.File }, (file, moves) =>
            wrongTarget.Transitions.MoveMethods(file, moves.Select(move => move with {
                DestinationOrdinal = move.DestinationOrdinal + 1,
            }).ToArray())), "a forged destination ordinal");

        var replaced = Fixture();
        TypeOwnershipLowering.ApplyAll(new[] { replaced.File }, replaced.Transitions.MoveMethods);
        replaced.Owner.Methods.Value[1] = (BirFunctionDeclaration)replaced.Moved.DeepClone();
        _ = replaced.File.ToDom(replaced.Transitions.Projection.ObserveProjection);
        Reject(replaced.Transitions.Projection.ProjectRetiredForests, "a copied-ID replacement after MOVE");
        var reordered = Fixture();
        TypeOwnershipLowering.ApplyAll(new[] { reordered.File }, reordered.Transitions.MoveMethods);
        reordered.File.Methods = reordered.File.Methods.Value.Reverse().ToArray();
        _ = reordered.File.ToDom(reordered.Transitions.Projection.ObserveProjection);
        Reject(reordered.Transitions.Projection.ProjectRetiredForests, "an unreported retained-vector reorder");
        Console.WriteLine("[owning placement continuation] self-test OK (actual MOVE, original semantic/native frames, repeated IDs and strict addresses)");
    }
}
