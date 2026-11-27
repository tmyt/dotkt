using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    sealed record ProjectionDeclaration(string Kind, bool SourceGeneric, bool NamedLocal, string[] Variances,
        NullableRepresentationFrame Frame);

    // Declaration readers vary by owning phase; representation policy does
    // not. Current definitions and retained logical alias headers have
    // distinct, producer-owned membership and precedence.
    internal sealed class CarrierProjectionContext
    {
        readonly Func<string, ProjectionDeclaration> declaration;
        readonly CurrentPhysicalTypeView currentPhysical;
        readonly Dictionary<(string Owner, bool NativeNames), BirTypeParameter[]> referencedWitnessParameters = new();
        readonly Dictionary<string, BirTypeParameter[]> referencedMethodWitnessParameters = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, SupertypeGraph.Def> synthesizedHierarchy;
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
                CurrentSourceDeclarations(realization, current, aliases), current);

        static Func<string, ProjectionDeclaration> CurrentSourceDeclarations(CarrierRealizationContext realization,
            CurrentPhysicalTypeView current, AliasDeclarationTransitions aliases)
        {
            var original = ForSource(realization.Demand, realization.References, aliases);
            return name => current.Definitions.TryGetValue(name, out var actual)
                ? ReadSource(AliasDeclarationTransitions.ReadLogicalHeaderFacts(actual))
                : original.LocalDeclaration(name);
        }

        static ProjectionDeclaration ReadSource(AliasDeclarationTransitions.LogicalHeaderFacts header) => new(
            header.Kind.OrDefault(), IsSourceGeneric(header), header.NamedLocal,
            header.TypeParams.HasValue ? header.TypeParams.Value.Select(parameter => parameter.Variance.OrDefault()).ToArray() : null,
            header.KotlinSupertypesPre.OrDefault()?.NullableFrame);

        void RequireSourceClock()
        {
            if (Preparation?.Bindings.IsBound == true)
                throw new InvalidOperationException("Source carrier projection crossed its owning frontier");
        }

        ProjectionDeclaration LocalDeclaration(string name) => declaration(name);

        // A late physical application must not consult a superseded source
        // occurrence, even if that occurrence has the same name and arity.
        IBirTypeDeclaration SelectLocalPhysicalClass(string name) => currentPhysical != null
            ? currentPhysical.Definitions.GetValueOrDefault(name)
            : Preparation.Bindings.SourceDefinitions.GetValueOrDefault(name)?.Source as IBirTypeDeclaration;

        static NullableRepresentationFrame ClassLayout(IBirTypeDeclaration type) =>
            NullableRepresentationViews.TypeFacts((BirGraphValue)type)?.NullableFrame;

        internal NullableRepresentationFrame OwnerFrame(Owner owner) =>
            ClassApplicationLayout(owner.Name);

        internal NullableRepresentationFrame ClassApplicationLayout(string name) =>
            SelectLocalPhysicalClass(name) is { } local ? ClassLayout(local)
                : References.NullableTypeFrames.GetValueOrDefault(name);

        internal TypeNode MaterialNativeMethodArgument(TypeNode type) => PreservesNestedNativeWitnessHead(type, this)
            ? Rewrite(type, preserveConstructedHead: true) : type;

        internal TypeNode Rewrite(TypeNode type, bool boundDeclaration = false,
            bool preserveConstructedHead = false, bool preserveArrayShape = false, bool sourceOverrideOwner = false)
        {
            RequireSourceClock();
            return RewriteType(type, this, boundDeclaration, preserveConstructedHead, preserveArrayShape, sourceOverrideOwner);
        }

        internal TypeNode ProjectOrdinaryParameter(TypeNode type)
        {
            RequireSourceClock();
            return FBoundStarProjectionErasure.ProjectOrdinaryParameter(type, this);
        }

        internal bool NeedsClassWitness(TypeNode original, TypeNode projected) =>
            StripSourceNullability(original) is TypeNode.Fqn { Args.Length: > 0 } application
                && TryExistentialCarrier(application.Name, out var carrier)
                && StripSourceNullability(projected) is TypeNode.Fqn p
                && (p.Name == carrier || ContainsExistentialProjection(application));

        internal TypeNode ClassConstrainedArgument(WitnessSlot slot,
            TypeNode projected, Func<int, TypeNode> closeArgument, TypeNode[] nativeArguments = null)
        {
            var application = slot.ConstructedArgument;
            var exact = !ContainsExistentialProjection(application);
            var proof = ClassStorageWitness(slot, closeArgument, allowIntersection: exact,
                acceptsPrimary: nativeArguments == null ? null : candidate => SatisfiesFixedSiblings(slot, nativeArguments, candidate));
            if (proof == null) return projected;
            // With no hidden argument the authored constructed class is itself a
            // genuine witness of the conjunction, even if no ancestor is one.
            return proof.Interfaces.Length == 0 ? proof.Class : Rewrite(application, preserveConstructedHead: true);
        }

        // A native type parameter may constrain a sibling: B : ISink<A> limits the
        // choice for A even though that constraint belongs to B's row. Only compare
        // closed, fixed arguments here; a caller variable is not a closed CLR type.
        bool SatisfiesFixedSiblings(WitnessSlot selected, TypeNode[] physical, TypeNode.Fqn candidate)
        {
            var entry = selected.Entry;
            for (int j = 0; j < physical.Length; j++)
            {
                if (j == selected.Index || ContainsStarOrTypeVariable(entry.Arguments[j])) continue;
                foreach (var bound in entry.Slot(j).Formal.Constraints.OrEmpty())
                {
                    bool depends = false, unresolved = false;
                    BirGraphNameFields.VisitTypeNodes(bound, part => {
                        if (part is not TypeNode.Tv { Scope: "type" } variable) return;
                        if (variable.I == selected.Index) depends = true;
                        else unresolved |= ContainsStarOrTypeVariable(entry.Arguments[variable.I]);
                    });
                    if (!depends || unresolved) continue;
                    var proposed = (TypeNode[])physical.Clone();
                    proposed[selected.Index] = candidate;
                    var required = SupertypeGraph.SubstOwnerTvs(bound, proposed);
                    synthesizedHierarchy ??= SupertypeGraph.CollectSource(Preparation.Bindings.SourceRoots.Select(r => (BirFileDeclaration)r.Source));
                    if (!ProjectedConstructorPlan.Proves(physical[j], required, synthesizedHierarchy, References)) return false;
                }
            }
            return true;
        }

        internal readonly record struct WitnessSlot(PhysicalWitnessApplication Entry, int Index)
        {
            internal TypeNode Actual => Entry.Arguments[Index];
            internal TypeNode.Fqn ConstructedArgument => StripSourceNullability(Actual) as TypeNode.Fqn;
            internal BirTypeParameter Formal => Entry.Parameters?[Index];
            internal NullableRepresentationFrame.Slot? Role => Entry.Layout?.PhysicalSlot(Index);
            internal TypeNode CloseBound(TypeNode bound, Func<int, TypeNode> closeSibling)
            {
                var scope = Entry.Scope;
                var original = Entry.Arguments;
                var index = Index;
                TypeNode[] closed = null;
                BirGraphNameFields.VisitTypeNodes(bound, part => {
                    if (part is TypeNode.Tv tv && tv.Scope == scope && tv.I != index)
                        (closed ??= (TypeNode[])original.Clone())[tv.I] = closeSibling(tv.I);
                });
                var arguments = closed ?? original;
                return Entry.Binder == WitnessBinder.Owner ? SupertypeGraph.SubstOwnerTvs(bound, arguments)
                    : SubstituteDeclarationTypeArguments(bound, Entry.Owner?.Args ?? [], arguments);
            }
        }

        internal enum WitnessBinder { Owner, Method }

        // Only selected, materialized applications enter the witness solver.
        // The owner of a method and its own type arguments are distinct binders.
        internal readonly struct PhysicalWitnessApplication
        {
            internal readonly TypeNode.Fqn Owner;
            internal readonly BirFunctionDeclaration LocalMethod;
            internal readonly NullableRepresentationFrame Layout;
            internal readonly BirTypeParameter[] Parameters;
            internal readonly TypeNode[] Arguments;
            internal readonly WitnessBinder Binder;

            internal PhysicalWitnessApplication(WitnessBinder binder, TypeNode.Fqn owner, string identity,
                BirFunctionDeclaration localMethod, NullableRepresentationFrame layout,
                BirTypeParameter[] parameters, TypeNode[] arguments)
            {
                if ((parameters != null && parameters.Length != arguments.Length)
                    || (layout != null && layout.PhysicalArity != arguments.Length))
                    throw new InvalidOperationException($"Physical constraint frame disagrees with the selected application '{identity ?? owner?.Name}'");
                Binder = binder;
                Owner = owner;
                LocalMethod = localMethod;
                Layout = layout;
                Parameters = parameters;
                Arguments = arguments;
            }

            internal WitnessSlot Slot(int index) => new(this, index);

            internal string Scope => Binder == WitnessBinder.Owner ? "type" : "method";
        }

        internal PhysicalWitnessApplication SelectPhysicalClassApplication(TypeNode.Fqn application, bool nativeNames = false)
        {
            if (SelectLocalPhysicalClass(application.Name) is { } local)
                return new(WitnessBinder.Owner, application, null, null,
                    ClassLayout(local), TypeParameterFrame.Declarations(local).ToArray(), application.Args ?? []);
            return new(WitnessBinder.Owner, application, null, null,
                References.NullableTypeFrames.GetValueOrDefault(application.Name),
                ReferencedConstraintParameters(application.Name, nativeNames), application.Args ?? []);
        }

        internal PhysicalWitnessApplication SelectPhysicalMethodApplication(BirFunctionDeclaration local, string identity,
            NullableRepresentationFrame frame, TypeNode.Fqn owner, TypeNode[] arguments, BirTypeParameter[] nativeParameters = null)
        {
            var parameters = local?.TypeParams.OrDefault() ?? nativeParameters;
            if (local == null && identity != null && !referencedMethodWitnessParameters.TryGetValue(identity, out parameters))
                referencedMethodWitnessParameters[identity] = parameters = References.PhysicalMethodTypeParametersInPhysicalFrame(identity);
            return new(WitnessBinder.Method, owner, identity, local, frame, parameters, arguments);
        }

        internal sealed record StorageWitness(TypeNode.Fqn Class, (int OwnerIndex, TypeNode.Fqn Bound)[] Required)
        {
            internal TypeNode.Fqn[] Interfaces => Required.Select(r => r.Bound).Distinct().ToArray();
        }

        BirTypeParameter[] ConstraintParameters(string owner, bool nativeNames = false)
        {
            if (Preparation.Bindings.SourceDefinitions.TryGetValue(owner, out var local))
                return TypeParameterFrame.Declarations((IBirTypeDeclaration)local.Source).ToArray();
            return ReferencedConstraintParameters(owner, nativeNames);
        }

        BirTypeParameter[] ReferencedConstraintParameters(string owner, bool nativeNames = false)
        {
            if (!referencedWitnessParameters.TryGetValue((owner, nativeNames), out var parameters))
                referencedWitnessParameters[(owner, nativeNames)] = parameters = References.PhysicalOwnerTypeParametersInPhysicalFrame(owner, nativeNames);
            return parameters;
        }

        TypeNode ConstraintKey(TypeNode bound) => bound is TypeNode.Fqn f && !Preparation.SourceSupertypes.ContainsKey(f.Name)
            ? References.WitnessConstraintKey(bound) : bound;

        string BoundKind(TypeNode.Fqn bound)
        {
            if (Preparation.SourceSupertypes.TryGetValue(bound.Name, out var def)) return def.Kind;
            if (References.PhysicalConstraintType(bound) is not TypeNode.Fqn exact) return null;
            var native = References.PhysicalTypeForProof(exact.Name, exact.Args?.Length ?? 0);
            return native?.IsInterface == true ? "interface" : native?.IsClass == true ? "class" : null;
        }

        internal TypeNode PreserveReferenceBound(BirTypeParameter row, TypeNode bound, TypeNode projected)
        {
            // The carrier preserves Kotlin membership, not the fact that its
            // source upper bound is a class. Keep that independent CLR proof.
            if (StripSourceNullability(bound) is TypeNode.Fqn { Args.Length: > 0 } f && BoundKind(f) == "class"
                && TryExistentialCarrier(f.Name, out var carrier)
                && StripSourceNullability(projected) is TypeNode.Fqn p && p.Name == carrier
                && !row.SpecialConstraints.OrEmpty().Contains("class"))
                row.SpecialConstraints = row.SpecialConstraints.OrEmpty().Append("class").ToArray();
            return projected;
        }

        internal sealed record FixedSupertypeConstraint(int Index, TypeNode.Fqn Bound, string Owner);

        readonly Dictionary<string, ProjectedClassStorageFrame> constructorFrames = new(StringComparer.Ordinal);

        internal ProjectedClassStorageFrame StorageFrame(string owner)
        {
            if (constructorFrames.TryGetValue(owner, out var cached)) return cached;
            // Discover only declarations reached by an actual forwarded constructor argument.
            // Close over the finite set of declared facets, not recursively expanded types.
            var work = new Dictionary<string, List<(NativeProofPlan.ConstructorEdge Use, ProjectedConstructorPlan External, FixedSupertypeConstraint[] Fixed)>>();
            var pending = new Queue<string>();
            void Admit(string name)
            {
                if (!constructorFrames.TryGetValue(name, out var frame))
                {
                    frame = constructorFrames[name] = DirectStorageFrame(name);
                    if (frame != null) pending.Enqueue(name);
                }
            }
            Admit(owner);
            while (pending.TryDequeue(out var name))
            {
                var calls = work[name] = [];
                foreach (var use in NativeProofs.ForwardedConstructors(Preparation.Bindings.SourceDefinitions.GetValueOrDefault(name)))
                {
                    if (use.Id == null)
                    {
                        // A selected CLR constructor needs its declaring TypeDef's physical
                        // primary rows, independently of which overload is invoked.
                        calls.Add((use, null, JointPrimaryConstraints(ReferencedConstraintParameters(use.Application.Name, nativeNames: true),
                            use.Application.Name).ToArray()));
                        continue;
                    }
                    ProjectedConstructorPlan entry = null;
                    if (Preparation.Bindings.SourceDefinitions.TryGetValue(use.Application.Name, out var local))
                    {
                        if (!((IBirTypeDeclaration)local.Source).Ctors.OrEmpty().Any(c => c.DeclarationId.OrDefault() == use.Id)) continue;
                        Admit(use.Application.Name);
                    }
                    else
                    {
                        entry = References.ConstructorPlan(StorageFactoryId(use.Id), nativeNames: false);
                        if (entry == null && !References.HasDotKtOwner(use.Application.Name)) continue;
                    }
                    calls.Add((use, entry, NativeSupertypeConstraints(use.Application.Name, use.Application.Args.Length).Fixed));
                }
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var (name, calls) in work)
                {
                    var frame = constructorFrames[name];
                    foreach (var (use, imported, fixedBase) in calls)
                    {
                        var actuals = use.Application.Args;
                        var local = constructorFrames.GetValueOrDefault(use.Application.Name);
                        if ((imported?.PrimaryArity ?? local?.PrimaryArity) is int arity && actuals.Length != arity)
                            throw new InvalidOperationException($"Physical argument frame disagrees with the selected constructor '{use.Id}'");
                        var demands = imported != null
                            ? imported.Facets.Where(f => f.NativeArgument).SelectMany(f => f.Constraints.Select(c => (Primary: f.Primary, Bound: c)))
                            : (local?.Interfaces ?? []).Where(f => f.NativeArgument).Select(f => (Primary: f.OwnerIndex, f.Bound));
                        var joint = fixedBase.Select(r => (r.Index, (TypeNode)r.Bound)).Concat(
                            imported != null ? JointPrimaryConstraints(imported.PrimaryParameters, use.Application.Name).Select(r => (r.Index, (TypeNode)r.Bound))
                                : (local?.Interfaces ?? []).SelectMany(s => (s.PrimaryBounds ?? []).Select(b => (s.OwnerIndex, b))));
                        (int I, TypeNode Bound)[] Forward(IEnumerable<(int Primary, TypeNode Bound)> requirements) =>
                            requirements.Where(f => actuals[f.Primary] is TypeNode.Tv { Scope: "type" })
                                .Select(f => (((TypeNode.Tv)actuals[f.Primary]).I, SupertypeGraph.SubstOwnerTvs(f.Bound, actuals)))
                                .Distinct().ToArray();
                        var fixedRows = Forward(joint).Where(r => r.Bound is TypeNode.Fqn c && BoundKind(c) == "interface").ToArray();
                        var expanded = fixedRows.Length == 0 ? frame : frame.WithForwardedPrimaryBounds(s => fixedRows.Where(r => r.I == s.OwnerIndex &&
                            ProjectedConstructorPlan.Proves(s.Bound, r.Bound, Preparation.SourceSupertypes, References))
                            .Select(r => PrimaryConstraint(s, r.Bound)));
                        if (expanded != frame)
                        {
                            constructorFrames[name] = frame = expanded;
                            changed = true;
                        }
                        var required = Forward(demands);
                        var added = frame.Interfaces.Where(s => !s.NativeArgument && required.Any(r => r.I == s.OwnerIndex &&
                            ProjectedConstructorPlan.Proves(s.Bound, r.Bound, Preparation.SourceSupertypes, References))).ToHashSet();
                        if (added.Count == 0) continue;
                        frame = constructorFrames[name] = frame.WithNativeArguments(s => s.NativeArgument || added.Contains(s));
                        changed = true;
                    }
                }
            } while (changed);
            return constructorFrames[owner];
        }

        TypeNode PrimaryConstraint(ProjectedClassStorageFrame.InterfaceSlot slot, TypeNode required) =>
            ConstraintKey(slot.Bound) == ConstraintKey(required) ? slot.Bound : required;

        ProjectedClassStorageFrame DirectStorageFrame(string owner)
        {
            if (ConstraintParameters(owner) is not { Length: > 0 } parameters) return null;
            Preparation.Bindings.SourceDefinitions.TryGetValue(owner, out var declaration);
            var layout = ClassApplicationLayout(owner);
            var frame = ProjectedClassStorageFrame.Plan(parameters, (index, bound) => {
                // The local source may say Base<T> even though its emitted constraint is
                // the Kotlin value interface. Classify the actual physical bound.
                var type = declaration == null ? bound :
                    Rewrite(bound, NativeProofs.IsPhysicalConstraint(declaration, index, bound)) as TypeNode.Fqn;
                if (type == null || IsObjectish(type)) return (type, null);
                if (type.Name != bound.Name && TryExistentialCarrier(bound.Name, out var carrier) && type.Name == carrier)
                    return (type, "interface");
                return (type, BoundKind(type));
            }, splitInterfaceConjunction: index =>
                (layout == null || layout.PhysicalSlot(index).Representation == NullableRepresentationFrame.Role.Ordinary) &&
                parameters[index].Constraints.OrEmpty().All(b => b is TypeNode.Fqn f && BoundKind(f) == "interface"));
            if (frame == null) return null;
            var (required, nativeArguments) = NativeSupertypeConstraints(owner, parameters.Length);
            bool Required(ProjectedClassStorageFrame.InterfaceSlot slot, FixedSupertypeConstraint r) =>
                r.Index == slot.OwnerIndex && ProjectedConstructorPlan.Proves(slot.Bound, r.Bound,
                    Preparation.SourceSupertypes, References);
            if (nativeArguments.Count != 0) frame = frame.WithNativeArguments(s => nativeArguments.Contains(s.OwnerIndex));
            frame = frame.WithDeclaredProofs(Values);
            return required.Length == 0 ? frame : frame.ForBase(s => required.Where(r => Required(s, r))
                .Select(r => PrimaryConstraint(s, r.Bound)).Distinct().ToArray());
        }

        (FixedSupertypeConstraint[] Fixed, HashSet<int> Native) NativeSupertypeConstraints(string owner, int arity)
        {
            // A derived TypeDef must be legal for *all* its instantiations, even
            // if no one constructs its projected form. A constraint used by its
            // exact base construction cannot be moved onto another CLR slot.
            IEnumerable<(TypeNode.Fqn spec, bool isInterface)> parents =
                Preparation.SourceSupertypes.TryGetValue(owner, out var def)
                ? def.Interfaces.Select(f => (f, true)).Append((def.Base, false))
                : References.ReferencedSupertypes(new TypeNode.Fqn(owner, Enumerable.Range(0, arity)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray()));
            var required = new List<FixedSupertypeConstraint>();
            var nativeArguments = new HashSet<int>();
            foreach (var (parent, isInterface) in parents)
            {
                if (parent?.Args == null || isInterface && (Preparation.SourceSupertypes.ContainsKey(parent.Name)
                    || References.HasDotKtOwner(parent.Name))) continue;
                BirGraphNameFields.VisitTypeNodes(parent, node => {
                    if (node is not TypeNode.Fqn { Args: { Length: > 0 } args } head ||
                        ConstraintParameters(head.Name, nativeNames: true) is not { } formal || formal.Length != args.Length) return;
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] is not TypeNode.Tv { Scope: "type" } actual) continue;
                        var bounds = formal[i].Constraints.OrEmpty();
                        if (isInterface && !bounds.OfType<TypeNode.Fqn>().Any(b => !IsObjectish(b) && BoundKind(b) == "class"))
                        {
                            nativeArguments.Add(actual.I);
                            continue;
                        }
                        foreach (var constraint in bounds)
                            if (SupertypeGraph.SubstOwnerTvs(constraint, args) is TypeNode.Fqn bound)
                                required.Add(new(actual.I, bound, head.Name));
                    }
                });
            }
            return (required.Distinct().ToArray(), nativeArguments);
        }

        // Only rows on the physical primary must hold jointly. The semantic witness
        // also contains the facets, which may legally be proven by different CLR types.
        IEnumerable<FixedSupertypeConstraint> JointPrimaryConstraints(BirTypeParameter[] primary, string owner) =>
            (primary ?? []).SelectMany((p, i) => {
                var bounds = p.Constraints.OrEmpty().OfType<TypeNode.Fqn>().ToArray();
                return bounds.Any(b => !IsObjectish(b) && BoundKind(b) == "class")
                    ? bounds.Where(b => BoundKind(b) == "interface").Select(b => new FixedSupertypeConstraint(i, b, owner))
                    : [];
            });

        internal TypeNode[] ProjectedConstructorArguments(PhysicalWitnessApplication application, ProjectedConstructorPlan plan)
        {
            var owner = application.Owner;
            // Construction invokes the factory, not the exact TypeDef. In particular,
            // a captured inner type's exact declaration may have weakened constraints.
            if (plan != null) application = new(WitnessBinder.Owner, owner, null, null,
                application.Layout, plan.WitnessParameters, application.Arguments);
            var fixedSlots = NativeSupertypeConstraints(owner.Name, application.Arguments.Length).Fixed.AsEnumerable()
                .Concat(plan != null ? JointPrimaryConstraints(plan.PrimaryParameters, owner.Name) : [])
                .Where(r => BoundKind(r.Bound) == "interface").ToArray();
            if (plan == null && fixedSlots.Length == 0) return null;
            var primary = ((TypeNode.Fqn)Rewrite(owner, preserveConstructedHead: true)).Args;
            var visited = new bool[primary.Length];
            var required = new List<(int OwnerIndex, TypeNode.Fqn Bound)>();
            var unproven = false;
            TypeNode Close(int i)
            {
                if (!visited[i] && StripSourceNullability(application.Arguments[i]) is TypeNode.Fqn arg && ContainsExistentialProjection(arg))
                {
                    visited[i] = true;
                    if (ClassStorageWitness(application.Slot(i), Close, allowIntersection: true) is { } proof)
                    {
                        if (proof.Interfaces.Length != 0 && fixedSlots?.Any(r => r.Index == i) == true)
                        {
                            var nativeBounds = fixedSlots.Where(r => r.Index == i).Select(r =>
                                (TypeNode.Fqn)application.Slot(i).CloseBound(r.Bound, Close)).ToArray();
                            bool Fits(TypeNode.Fqn candidate) => nativeBounds.All(b =>
                                ProjectedConstructorPlan.Proves(candidate, b, Preparation.SourceSupertypes, References));
                            if (!Fits(proof.Class)) proof = ClassStorageWitness(application.Slot(i), Close, true, Fits)
                                ?? throw new NotSupportedException($"cannot construct '{owner.Name}': CLR construction "
                                    + $"requires a single type argument that satisfies both its class and interface constraints "
                                    + $"({string.Join(", ", fixedSlots.Where(r => r.Index == i).Select(r => r.Owner).Distinct())}, parameter {i})");
                        }
                        primary[i] = proof.Class;
                        required.AddRange(proof.Required);
                    }
                    else if (plan?.Facets.Any(slot => slot.Primary == i) == true)
                        unproven = true;
                }
                return primary[i];
            }
            for (var i = 0; i < primary.Length; i++) Close(i);
            // Substitute sibling arguments only after all primary slots have
            // been closed: an interface may refer to a later slot.
            TypeNode NativeArgument(int index)
            {
                var a = application.Arguments[index];
                return Rewrite(a, preserveConstructedHead: PreservesNativeWitnessHead(a, this) || IsExactNativeArrayWitness(a, this));
            }
            // Forwarding A through another Kotlin constructor must not forget a
            // more specific proof solely because that caller has no native face
            // of its own. Ask the actual synthesized carrier (or referenced DLL),
            // not the source class whose interfaces it might not represent.
            TypeNode FacetArgument(ProjectedConstructorPlan.Facet slot, TypeNode bound)
            {
                var actual = NativeArgument(slot.Primary);
                if (slot.NativeArgument) return actual;
                if (actual is not TypeNode.Fqn candidate || bound is not TypeNode.Fqn facet) return bound;
                return HasPhysicalSupertype(candidate, facet) ? actual : bound;
            }
            if (plan == null || unproven || required.Any(r => !plan.Facets.Any(s =>
                s.Primary == r.OwnerIndex && ConstraintKey(SupertypeGraph.SubstOwnerTvs(s.Bound, primary)) == ConstraintKey(r.Bound)))) return null;
            var arguments = plan.Close(primary, FacetArgument);
            if (required.Count == 0 && !plan.Facets.Select((slot, i) => (slot, witness: arguments[primary.Length + i]))
                .Any(row => ContainsExistentialProjection(application.Arguments[row.slot.Primary]) &&
                    row.witness == NativeArgument(row.slot.Primary) && row.witness != primary[row.slot.Primary])) return null;
            return arguments;
        }

        internal TypeNode.Fqn InterfaceCarrierConstruction(TypeNode.Fqn owner)
        {
            RequireSourceClock();
            if (!TryExistentialCarrier(owner.Name, out _)) return null;
            var application = SelectPhysicalClassApplication(owner);
            if (application.Parameters == null) return null;
            var proposed = (TypeNode[])owner.Args.Clone();
            var constrained = false;
            for (int i = 0; i < proposed.Length; i++)
            {
                if (!ContainsExistentialProjection(proposed[i])) continue;
                if (StripSourceNullability(proposed[i]) is not TypeNode.Fqn arg || !TryExistentialCarrier(arg.Name, out var name)
                    || application.Parameters[i].SpecialConstraints.OrEmpty().Any(s => s != "class")) return null;
                proposed[i] = new TypeNode.Fqn(name);
                constrained |= application.Parameters[i].Constraints.OrEmpty().Any(b => b is TypeNode.Fqn f && !IsObjectish(f));
            }
            if (!constrained) return null;
            // Only a native obligation needs the carrier as the CLR primary
            // argument. Pure Kotlin bounds keep their established witness and
            // value slots; replacing a usable witness can erase a use-site bound.
            var native = NativeSupertypeConstraints(owner.Name, proposed.Length);
            if (!Enumerable.Range(0, proposed.Length).Any(i => proposed[i] != owner.Args[i]
                && (NativeProofs.ExactTypeArgument(owner.Name, i) || native.Native.Contains(i)
                    || native.Fixed.Any(row => row.Index == i)))) return null;
            // Proof and newobj must name the same arguments, including the slot
            // assigned to a sibling bound. An operand's runtime class is not
            // the type argument of an invariant native interface.
            var selected = owner with { Args = proposed };
            var physical = ((TypeNode.Fqn)Rewrite(selected, preserveConstructedHead: true)).Args;
            synthesizedHierarchy ??= SupertypeGraph.CollectSource(Preparation.Bindings.SourceRoots.Select(r => (BirFileDeclaration)r.Source));
            Preparation.Bindings.SourceDefinitions.TryGetValue(owner.Name, out var local);
            for (int i = 0; i < physical.Length; i++)
                foreach (var constraint in application.Parameters[i].Constraints.OrEmpty())
                {
                    // Unchanged slots may contain the caller's open type variables.
                    // Only constraints affected by this substitution need a new proof.
                    var changed = proposed[i] != owner.Args[i];
                    if (!changed) BirGraphNameFields.VisitTypeNodes(constraint, part =>
                        changed |= part is TypeNode.Tv { Scope: "type" } tv && proposed[tv.I] != owner.Args[tv.I]);
                    if (!changed) continue;
                    var bound = local == null ? constraint : Rewrite(constraint, NativeProofs.IsPhysicalConstraint(local, i, constraint));
                    var closed = SupertypeGraph.SubstOwnerTvs(bound, physical);
                    if (!IsObjectish(closed) && !ProjectedConstructorPlan.Proves(physical[i], closed, synthesizedHierarchy, References)) return null;
                }
            return selected;
        }

        internal StorageWitness ClassStorageWitness(WitnessSlot slot,
            Func<int, TypeNode> closeArgument, bool allowIntersection, Func<TypeNode.Fqn, bool> acceptsPrimary = null)
        {
            TypeNode[] closed = null;
            closeArgument ??= i => (closed ??= ((TypeNode.Fqn)Rewrite(slot.Entry.Owner, preserveConstructedHead: true)).Args)[i];
            return SharedClassWitness(slot, closeArgument,
                allowIntersection: allowIntersection, acceptsPrimary: acceptsPrimary);
        }

        internal StorageWitness MethodClassWitness(WitnessSlot slot, Func<int, TypeNode> closeArgument,
            Func<int,bool> keepUnboundedMaterial)
        {
            var method = slot.Entry.LocalMethod;
            return SharedClassWitness(slot, closeArgument, method == null ? null
                : (i, bound) => Rewrite(bound, NativeProofs.IsPhysicalConstraint(method, i, bound)), allowIntersection: true,
                keepUnboundedMaterial: keepUnboundedMaterial);
        }

        bool HasPhysicalSupertype(TypeNode.Fqn candidate, TypeNode.Fqn required)
        {
            synthesizedHierarchy ??= SupertypeGraph.CollectSource(Preparation.Bindings.SourceRoots
                .Select(r => (BirFileDeclaration)r.Source));
            return SupertypeGraph.Reaches(candidate, required, synthesizedHierarchy, References);
        }

        StorageWitness SharedClassWitness(WitnessSlot selected,
            Func<int, TypeNode> closeArgument, Func<int, TypeNode, TypeNode> physicalBound = null,
            bool allowIntersection = false, Func<TypeNode.Fqn, bool> acceptsPrimary = null,
            Func<int,bool> keepUnboundedMaterial = null)
        {
            if (selected.Formal == null || selected.ConstructedArgument is not { Args.Length: > 0 } application) return null;
            var entry = selected.Entry;
            var frame = entry.Layout;
            var scope = entry.Scope;
            var linked = new HashSet<int> { selected.Index };
            if (selected.Role is { } slot)
            {
                if (slot.Representation is NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable
                    && frame.NullableIndices.Contains(slot.SourceIndex))
                {
                    var ordinary = frame.SourcePosition(slot.SourceIndex);
                    var nullable = frame.NullableVariable(new TypeNode.Tv(scope, slot.SourceIndex)).I;
                    // A : nullableA is an edge between two physical slots for the same source A.
                    // Closing both to one class satisfies that edge, not any unrelated B : A.
                    if (entry.Slot(ordinary).Formal.Constraints.OrEmpty().Contains(new TypeNode.Tv(scope, nullable)))
                    { linked.Add(ordinary); linked.Add(nullable); }
                }
            }
            // A physical slot (including a synthesized slot without a source frame)
            // may constrain another slot closed over the very same projected type.
            bool changed;
            do {
                changed = false;
                for (int i = 0; i < entry.Arguments.Length; i++)
                    foreach (var edge in entry.Slot(i).Formal.Constraints.OrEmpty().OfType<TypeNode.Tv>())
                        if (edge.Scope == scope && entry.Slot(i).Actual == entry.Slot(edge.I).Actual
                            && (linked.Contains(i) || linked.Contains(edge.I)))
                        { changed |= linked.Add(i); changed |= linked.Add(edge.I); }
            } while (changed);
            if (linked.Any(i => entry.Slot(i).Formal.SpecialConstraints.OrEmpty().Any(special => special != "class"))) return null;
            var declared = linked.SelectMany(i => entry.Slot(i).Formal.Constraints.OrEmpty().Select(bound =>
                    (Index: i, Bound: physicalBound?.Invoke(i, bound) ?? bound)))
                .Where(r => r.Bound is not TypeNode.Tv tv || tv.Scope != scope || !linked.Contains(tv.I))
                .Select(r => (r.Index, Bound: StripSourceNullability(selected.CloseBound(r.Bound, closeArgument)))).Distinct().ToArray();
            var bounds = declared.Select(r => r.Bound).Distinct().ToArray();
            // The value has its own material argument. Keep the most precise common
            // class in the nominal method slot, including when the published slot
            // only requires an interface. A later override can use that class;
            // the existential interface spelling is not itself a CLR class.
            var separateMaterial = scope == "method" && linked.Any(i =>
                entry.Slot(i).Role is { Representation: NullableRepresentationFrame.Role.Ordinary } role &&
                frame.StorageIndices.Contains(role.SourceIndex));
            if (bounds.Length == 0 && (!separateMaterial || keepUnboundedMaterial != null && linked.Any(keepUnboundedMaterial))) return null;
            TypeNode Identity(TypeNode type) => ConstraintKey(type);
            var classBound = bounds.OfType<TypeNode.Fqn>().Any(bound => !IsObjectish(bound) && BoundKind(bound) == "class");
            if (!classBound && !separateMaterial) return null;

            // A nominal Kotlin value interface cannot witness a CLR class bound. Use a real
            // shared base of the possible constructions only if it proves every constraint.
            // Prove the ancestor independently of hidden or variant Kotlin arguments. A
            // Mid<object> inferred from the static view need not be a base of Mid<string>.
            var variances = LocalDeclaration(application.Name)?.Variances ?? References.OwnerTypeParametersInPhysicalFrame(application.Name)
                ?.Select(p => p.Variance.OrDefault()).ToArray();
            var partiallyOpen = ContainsExistentialProjection(application);
            var common = application with { Args = application.Args.Select((arg, i) =>
                !partiallyOpen || ContainsExistentialProjection(arg) ||
                variances?.ElementAtOrDefault(i) is "in" or "out" ? (TypeNode)new TypeNode.Star() : arg).ToArray() };
            var supers = SupertypeGraph.ReachableFromClass(common, Preparation.SourceSupertypes, References).Skip(1).ToArray();
            StorageWitness split = null;
            foreach (var (ancestor, isInterface) in separateMaterial ? supers : supers.Reverse())
            {
                if (isInterface || ContainsExistentialProjection(ancestor) || acceptsPrimary?.Invoke(ancestor) == false) continue;
                var nominal = SupertypeGraph.ReachableFromClass(ancestor, Preparation.SourceSupertypes, References)
                    .Select(edge => Identity(edge.spec)).ToHashSet();
                var missing = bounds.Where(bound => !IsObjectish(bound) && !nominal.Contains(Identity(bound))).ToArray();
                if (missing.Length == 0) return new(ancestor, []);
                // The value, not its common base, must implement each additional interface.
                // Only a projected implementation with independent slots may use this proof.
                if (classBound && allowIntersection && split == null && missing.All(bound => bound is TypeNode.Fqn required
                    && !ContainsExistentialProjection(required) && (supers.Any(edge => edge.isInterface
                        && Identity(edge.spec) == Identity(required)) || TryExistentialCarrier(application.Name, out var carrier)
                        && HasPhysicalSupertype(new TypeNode.Fqn(carrier), required))))
                    split = new(ancestor, declared.Where(r => missing.Contains(r.Bound))
                        .Select(r => (r.Index, (TypeNode.Fqn)r.Bound)).ToArray());
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
                return local.Kind is "class" or "interface" && local.SourceGeneric && Values.OwnsValueSlots(application.Name)
                    && (!local.NamedLocal || TryExistentialCarrier(application.Name, out _));
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

    static bool PreservesNestedNativeWitnessHead(TypeNode type, CarrierProjectionContext context) =>
        StripSourceNullability(type) is TypeNode.Fqn { Args.Length: > 0 } nested
        && nested.Args.Any(ContainsExistentialProjection) && !nested.Args.Any(IsExistentialArgument)
        && context.Values?.IsBindingAlias(nested.Name) != true && context.LocalClrAliases?.ContainsKey(nested.Name) != true
        && IsNativeArrayOwner(nested, context.References, context.LocalClrAliases);

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
