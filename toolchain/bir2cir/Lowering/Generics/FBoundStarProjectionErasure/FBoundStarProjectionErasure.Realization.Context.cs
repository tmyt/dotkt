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
        internal Dictionary<string, (string Owner, string Name, ProjectedConstructorPlan Plan)> ProjectedConstructors { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, (string Owner, string Name, int Arity, TypeNode[] Parameters, TypeNode Result)> ProjectedInnerFactories { get; } = new(StringComparer.Ordinal);
        readonly Dictionary<Owner, Dictionary<DeclarationBinding, SourceSlot>> slots = new();
        readonly Dictionary<Owner, Dictionary<DeclarationBinding, SourceInnerFactory>> factories = new();
        internal Dictionary<string, string> PhysicalDeclarationSources { get; } = new(StringComparer.Ordinal);

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
        internal void Add(Owner owner, SourceSlot row)
        {
            MutableSlots(owner).Add(row.Original, row);
            if (owner.Needed && row.Declaration.DeclarationId.OrDefault() is string original
                && row.Slot.DeclarationId.OrDefault() is string physical)
                PhysicalDeclarationSources[physical] = original;
        }
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
        internal SourceCarrierDeclarations Declarations { get; } = new();
        internal List<ProjectedStorageOwner> ProjectedStorage { get; } = new();
        // A proven constructed CLR MethodSpec argument is physical, even when
        // the same spelling in an ordinary Kotlin value slot requires erasure.
        internal Dictionary<BirGraphValue, Dictionary<int, TypeNode>> MethodWitnesses { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<BirGraphValue, (string Source, string Slot)> MethodSlotSelections { get; } = new(ReferenceEqualityComparer.Instance);
        readonly Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> materializedMethods;
        readonly Action<BirFileDeclaration, IBirTypeDeclaration> materializedType;

        internal CarrierRealizationContext(CarrierSurfacePreparation surface, CarrierProjectionContext projection,
            ReferenceMetadataIndex references,
            Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> materializedMethods = null,
            Action<BirFileDeclaration, IBirTypeDeclaration> materializedType = null)
        {
            Surface = surface; Projection = projection; References = references;
            var aliases = new Dictionary<string, string>(references.Aliases, StringComparer.Ordinal);
            foreach (var alias in Preparation.LocalClrAliases) aliases[alias.Key] = alias.Value;
            Aliases = aliases;
            this.materializedMethods = materializedMethods;
            this.materializedType = materializedType;
        }

        internal IBirTypeDeclaration Definition(Owner owner) => (IBirTypeDeclaration)owner.DefinitionBinding.Source;
        internal BirFileDeclaration Root(Owner owner) => (BirFileDeclaration)owner.RootBinding.Source;

        readonly Dictionary<string, string> erasedScopes = new(StringComparer.Ordinal);

        // An existential TypeDef has no enclosing generic parameters to repeat. At module
        // scope its full name distinguishes it; under a real enclosing type CLR nesting
        // uses only the simple name, so retain a distinct zero-frame scope for each owner.
        internal string ErasedEnclosingScope(BirFileDeclaration root, IBirTypeDeclaration type)
        {
            var parent = type.NestedIn.OrDefault() ?? type.SemanticOwner.OrDefault();
            if (parent == null) return null;
            if (parent == root.FileClass.OrDefault()) return parent;
            var declaration = (IBirTypeDeclaration)SourceDefinitions[parent].Source;
            if (TypeParameterFrame.Count(declaration) == 0) return parent;
            if (erasedScopes.TryGetValue(parent, out var scope)) return scope;
            var outer = ErasedEnclosingScope(root, declaration);
            if (outer == null) return null;
            scope = Demand.AllocateTypeName(parent + "$dotkt_erased_scope");
            erasedScopes.Add(parent, scope);
            AttachType(root, new BirClassDefinition {
                Name = scope, Kind = "class", Abstract = true, Final = true, Generated = true,
                NestedIn = outer, Vis = VisibilityInScope(declaration, outer) ?? "public",
                Base = OptionalField<TypeNode>.Null, Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<BirFieldDeclaration>(),
                Ctors = Array.Empty<BirConstructor>(), Methods = Array.Empty<BirFunctionDeclaration>(),
                Properties = Array.Empty<BirPropertyDeclaration>(), Attrs = Array.Empty<BirAttribute>(),
            });
            return scope;
        }

        internal static string VisibilityInScope(IBirTypeDeclaration source, string scope)
        {
            var visibility = PublishedApiTypeVisibilityLowering.ResolveVisibility(source);
            if (scope == (source.NestedIn.OrDefault() ?? source.SemanticOwner.OrDefault())) return visibility;
            // The generated sibling is no longer a member of the source's declaring
            // class. Preserve access for its assembly and for external subclasses.
            return visibility switch { "private" => "internal", "protected" or "protectedInternal" => "public", _ => visibility };
        }

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
