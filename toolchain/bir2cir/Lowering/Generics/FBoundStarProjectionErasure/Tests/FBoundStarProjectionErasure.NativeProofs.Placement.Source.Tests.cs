using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static void NativeProofPlacementSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Native proof placement frame: " + message); }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var capturedBound = new TypeNode.Fqn("System.IDisposable");
        var ownBound = new TypeNode.Fqn("System.ICloneable");
        var captured = new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { capturedBound } };
        var own = new BirTypeParameter { Name = "U", Constraints = new TypeNode[] { ownBound } };
        var outer = new BirClassDefinition { Name = "Outer", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { capturedBound } } } };
        var inner = new BirClassDefinition { Name = "Outer.Inner", Kind = "class", SemanticOwner = "Outer",
            OuterTypeParamCount = 1, TypeParams = new[] { captured, own } };
        var root = new BirFileDeclaration { FileClass = "NativeLayout", Types = new IBirTypeDeclaration[] { outer, inner } };
        var roots = new[] { root };
        var values = GenericRepresentationPolicy.CreateSource(roots, references.Aliases, references);
        var bindings = PrepareCurrentDeclarations(roots, values);
        var proofs = PrepareNativeProofs(bindings, references, values);
        var original = bindings.GetSource(inner);
        Require(proofs.IsPhysicalConstraint(original, 0, capturedBound)
            && !proofs.IsPhysicalConstraint(original, 0, ownBound)
            && proofs.IsPhysicalConstraint(original, 1, ownBound)
            && !proofs.IsPhysicalConstraint(original, 1, capturedBound)
            && !proofs.Bounds(original, 0).Any() && !proofs.Bounds(original, 1).Any(),
            "initial native rows lost distinct physical slots or repeated retained source bounds");

        TypeOwnershipLowering.ApplyAll(roots, (_, _) => throw new InvalidOperationException("No method moved in layout-only test"));
        Require(inner.NestedIn.Value == "Outer" && inner.CapturedTypeParams.Value.Length == 1
            && ReferenceEquals(inner.TypeParams.Value.Single(), own)
            && !proofs.Bounds(original, 0).Any() && !proofs.Bounds(original, 1).Any(),
            "split source layout changed the original proof frame or its retained-bound suppression");
        var ownConstraints = own.Constraints;
        own.Constraints = default;
        Require(proofs.Bounds(original, 1).SequenceEqual(new[] { ownBound }) && !proofs.Bounds(original, 0).Any(),
            "source queries did not read the live own constraint at its captured-prefix offset");
        own.Constraints = ownConstraints;

        Require(ReferenceEquals(original.Source, inner)
            && proofs.IsPhysicalConstraint(original, 0, capturedBound)
            && !proofs.IsPhysicalConstraint(original, 0, ownBound)
            && proofs.IsPhysicalConstraint(original, 1, ownBound)
            && !proofs.IsPhysicalConstraint(original, 1, capturedBound),
            "current physical queries rebound proof rows or failed to offset the own declaration frame");
        var ledger = new SourceDeclarationProjection(roots, bindings);
        ledger.SealSourceContinuations();
        bindings.CompleteSourceCustody(ledger);
        Require(bindings.IsBound, "physical placement did not complete its exact source custody");
        ledger.Release();
    }
}
