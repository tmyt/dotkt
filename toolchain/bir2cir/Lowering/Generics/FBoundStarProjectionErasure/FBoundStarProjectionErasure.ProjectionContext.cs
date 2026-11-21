using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    sealed record ProjectionDeclaration(string Kind, bool SourceGeneric, string[] Variances,
        NullableRepresentationFrame Frame);

    // Declaration readers vary by owning phase; representation policy does
    // not. Current definitions and retained logical alias headers have
    // distinct, producer-owned membership and precedence.
    internal sealed class CarrierProjectionContext
    {
        readonly Func<string, ProjectionDeclaration> declaration;
        readonly CurrentPhysicalTypeView currentPhysical;
        readonly Dictionary<string, BirTypeParameter[]> referencedWitnessParameters = new(StringComparer.Ordinal);
        readonly Dictionary<string, BirTypeParameter[]> referencedMethodWitnessParameters = new(StringComparer.Ordinal);
        internal CurrentDeclarationPreparation Preparation { get; }
        internal IReadOnlyDictionary<string, Owner> Owners { get; }
        internal ReferenceMetadataIndex References { get; }
        internal GenericRepresentationPolicy Values { get; }
        internal NativeProofPlan NativeProofs { get; }
        internal IReadOnlyDictionary<string, string> LocalClrAliases { get; }

        CarrierProjectionContext(CurrentDeclarationPreparation preparation, IReadOnlyDictionary<string, Owner> owners,
            ReferenceMetadataIndex references, GenericRepresentationPolicy values, NativeProofPlan nativeProofs,
            IReadOnlyDictionary<string, string> aliases, Func<string, ProjectionDeclaration> declaration,
            CurrentPhysicalTypeView currentPhysical = null)
        {
            Preparation = preparation; Owners = owners; References = references;
            Values = values; NativeProofs = nativeProofs; LocalClrAliases = aliases;
            this.declaration = declaration;
            this.currentPhysical = currentPhysical;
        }

        internal static CarrierProjectionContext ForSource(CarrierDemandPlan demand, ReferenceMetadataIndex references,
            AliasDeclarationTransitions aliases = null)
        {
            var preparation = demand.Preparation;
            ProjectionDeclaration Read(string name)
            {
                if (preparation.Bindings.SourceDefinitions.TryGetValue(name, out var current))
                    return ReadSource(AliasDeclarationTransitions.ReadLogicalHeaderFacts((IBirTypeDeclaration)current.Source));
                return aliases != null && aliases.TryRetiredLogicalHeader(name, out var retired) ? ReadSource(retired) : null;
            }
            return new(preparation, demand.Owners, references, demand.Values, demand.NativeProofs,
                preparation.LocalClrAliases, Read);
        }


        internal static CarrierProjectionContext ForCurrentSource(CarrierRealizationContext realization,
            CurrentPhysicalTypeView current, AliasDeclarationTransitions aliases = null)
        {
            // Only the already allocated, needed names are exported by the
            // continuation. Their current physical records supply owner frames.
            var owners = realization.Owners.Values.Where(owner => owner.Needed).ToDictionary(owner => owner.Name,
                owner => current.Bindings.TryGetValue(owner.Name, out var binding)
                    ? new Owner(new GenericOwner(owner.Name, binding, null,
                        current.Definitions[owner.Name].TypeParams.OrEmpty().Length, true)) { ErasedName = owner.ErasedName }
                    : new Owner { Name = owner.Name, ErasedName = owner.ErasedName, Needed = true }, StringComparer.Ordinal);
            return new(realization.Preparation, owners, realization.References, realization.Values,
                realization.NativeProofs, current.LocalClrAliases, CurrentSourceDeclarations(realization, current, aliases), current);
        }

        // Collection helper signatures project referenced values, not local
        // carrier owners. Keep that scope explicit with the SAME source facts.
        internal static CarrierProjectionContext ForReferencedValuesSource(CarrierRealizationContext realization,
            CurrentPhysicalTypeView current, AliasDeclarationTransitions aliases = null) => new(
                realization.Preparation, new Dictionary<string, Owner>(StringComparer.Ordinal),
                realization.References, realization.Values, realization.NativeProofs, null,
                CurrentSourceDeclarations(realization, current, aliases));

        static Func<string, ProjectionDeclaration> CurrentSourceDeclarations(CarrierRealizationContext realization,
            CurrentPhysicalTypeView current, AliasDeclarationTransitions aliases)
        {
            var original = ForSource(realization.Demand, realization.References, aliases);
            return name => current.Definitions.TryGetValue(name, out var actual)
                ? ReadSource(AliasDeclarationTransitions.ReadLogicalHeaderFacts(actual))
                : original.LocalDeclaration(name);
        }

        static ProjectionDeclaration ReadSource(AliasDeclarationTransitions.LogicalHeaderFacts header) => new(
            header.Kind.OrDefault(), IsSourceGeneric(header.Name.OrDefault(), header.Generated.OrDefault(),
                header.Attrs.OrEmpty().Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" }),
                header.TypeParams.OrEmpty().Length),
            header.TypeParams.HasValue ? header.TypeParams.Value.Select(parameter => parameter.Variance.OrDefault()).ToArray() : null,
            header.KotlinSupertypesPre.OrDefault()?.NullableFrame);

        void RequireSourceClock()
        {
            if (Preparation?.Bindings.IsBound == true)
                throw new InvalidOperationException("Source carrier projection crossed its owning frontier");
        }

        ProjectionDeclaration LocalDeclaration(string name) => declaration(name);

        internal NullableRepresentationFrame OwnerFrame(Owner owner) => currentPhysical != null
            ? currentPhysical.Definitions.TryGetValue(owner.Name, out var actual)
                ? NullableRepresentationViews.TypeFacts((BirGraphValue)actual)?.NullableFrame : null
            : owner.DefinitionBinding is { } source
                ? NullableRepresentationViews.TypeFacts(source.Source)?.NullableFrame : null;

        internal TypeNode Rewrite(TypeNode type, bool boundDeclaration = false,
            bool preserveConstructedHead = false, bool preserveArrayShape = false)
        {
            RequireSourceClock();
            return RewriteType(type, this, boundDeclaration, preserveConstructedHead, preserveArrayShape);
        }

        internal TypeNode ProjectOrdinaryParameter(TypeNode type)
        {
            RequireSourceClock();
            return FBoundStarProjectionErasure.ProjectOrdinaryParameter(type, this);
        }

        internal TypeNode ClassConstrainedArgument(TypeNode.Fqn owner, int index, TypeNode source, TypeNode projected,
            Func<int, TypeNode> closeArgument)
        {
            if (StripSourceNullability(source) is not TypeNode.Fqn { Args.Length: > 0 } application
                || !TryExistentialCarrier(application.Name, out var carrier)
                || StripSourceNullability(projected) is not TypeNode.Fqn p
                || p.Name != carrier && !ContainsExistentialProjection(application)) return projected;
            var exact = !ContainsExistentialProjection(application);
            var proof = ClassStorageWitness(owner, index, application, closeArgument, allowIntersection: exact);
            if (proof == null) return projected;
            // With no hidden argument the authored constructed class is itself a
            // genuine witness of the conjunction, even if no ancestor is one.
            return proof.Interfaces.Length == 0 ? proof.Class : Rewrite(application, preserveConstructedHead: true);
        }

        internal TypeNode.Fqn SharedClassWitness(TypeNode.Fqn owner, int index, TypeNode.Fqn application,
            Func<int, TypeNode> closeArgument = null)
            => ClassStorageWitness(owner, index, application, closeArgument, allowIntersection: false)?.Class;

        internal sealed record StorageWitness(TypeNode.Fqn Class, TypeNode.Fqn[] Interfaces);

        BirTypeParameter[] ConstraintParameters(string owner)
        {
            if (Preparation.Bindings.SourceDefinitions.TryGetValue(owner, out var local))
                return TypeParameterFrame.Declarations((IBirTypeDeclaration)local.Source).ToArray();
            if (!referencedWitnessParameters.TryGetValue(owner, out var parameters))
                referencedWitnessParameters[owner] = parameters = References.PhysicalOwnerTypeParametersInPhysicalFrame(owner);
            return parameters;
        }

        string BoundKind(TypeNode.Fqn bound)
        {
            if (Preparation.SourceSupertypes.TryGetValue(bound.Name, out var def)) return def.Kind;
            if (References.PhysicalConstraintType(bound) is not TypeNode.Fqn exact) return null;
            var native = References.PhysicalTypeForProof(exact.Name, exact.Args?.Length ?? 0);
            return native?.IsInterface == true ? "interface" : native?.IsClass == true ? "class" : null;
        }

        internal ProjectedClassStorageFrame StorageFrame(string owner)
        {
            if (ConstraintParameters(owner) is not { Length: > 0 } parameters) return null;
            Preparation.Bindings.SourceDefinitions.TryGetValue(owner, out var declaration);
            var frame = ProjectedClassStorageFrame.Plan(parameters, (index, bound) => {
                // The local source may say Base<T> even though its emitted constraint is
                // the Kotlin value interface. A pair of interfaces is not a class intersection.
                var type = declaration == null ? bound :
                    Rewrite(bound, NativeProofs.IsPhysicalConstraint(declaration, index, bound)) as TypeNode.Fqn;
                if (type == null || IsObjectish(type)) return (type, null);
                if (type.Name != bound.Name && TryExistentialCarrier(bound.Name, out var carrier) && type.Name == carrier)
                    return (type, "interface");
                return (type, BoundKind(type));
            });
            if (frame == null) return null;
            // A derived TypeDef must be legal for *all* its instantiations, even
            // if no one constructs its projected form. A constraint used by its
            // exact base construction cannot be moved onto another CLR slot.
            var parent = Preparation.SourceSupertypes.TryGetValue(owner, out var def) ? def.Base :
                References.ReferencedSupertypes(new TypeNode.Fqn(owner, Enumerable.Range(0, parameters.Length)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray())).FirstOrDefault(e => !e.isInterface).spec;
            if (parent?.Args == null) return frame;
            var required = new List<(int Index, TypeNode.Fqn Bound)>();
            BirGraphNameFields.VisitTypeNodes(parent, node => {
                if (node is not TypeNode.Fqn { Args: { Length: > 0 } args } head ||
                    ConstraintParameters(head.Name) is not { } formal || formal.Length != args.Length) return;
                for (int i = 0; i < args.Length; i++)
                    if (args[i] is TypeNode.Tv { Scope: "type" } actual)
                        foreach (var constraint in formal[i].Constraints.OrEmpty())
                            if (SupertypeGraph.SubstOwnerTvs(constraint, args) is TypeNode.Fqn bound)
                                required.Add((actual.I, bound));
            });
            return required.Count == 0 ? frame : frame.Retaining(slot => required.Any(r => r.Index == slot.OwnerIndex
                && slot.Bound is TypeNode.Fqn face && (r.Bound == face ||
                    SupertypeGraph.Reaches(face, r.Bound, Preparation.SourceSupertypes, References))));
        }

        internal TypeNode[] ProjectedConstructorArguments(TypeNode.Fqn owner, ProjectedClassStorageFrame layout)
        {
            var primary = ((TypeNode.Fqn)Rewrite(owner, preserveConstructedHead: true)).Args;
            var visited = new bool[primary.Length];
            var required = new List<(int OwnerIndex, TypeNode.Fqn Bound)>();
            var unproven = false;
            TypeNode Close(int i)
            {
                if (!visited[i] && StripSourceNullability(owner.Args[i]) is TypeNode.Fqn arg && ContainsExistentialProjection(arg))
                {
                    visited[i] = true;
                    if (ClassStorageWitness(owner, i, arg, Close, allowIntersection: true) is { } proof)
                    {
                        primary[i] = proof.Class;
                        foreach (var bound in proof.Interfaces) required.Add((i, bound));
                    }
                    else if (layout.Interfaces.Any(slot => slot.OwnerIndex == i))
                        unproven = true;
                }
                return primary[i];
            }
            for (var i = 0; i < primary.Length; i++) Close(i);
            // Substitute sibling arguments only after all primary slots have
            // been closed: an interface may refer to a later slot.
            if (unproven || required.Count == 0 || required.Any(r => !layout.Interfaces.Any(s =>
                s.OwnerIndex == r.OwnerIndex && SupertypeGraph.SubstOwnerTvs(s.Bound, primary) == r.Bound))) return null;
            return layout.Close(primary);
        }

        internal StorageWitness ClassStorageWitness(TypeNode.Fqn owner, int index, TypeNode.Fqn application,
            Func<int, TypeNode> closeArgument, bool allowIntersection)
        {
            var parameters = ConstraintParameters(owner.Name);
            var frame = Owners.TryGetValue(owner.Name, out var definition) ? OwnerFrame(definition)
                : References.NullableTypeFrames.GetValueOrDefault(owner.Name);
            TypeNode[] closed = null;
            closeArgument ??= i => (closed ??= ((TypeNode.Fqn)Rewrite(owner, preserveConstructedHead: true)).Args)[i];
            return SharedClassWitness(parameters, frame, "type", index, application,
                bound => SupertypeGraph.SubstOwnerTvs(bound, BoundArguments(bound, "type", index, owner.Args, closeArgument)),
                applicationArguments: owner.Args, allowIntersection: allowIntersection);
        }

        static TypeNode[] BoundArguments(TypeNode bound, string scope, int index, TypeNode[] arguments,
            Func<int, TypeNode> closeArgument)
        {
            TypeNode[] physical = null;
            BirGraphNameFields.VisitTypeNodes(bound, part => {
                if (part is TypeNode.Tv tv && tv.Scope == scope && tv.I != index)
                    (physical ??= (TypeNode[])arguments.Clone())[tv.I] = closeArgument(tv.I);
            });
            return physical ?? arguments;
        }

        internal TypeNode MethodClassWitness(BirFunctionDeclaration local, string identity, NullableRepresentationFrame frame,
            TypeNode.Fqn owner, TypeNode[] arguments, int index, TypeNode.Fqn application, TypeNode projected,
            Func<int, TypeNode> closeArgument)
        {
            var parameters = local?.TypeParams.OrDefault();
            if (local == null && identity != null && !referencedMethodWitnessParameters.TryGetValue(identity, out parameters))
                referencedMethodWitnessParameters[identity] = parameters = References.PhysicalMethodTypeParametersInPhysicalFrame(identity);
            TypeNode CloseBound(TypeNode bound) => SubstituteDeclarationTypeArguments(bound, owner?.Args ?? [],
                BoundArguments(bound, "method", index, arguments, closeArgument));
            return SharedClassWitness(parameters, frame, "method", index, application, CloseBound,
                local == null ? null : (i, bound) => Rewrite(bound, NativeProofs.IsPhysicalConstraint(local, i, bound)),
                arguments)?.Class ?? projected;
        }

        StorageWitness SharedClassWitness(BirTypeParameter[] parameters, NullableRepresentationFrame frame, string scope,
            int index, TypeNode.Fqn application, Func<TypeNode, TypeNode> substitute,
            Func<int, TypeNode, TypeNode> physicalBound = null, TypeNode[] applicationArguments = null,
            bool allowIntersection = false)
        {
            if (parameters == null || index >= parameters.Length) return null;
            var linked = new HashSet<int> { index };
            if (frame != null && frame.PhysicalArity == parameters.Length)
            {
                var slot = frame.PhysicalSlot(index);
                if (slot.Representation is NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable
                    && frame.NullableIndices.Contains(slot.SourceIndex))
                {
                    var ordinary = frame.SourcePosition(slot.SourceIndex);
                    var nullable = frame.NullableVariable(new TypeNode.Tv(scope, slot.SourceIndex)).I;
                    // A : nullableA is an edge between two physical slots for the same source A.
                    // Closing both to one class satisfies that edge, not any unrelated B : A.
                    if (parameters[ordinary].Constraints.OrEmpty().Contains(new TypeNode.Tv(scope, nullable)))
                    { linked.Add(ordinary); linked.Add(nullable); }
                }
            }
            // A physical slot (including a synthesized slot without a source frame)
            // may constrain another slot closed over the very same projected type.
            if (applicationArguments != null)
            {
                bool changed;
                do {
                    changed = false;
                    for (int i = 0; i < parameters.Length; i++)
                        foreach (var edge in parameters[i].Constraints.OrEmpty().OfType<TypeNode.Tv>())
                            if (edge.Scope == scope && applicationArguments[i] == applicationArguments[edge.I]
                                && (linked.Contains(i) || linked.Contains(edge.I)))
                            { changed |= linked.Add(i); changed |= linked.Add(edge.I); }
                } while (changed);
            }
            if (linked.Any(i => parameters[i].SpecialConstraints.OrEmpty().Any(special => special != "class"))) return null;
            var bounds = linked.SelectMany(i => parameters[i].Constraints.OrEmpty().Select(bound => physicalBound?.Invoke(i, bound) ?? bound))
                .Where(bound => bound is not TypeNode.Tv tv || tv.Scope != scope || !linked.Contains(tv.I))
                .Select(bound => StripSourceNullability(substitute(bound))).Distinct().ToArray();
            if (bounds.Length == 0) return null;
            TypeNode Identity(TypeNode type) => type is TypeNode.Fqn f && !Preparation.SourceSupertypes.ContainsKey(f.Name)
                ? References.PhysicalConstraintType(type) : type;
            if (!bounds.OfType<TypeNode.Fqn>().Any(bound => !IsObjectish(bound) && BoundKind(bound) == "class")) return null;

            // A nominal Kotlin value interface cannot witness a CLR class bound. Use a real
            // shared base of the possible constructions only if it proves every constraint.
            // Prove the ancestor independently of hidden or variant Kotlin arguments. A
            // Mid<object> inferred from the static view need not be a base of Mid<string>.
            var common = application with { Args = application.Args.Select(_ => (TypeNode)new TypeNode.Star()).ToArray() };
            var supers = SupertypeGraph.ReachableFromClass(common, Preparation.SourceSupertypes, References).Skip(1).ToArray();
            StorageWitness split = null;
            foreach (var (ancestor, isInterface) in supers.Reverse())
            {
                if (isInterface || ContainsExistentialProjection(ancestor)) continue;
                var nominal = SupertypeGraph.ReachableFromClass(ancestor, Preparation.SourceSupertypes, References)
                    .Select(edge => Identity(edge.spec)).ToHashSet();
                var missing = bounds.Where(bound => !IsObjectish(bound) && !nominal.Contains(Identity(bound))).ToArray();
                if (missing.Length == 0) return new(ancestor, []);
                // The value, not its common base, must implement each additional interface.
                // Only a projected implementation with independent slots may use this proof.
                if (allowIntersection && split == null && missing.All(bound => bound is TypeNode.Fqn required
                    && !ContainsExistentialProjection(required) && supers.Any(edge => edge.isInterface
                        && Identity(edge.spec) == Identity(required))))
                    split = new(ancestor, missing.Cast<TypeNode.Fqn>().ToArray());
            }
            return split;
        }

        internal bool IsCapturedInner(TypeNode type)
        {
            RequireSourceClock();
            return FBoundStarProjectionErasure.IsCapturedInner(type, Owners, References);
        }

        internal bool ProjectionCanChangeConstruction(TypeNode.Fqn application) =>
            LocalDeclaration(application.Name) is { } local
                ? HasKotlinVariantParameter(application, local.Variances)
                : HasKotlinVariantParameter(application, References.OwnerTypeParametersInPhysicalFrame(application.Name)
                    ?.Select(parameter => parameter.Variance.OrDefault()).ToArray());

        internal bool RequiresOpaqueVariantAlias(TypeNode.Fqn application)
        {
            var local = LocalDeclaration(application.Name);
            return AliasVarianceRepresentation.RequiresErasureFromSource(application, References, local?.Variances,
                Values?.BindingAlias(application.Name) ?? LocalClrAliases?.GetValueOrDefault(application.Name), local?.Frame);
        }

        internal bool RequiresKotlinValueCarrier(TypeNode.Fqn application)
        {
            if (application.Args is not { Length: > 0 } || Values?.IsBindingAlias(application.Name) == true) return false;
            if (LocalDeclaration(application.Name) is { } local)
                return local.Kind is "class" or "interface" && local.SourceGeneric && Values.OwnsValueSlots(application.Name);
            if (IsCapturedInner(application)) return true;
            if (!References.HasDotKtOwner(application.Name) || !Values.OwnsValueSlots(application.Name)) return false;
            var physicalOwner = References.TryExactPhysicalTypeName(application.Name, application.Args.Length, out var exact)
                && exact != null ? exact : application.Name;
            var reflected = References.ResolveRefType(physicalOwner,
                physicalOwner.Contains('`') || physicalOwner.Contains('+') ? 0 : application.Args.Length);
            return reflected != null && !reflected.IsValueType && !typeof(MulticastDelegate).IsAssignableFrom(reflected);
        }

        internal bool TryExistentialCarrier(string name, out string physicalOwner)
        {
            if (Owners.TryGetValue(name, out var local)) { physicalOwner = local.ErasedName; return local.Needed; }
            if (LocalDeclaration(name) != null) { physicalOwner = null; return false; }
            return References.TryExistentialPhysicalOwner(name, out physicalOwner);
        }
    }

    static bool PreservesNativeWitnessHead(TypeNode type, CarrierProjectionContext context) =>
        StripSourceNullability(type) is TypeNode.Fqn component
        && !ContainsExistentialProjection(component)
        && context.RequiresKotlinValueCarrier(component)
        && !context.ProjectionCanChangeConstruction(component);

    static bool IsExactNativeArrayWitness(TypeNode type, CarrierProjectionContext context) =>
        StripSourceNullability(type) is TypeNode.Array array &&
        (PreservesNativeWitnessHead(array.Elem, context) || IsExactNativeArrayWitness(array.Elem, context));

    static TypeNode ProjectOrdinaryParameter(TypeNode type, CarrierProjectionContext context) => type switch {
        TypeNode.Nullable nullable => new TypeNode.Nullable(ProjectOrdinaryParameter(nullable.Of, context)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ProjectOrdinaryParameter(oblivious.Of, context)),
        TypeNode.Fqn { Args: { Length: > 0 } } named
            when (!context.Owners.TryGetValue(named.Name, out var local) || local.Kind is "class" or "interface")
                && !context.References.IsValueType(named)
                && context.TryExistentialCarrier(named.Name, out var carrier) => new TypeNode.Fqn(carrier),
        _ => type,
    };

    static bool TryWritableVariantArrayElement(TypeNode element, CarrierProjectionContext context,
        out TypeNode.Fqn semanticElement, out TypeNode.Fqn physicalElement)
    {
        semanticElement = StripSourceNullability(element) as TypeNode.Fqn;
        physicalElement = null;
        if (semanticElement?.Args is not { Length: > 0 }
            || !(context.RequiresKotlinValueCarrier(semanticElement) || context.ProjectionCanChangeConstruction(semanticElement))
            || !context.TryExistentialCarrier(semanticElement.Name, out var carrier)) return false;
        physicalElement = new TypeNode.Fqn(carrier);
        return true;
    }
}
