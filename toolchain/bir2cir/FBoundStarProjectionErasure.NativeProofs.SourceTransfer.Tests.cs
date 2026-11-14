using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void NativeProofSourceTransferSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning native proof transfer: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Native proof transfer accepted an uncaptured or consumed source");
        }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var disposable = new TypeNode.Fqn("System.IDisposable");
        var independent = new TypeNode.Fqn("System.Collections.Generic.IComparer`1", new TypeNode[] { methodVariable });
        var dependent = new TypeNode.Fqn("System.Collections.Generic.IComparer`1", new TypeNode[] { ownerVariable });
        BirTypeParameter Parameter(string name) => new() { Name = name, Constraints = Array.Empty<TypeNode>() };
        BirFunctionDeclaration Method(string name, int arity = 3) => new() {
            Name = name, DeclarationId = name, Params = Array.Empty<BirParameter>(),
            TypeParams = Enumerable.Range(0, arity).Select(index => Parameter("T" + index)).ToArray(),
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>(),
        };
        var source = Method("source");
        var privateSource = Method("privateSource");
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class",
            TypeParams = new[] { Parameter("OwnerT") }, Methods = new[] { source, privateSource } };
        var root = new BirFileDeclaration { FileClass = "Transfers", Types = new IBirTypeDeclaration[] { owner } };
        var roots = new[] { root };
        var values = GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs);
        var bindings = PrepareCurrentDeclarations(roots, values);
        var plan = PrepareNativeProofs(bindings, refs, values);
        var original = bindings.GetCapturedSource(source);
        var localDeclarations = bindings.SourceLocalDeclarations;
        var localMethods = bindings.SourceLocalMethods;
        var originalCount = localDeclarations.Count;
        Require(plan.Add(original, 0, disposable) && plan.Add(original, 1, independent)
            && plan.Add(original, 1, dependent) && plan.Add(original, 2, disposable),
            "source proof fixture did not populate its exact ordinary parameter positions");
        Require(ReferenceEquals(plan.LocalSourceMethod("source"), source)
            && ReferenceEquals(plan.LocalSourceMethodBinding("source"), original)
            && plan.LocalSourceMethodBinding("unknown") == null
            && plan.ExactMethodArgument(original, "source", 0)
            && plan.IsPhysicalConstraint(original, 0, disposable)
            && !plan.IsPhysicalConstraint(original, 0, dependent),
            "source queries lost their actual captured method or shared proof rows");

        var untouched = Method("unadmitted");
        Reject(() => plan.TransferIndependentMethodProofs(source with { }, untouched));
        Reject(() => bindings.GetCapturedSource(untouched));
        var projection = Method("projection");
        projection.NullableRepresentationFrame = new NullableRepresentationFrame(2, Array.Empty<int>(), storageIndices: new[] { 1 });
        var shortProjection = Method("shortProjection", 1);
        var privateProjection = Method("privateProjection");
        // Admit descriptors only once their exact objects are attached to an
        // emitted method. Their arity is their own, not the bridge's frame.
        BirMethodImpl Descriptor(int arity) => new() {
            Owner = new TypeNode.Fqn("Contract"), Member = "slot", Arity = arity,
            Params = Array.Empty<TypeNode>(), Ret = new TypeNode.Fqn("kotlin.Unit"),
            TypeParams = Enumerable.Range(0, arity).Select(index => Parameter("D" + index)).ToArray(),
        };
        var descriptor = Descriptor(3);
        var shortDescriptor = Descriptor(1);
        projection.ClrInterfaceImpls = new[] { descriptor, shortDescriptor };
        owner.Methods = owner.Methods.Value.Concat(new[] { projection, shortProjection, privateProjection }).ToArray();
        Reject(() => plan.TransferIndependentMethodProofs(source with { }, descriptor));
        Reject(() => bindings.GetCapturedSource(descriptor));
        plan.TransferIndependentMethodProofs(source, projection);
        plan.TransferIndependentMethodProofs(source, shortProjection);
        plan.TransferIndependentMethodProofs(privateSource, privateProjection);
        plan.TransferIndependentMethodProofs(source, descriptor);
        plan.TransferIndependentMethodProofs(source, shortDescriptor);
        var projected = bindings.GetCapturedSource(projection);
        var shortBinding = bindings.GetCapturedSource(shortProjection);
        var privateBinding = bindings.GetCapturedSource(privateProjection);
        var descriptorBinding = bindings.GetCapturedSource(descriptor);
        var shortDescriptorBinding = bindings.GetCapturedSource(shortDescriptor);
        Require(plan.Bounds(projected, 0).Contains(disposable)
            && plan.Bounds(projected, 1).Single() is TypeNode.Fqn {
                Name: "System.Collections.Generic.IComparer`1", Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            && !plan.Bounds(projected, 2).Any(),
            "independent proof, owner-variable exclusion or nullable storage-companion restriction changed");
        Require(plan.Bounds(shortBinding, 0).Contains(disposable) && !plan.Bounds(shortBinding, 1).Any()
            && !plan.Bounds(shortBinding, 2).Any() && !plan.Bounds(privateBinding, 1).Any(),
            "target arity was ignored or a separate private source borrowed another method's proofs");
        Require(plan.Bounds(descriptorBinding, 0).Contains(disposable)
            && plan.Bounds(descriptorBinding, 1).Single() is TypeNode.Fqn {
                Name: "System.Collections.Generic.IComparer`1", Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            && plan.Bounds(descriptorBinding, 2).Contains(disposable)
            && plan.Bounds(shortDescriptorBinding, 0).Contains(disposable)
            && !plan.Bounds(shortDescriptorBinding, 1).Any(),
            "attached MethodImpl lost independent proofs, borrowed the bridge storage frame or ignored its own arity");
        var originalProof = plan.Bounds(original, 1).OfType<TypeNode.Fqn>()
            .Single(proof => proof.Args is [TypeNode.Tv { Scope: "method" }]);
        var copiedProof = (TypeNode.Fqn)plan.Bounds(projected, 1).Single();
        Require(!ReferenceEquals(originalProof.Args, copiedProof.Args), "proof transfer shared a mutable argument vector");
        originalProof.Args[0] = new TypeNode.Fqn("System.String");
        Require(copiedProof.Args[0] == methodVariable, "source proof mutation changed its authored projection");
        Require(((TypeNode.Fqn)plan.Bounds(descriptorBinding, 1).Single()).Args[0] == methodVariable,
            "source proof mutation changed its attached MethodImpl projection");
        Require(ReferenceEquals(bindings.SourceLocalDeclarations, localDeclarations)
            && ReferenceEquals(bindings.SourceLocalMethods, localMethods) && localDeclarations.Count == originalCount
            && !localDeclarations.ContainsKey(("Owner", "projection")) && !localMethods.ContainsKey("Owner\0projection"),
            "physical projection admission rebuilt or extended the original selection catalog");

        // The observer sees the actual emitted methods and their attached
        // descriptors, not same-name reconstructions at a missing-node check.
        var physicalRoot = root.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        Reject(() => plan.LocalSourceMethod("source"));
        var physicalOwner = physicalRoot["types"][0].AsObject();
        var physicalProjection = physicalOwner["methods"][2].AsObject();
        Require(ReferenceEquals(projected.Physical, physicalProjection)
            && ReferenceEquals(plan.LocalMethod("projection"), physicalProjection)
            && plan.IsPhysicalConstraint(physicalProjection["typeParams"][1]["constraints"].AsArray(), independent)
            && !plan.IsPhysicalConstraint(physicalProjection["typeParams"][1]["constraints"].AsArray(), dependent)
            && !plan.IsPhysicalConstraint(physicalProjection["typeParams"][2]["constraints"].AsArray(), disposable),
            "the single frontier lost generated identity, proof rows or exact companion positions");
        var physicalDescriptor = physicalProjection["clrInterfaceImpls"][0].AsObject();
        Require(ReferenceEquals(descriptorBinding.Physical, physicalDescriptor)
            && plan.IsPhysicalConstraint(physicalDescriptor["typeParams"][1]["constraints"].AsArray(), independent)
            && !plan.IsPhysicalConstraint(physicalDescriptor["typeParams"][1]["constraints"].AsArray(), dependent)
            && plan.IsPhysicalConstraint(physicalDescriptor["typeParams"][2]["constraints"].AsArray(), disposable)
            && ReferenceEquals(plan.LocalMethod("projection"), physicalProjection),
            "attached MethodImpl lost the same proof rows at the frontier or replaced the method identity catalog");
        Reject(() => plan.TransferIndependentMethodProofs(source, Method("afterFrontier")));

        var late = Method("late").ToDom();
        physicalOwner["methods"].AsArray().Add(late);
        plan.TransferIndependentMethodProofs(physicalProjection, late);
        Require(plan.IsPhysicalConstraint(late["typeParams"][1]["constraints"].AsArray(), independent)
            && !plan.IsPhysicalConstraint(late["typeParams"][1]["constraints"].AsArray(), dependent)
            && ReferenceEquals(plan.LocalMethod("late"), late),
            "actual late DOM transfer did not consume the same bound proof rows and generated identity");
        Console.WriteLine("[owning native proof transfer] self-test OK (captured source, emitted methods/attached MethodImpls, arity/frame restrictions, copies and one frontier)");
    }
}
