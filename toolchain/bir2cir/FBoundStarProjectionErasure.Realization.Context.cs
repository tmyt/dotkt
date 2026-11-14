using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    // The selected source row exists even when its proposed CLR slot collides
    // with an earlier slot. A proposal is metadata, not an owning declaration.
    internal sealed class SourceSlot
    {
        internal DeclarationBinding Original { get; }
        internal BirFunctionDeclaration Declaration => (BirFunctionDeclaration)Original.Source;
        internal BirFunctionDeclaration Slot { get; }
        internal TypeNode[] FrozenSignature { get; }
        internal SourceReferenceContract ReferenceContract { get; set; }
        internal BirFunctionDeclaration EmittedSlot { get; set; }
        internal DeclarationBinding EmittedBinding { get; set; }

        internal SourceSlot(DeclarationBinding original, BirFunctionDeclaration slot, TypeNode[] signature)
        {
            Original = original; Slot = slot;
            FrozenSignature = signature;
        }
    }

    internal sealed record SourceReferenceContract(TypeNode.Fqn Owner, BirFunctionDeclaration Slot, string Invoke)
    {
        internal DeclarationBinding SlotBinding { get; init; }
    }

    internal sealed record SourceInnerFactory(Owner Outer, Owner Inner, DeclarationBinding Constructor,
        BirFunctionDeclaration Slot, BirFunctionDeclaration Bridge, int[] TypeArgs)
    {
        internal SourceReferenceContract ReferenceContract { get; set; }
        internal DeclarationBinding SlotBinding { get; set; }
        internal DeclarationBinding BridgeBinding { get; set; }
    }

    internal sealed class SourceCarrierDeclarations
    {
        readonly Dictionary<Owner, Dictionary<DeclarationBinding, SourceSlot>> slots = new();
        readonly Dictionary<Owner, Dictionary<DeclarationBinding, SourceInnerFactory>> factories = new();

        internal IReadOnlyDictionary<DeclarationBinding, SourceSlot> Slots(Owner owner) =>
            MutableSlots(owner);
        internal IReadOnlyDictionary<DeclarationBinding, SourceInnerFactory> InnerFactories(Owner owner) =>
            MutableFactories(owner);

        Dictionary<DeclarationBinding, SourceSlot> MutableSlots(Owner owner)
        {
            if (!slots.TryGetValue(owner, out var rows)) slots.Add(owner, rows = new());
            return rows;
        }
        Dictionary<DeclarationBinding, SourceInnerFactory> MutableFactories(Owner owner)
        {
            if (!factories.TryGetValue(owner, out var rows)) factories.Add(owner, rows = new());
            return rows;
        }
        internal void Add(Owner owner, SourceSlot row) => MutableSlots(owner).Add(row.Original, row);
        internal void Add(Owner owner, SourceInnerFactory row) => MutableFactories(owner).Add(row.Constructor, row);
        internal void ReleaseSourceRows() { slots.Clear(); factories.Clear(); }
    }

    internal sealed class CarrierRealizationContext
    {
        internal CarrierSurfacePreparation Surface { get; }
        internal CarrierDemandPlan Demand => Surface.Demand;
        internal CarrierProjectionContext Projection { get; }
        internal CurrentDeclarationPreparation Preparation => Demand.Preparation;
        internal CurrentDeclarationBindings Bindings => Preparation.Bindings;
        internal ReferenceMetadataIndex References { get; }
        internal NativeProofPlan NativeProofs => Demand.NativeProofs;
        internal GenericRepresentationPolicy Values => Demand.Values;
        internal IReadOnlyDictionary<string, Owner> Owners => Demand.Owners;
        internal IReadOnlyDictionary<string, DeclarationBinding> SourceDefinitions => Bindings.SourceDefinitions;
        internal IReadOnlyDictionary<string, string> Aliases { get; }
        internal HashSet<string> ReservedNames { get; }
        internal SourceCarrierDeclarations Declarations { get; } = new();
        readonly Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> materializedMethods;
        readonly Action<BirFileDeclaration, IBirTypeDeclaration> materializedType;

        internal CarrierRealizationContext(CarrierSurfacePreparation surface, CarrierProjectionContext projection,
            ReferenceMetadataIndex references, HashSet<string> reservedNames,
            Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> materializedMethods = null,
            Action<BirFileDeclaration, IBirTypeDeclaration> materializedType = null)
        {
            Surface = surface; Projection = projection; References = references;
            ReservedNames = reservedNames;
            var aliases = new Dictionary<string, string>(references.Aliases, StringComparer.Ordinal);
            foreach (var alias in Preparation.LocalClrAliases) aliases[alias.Key] = alias.Value;
            Aliases = aliases;
            this.materializedMethods = materializedMethods;
            this.materializedType = materializedType;
        }

        internal IBirTypeDeclaration Definition(Owner owner) => (IBirTypeDeclaration)owner.DefinitionBinding.Source;
        internal BirFileDeclaration Root(Owner owner) => (BirFileDeclaration)owner.RootBinding.Source;

        internal void AttachMethod(IBirTypeDeclaration owner, BirFunctionDeclaration method,
            BirFunctionDeclaration proofSource = null)
        {
            owner.Methods = owner.Methods.OrEmpty().Append(method).ToArray();
            Bindings.GetSource(method);
            if (proofSource != null) NativeProofs.TransferIndependentMethodProofs(proofSource, method);
            foreach (var implementation in method.ClrInterfaceImpls.OrEmpty())
            {
                Bindings.GetSource(implementation);
                if (proofSource != null) NativeProofs.TransferIndependentMethodProofs(proofSource, implementation);
            }
            materializedMethods?.Invoke(owner, new[] { method });
        }

        internal void AttachType(BirFileDeclaration root, IBirTypeDeclaration type)
        {
            root.Types = root.Types.OrEmpty().Append(type).ToArray();
            Bindings.GetSource((BirGraphValue)type);
            foreach (var method in type.Methods.OrEmpty())
            {
                Bindings.GetSource(method);
                foreach (var implementation in method.ClrInterfaceImpls.OrEmpty()) Bindings.GetSource(implementation);
            }
            materializedType?.Invoke(root, type);
        }
    }
}
