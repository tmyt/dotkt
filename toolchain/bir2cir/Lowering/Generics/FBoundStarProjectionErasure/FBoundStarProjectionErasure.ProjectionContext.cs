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

        internal TypeNode.Fqn ClassWitnessCandidate(TypeNode original, TypeNode projected) =>
            StripSourceNullability(original) is TypeNode.Fqn { Args.Length: > 0 } application
                && TryExistentialCarrier(application.Name, out var carrier)
                && StripSourceNullability(projected) is TypeNode.Fqn p
                && (p.Name == carrier || ContainsExistentialProjection(application)) ? application : null;

        internal TypeNode ClassConstrainedArgument(PhysicalWitnessApplication owner, int index, TypeNode.Fqn application,
            TypeNode projected, Func<int, TypeNode> closeArgument)
        {
            var exact = !ContainsExistentialProjection(application);
            var proof = ClassStorageWitness(owner, index, application, closeArgument, allowIntersection: exact);
            if (proof == null) return projected;
            // With no hidden argument the authored constructed class is itself a
            // genuine witness of the conjunction, even if no ancestor is one.
            return proof.Interfaces.Length == 0 ? proof.Class : Rewrite(application, preserveConstructedHead: true);
        }

        internal TypeNode.Fqn SharedClassWitness(PhysicalWitnessApplication owner, int index, TypeNode.Fqn application)
            => ClassStorageWitness(owner, index, application, null, allowIntersection: false)?.Class;

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
            readonly WitnessBinder binder;

            internal PhysicalWitnessApplication(WitnessBinder binder, TypeNode.Fqn owner, string identity,
                BirFunctionDeclaration localMethod, NullableRepresentationFrame layout,
                BirTypeParameter[] parameters, TypeNode[] arguments)
            {
                if ((parameters != null && parameters.Length != arguments.Length)
                    || (layout != null && layout.PhysicalArity != arguments.Length))
                    throw new InvalidOperationException($"Physical constraint frame disagrees with the selected application '{identity ?? owner?.Name}'");
                this.binder = binder;
                Owner = owner;
                LocalMethod = localMethod;
                Layout = layout;
                Parameters = parameters;
                Arguments = arguments;
            }

            internal string Scope => binder == WitnessBinder.Owner ? "type" : "method";

            internal TypeNode CloseBound(TypeNode bound, int index, Func<int, TypeNode> closeArgument)
            {
                var scope = Scope;
                var original = Arguments;
                TypeNode[] closed = null;
                BirGraphNameFields.VisitTypeNodes(bound, part => {
                    if (part is TypeNode.Tv tv && tv.Scope == scope && tv.I != index)
                        (closed ??= (TypeNode[])original.Clone())[tv.I] = closeArgument(tv.I);
                });
                var arguments = closed ?? original;
                return binder == WitnessBinder.Owner ? SupertypeGraph.SubstOwnerTvs(bound, arguments)
                    : SubstituteDeclarationTypeArguments(bound, Owner?.Args ?? [], arguments);
            }
        }

        internal PhysicalWitnessApplication SelectPhysicalClassApplication(TypeNode.Fqn application)
        {
            if (SelectLocalPhysicalClass(application.Name) is { } local)
                return new(WitnessBinder.Owner, application, null, null,
                    ClassLayout(local), TypeParameterFrame.Declarations(local).ToArray(), application.Args ?? []);
            return new(WitnessBinder.Owner, application, null, null,
                References.NullableTypeFrames.GetValueOrDefault(application.Name),
                ReferencedConstraintParameters(application.Name), application.Args ?? []);
        }

        internal PhysicalWitnessApplication SelectPhysicalMethodApplication(BirFunctionDeclaration local, string identity,
            NullableRepresentationFrame frame, TypeNode.Fqn owner, TypeNode[] arguments)
        {
            var parameters = local?.TypeParams.OrDefault();
            if (local == null && identity != null && !referencedMethodWitnessParameters.TryGetValue(identity, out parameters))
                referencedMethodWitnessParameters[identity] = parameters = References.PhysicalMethodTypeParametersInPhysicalFrame(identity);
            return new(WitnessBinder.Method, owner, identity, local, frame, parameters, arguments);
        }

        internal sealed record StorageWitness(TypeNode.Fqn Class, (int OwnerIndex, TypeNode.Fqn Bound)[] Required)
        {
            internal TypeNode.Fqn[] Interfaces => Required.Select(r => r.Bound).Distinct().ToArray();
        }

        BirTypeParameter[] ConstraintParameters(string owner)
        {
            if (Preparation.Bindings.SourceDefinitions.TryGetValue(owner, out var local))
                return TypeParameterFrame.Declarations((IBirTypeDeclaration)local.Source).ToArray();
            return ReferencedConstraintParameters(owner);
        }

        BirTypeParameter[] ReferencedConstraintParameters(string owner)
        {
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

        internal ProjectedClassStorageFrame StorageFrame(string owner) => StorageFrame(owner, out _);

        internal sealed record FixedSupertypeConstraint(int Index, TypeNode.Fqn Bound, string Owner);

        ProjectedClassStorageFrame StorageFrame(string owner, out FixedSupertypeConstraint[] fixedSlots)
        {
            fixedSlots = [];
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
            IEnumerable<(TypeNode.Fqn spec, bool isInterface)> parents =
                Preparation.SourceSupertypes.TryGetValue(owner, out var def)
                ? def.Interfaces.Select(f => (f, true)).Append((def.Base, false))
                : References.ReferencedSupertypes(new TypeNode.Fqn(owner, Enumerable.Range(0, parameters.Length)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray()));
            var required = new List<FixedSupertypeConstraint>();
            var nativeArguments = new HashSet<int>();
            foreach (var (parent, isInterface) in parents)
            {
                if (parent?.Args == null || isInterface && (Preparation.SourceSupertypes.ContainsKey(parent.Name)
                    || References.HasDotKtOwner(parent.Name))) continue;
                BirGraphNameFields.VisitTypeNodes(parent, node => {
                    if (node is not TypeNode.Fqn { Args: { Length: > 0 } args } head ||
                        ConstraintParameters(head.Name) is not { } formal || formal.Length != args.Length) return;
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
            bool Required(ProjectedClassStorageFrame.InterfaceSlot slot, FixedSupertypeConstraint r) =>
                r.Index == slot.OwnerIndex && slot.Bound is TypeNode.Fqn face && (r.Bound == face ||
                    SupertypeGraph.Reaches(face, r.Bound, Preparation.SourceSupertypes, References));
            fixedSlots = required.Where(r => frame.Interfaces.Any(s => Required(s, r))).Distinct().ToArray();
            if (nativeArguments.Count != 0) frame = frame.WithNativeArguments(nativeArguments);
            return fixedSlots.Length == 0 ? frame : frame.ForBase(s => required.Where(r => Required(s, r))
                .Select(r => (TypeNode)r.Bound).Distinct().ToArray());
        }

        internal TypeNode[] ProjectedConstructorArguments(PhysicalWitnessApplication application)
        {
            var layout = StorageFrame(application.Owner.Name, out var fixedSlots);
            return layout == null && fixedSlots.Length == 0 ? null :
                ProjectedConstructorArguments(application, layout, fixedSlots);
        }

        internal TypeNode[] ProjectedConstructorArguments(PhysicalWitnessApplication application, ProjectedClassStorageFrame layout,
            FixedSupertypeConstraint[] fixedSlots = null)
        {
            var owner = application.Owner;
            var primary = ((TypeNode.Fqn)Rewrite(owner, preserveConstructedHead: true)).Args;
            var visited = new bool[primary.Length];
            var required = new List<(int OwnerIndex, TypeNode.Fqn Bound)>();
            var unproven = false;
            TypeNode Close(int i)
            {
                if (!visited[i] && StripSourceNullability(application.Arguments[i]) is TypeNode.Fqn arg && ContainsExistentialProjection(arg))
                {
                    visited[i] = true;
                    if (ClassStorageWitness(application, i, arg, Close, allowIntersection: true) is { } proof)
                    {
                        if (proof.Interfaces.Length != 0 && fixedSlots?.Any(r => r.Index == i) == true)
                        {
                            var nativeBounds = fixedSlots.Where(r => r.Index == i).Select(r =>
                                (TypeNode.Fqn)application.CloseBound(r.Bound, i, Close)).ToArray();
                            bool Fits(TypeNode.Fqn candidate) => nativeBounds.All(b =>
                                SupertypeGraph.Reaches(candidate, b, Preparation.SourceSupertypes, References));
                            if (!Fits(proof.Class)) proof = ClassStorageWitness(application, i, arg, Close, true, Fits)
                                ?? throw new NotSupportedException($"cannot construct '{owner.Name}': CLR supertype '{string.Join(", ", fixedSlots.Where(r => r.Index == i).Select(r => r.Owner).Distinct())}' "
                                    + "requires a single type argument that satisfies both its class and interface constraints");
                        }
                        primary[i] = proof.Class;
                        required.AddRange(proof.Required);
                    }
                    else if (layout?.Interfaces.Any(slot => slot.OwnerIndex == i) == true)
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
            TypeNode FacetArgument(ProjectedClassStorageFrame.InterfaceSlot slot, TypeNode bound)
            {
                var actual = NativeArgument(slot.OwnerIndex);
                if (slot.NativeArgument) return actual;
                if (actual is not TypeNode.Fqn candidate || bound is not TypeNode.Fqn facet) return bound;
                synthesizedHierarchy ??= SupertypeGraph.CollectSource(Preparation.Bindings.SourceRoots
                    .Select(r => (BirFileDeclaration)r.Source));
                return SupertypeGraph.Reaches(candidate, facet, synthesizedHierarchy, References) ? actual : bound;
            }
            if (layout == null || unproven || required.Any(r => !layout.Interfaces.Any(s =>
                s.OwnerIndex == r.OwnerIndex && SupertypeGraph.SubstOwnerTvs(s.Bound, primary) == r.Bound))) return null;
            var arguments = layout.Close(primary, FacetArgument);
            if (required.Count == 0 && !layout.Interfaces.Select((slot, i) => (slot, witness: arguments[primary.Length + i]))
                .Any(row => ContainsExistentialProjection(application.Arguments[row.slot.OwnerIndex]) &&
                    row.witness == NativeArgument(row.slot.OwnerIndex) && row.witness != primary[row.slot.OwnerIndex])) return null;
            return arguments;
        }

        internal StorageWitness ClassStorageWitness(PhysicalWitnessApplication owner, int index, TypeNode.Fqn application,
            Func<int, TypeNode> closeArgument, bool allowIntersection, Func<TypeNode.Fqn, bool> acceptsPrimary = null)
        {
            TypeNode[] closed = null;
            closeArgument ??= i => (closed ??= ((TypeNode.Fqn)Rewrite(owner.Owner, preserveConstructedHead: true)).Args)[i];
            return SharedClassWitness(owner, index, application, closeArgument,
                allowIntersection: allowIntersection, acceptsPrimary: acceptsPrimary);
        }

        internal TypeNode MethodClassWitness(PhysicalWitnessApplication method, int index, TypeNode.Fqn application, TypeNode projected,
            Func<int, TypeNode> closeArgument)
        {
            return SharedClassWitness(method, index, application, closeArgument, method.LocalMethod == null ? null
                : (i, bound) => Rewrite(bound, NativeProofs.IsPhysicalConstraint(method.LocalMethod, i, bound)))?.Class ?? projected;
        }

        StorageWitness SharedClassWitness(PhysicalWitnessApplication frameApplication, int index, TypeNode.Fqn application,
            Func<int, TypeNode> closeArgument, Func<int, TypeNode, TypeNode> physicalBound = null,
            bool allowIntersection = false, Func<TypeNode.Fqn, bool> acceptsPrimary = null)
        {
            var parameters = frameApplication.Parameters;
            if (parameters == null) return null;
            var frame = frameApplication.Layout;
            var scope = frameApplication.Scope;
            var applicationArguments = frameApplication.Arguments;
            var linked = new HashSet<int> { index };
            if (frame != null)
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
            bool changed;
            do {
                changed = false;
                for (int i = 0; i < parameters.Length; i++)
                    foreach (var edge in parameters[i].Constraints.OrEmpty().OfType<TypeNode.Tv>())
                        if (edge.Scope == scope && applicationArguments[i] == applicationArguments[edge.I]
                            && (linked.Contains(i) || linked.Contains(edge.I)))
                        { changed |= linked.Add(i); changed |= linked.Add(edge.I); }
            } while (changed);
            if (linked.Any(i => parameters[i].SpecialConstraints.OrEmpty().Any(special => special != "class"))) return null;
            var declared = linked.SelectMany(i => parameters[i].Constraints.OrEmpty().Select(bound =>
                    (Index: i, Bound: physicalBound?.Invoke(i, bound) ?? bound)))
                .Where(r => r.Bound is not TypeNode.Tv tv || tv.Scope != scope || !linked.Contains(tv.I))
                .Select(r => (r.Index, Bound: StripSourceNullability(frameApplication.CloseBound(r.Bound, index, closeArgument)))).Distinct().ToArray();
            var bounds = declared.Select(r => r.Bound).Distinct().ToArray();
            if (bounds.Length == 0) return null;
            TypeNode Identity(TypeNode type) => type is TypeNode.Fqn f && !Preparation.SourceSupertypes.ContainsKey(f.Name)
                ? References.PhysicalConstraintType(type) : type;
            if (!bounds.OfType<TypeNode.Fqn>().Any(bound => !IsObjectish(bound) && BoundKind(bound) == "class")) return null;

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
            foreach (var (ancestor, isInterface) in supers.Reverse())
            {
                if (isInterface || ContainsExistentialProjection(ancestor) || acceptsPrimary?.Invoke(ancestor) == false) continue;
                var nominal = SupertypeGraph.ReachableFromClass(ancestor, Preparation.SourceSupertypes, References)
                    .Select(edge => Identity(edge.spec)).ToHashSet();
                var missing = bounds.Where(bound => !IsObjectish(bound) && !nominal.Contains(Identity(bound))).ToArray();
                if (missing.Length == 0) return new(ancestor, []);
                // The value, not its common base, must implement each additional interface.
                // Only a projected implementation with independent slots may use this proof.
                if (allowIntersection && split == null && missing.All(bound => bound is TypeNode.Fqn required
                    && !ContainsExistentialProjection(required) && supers.Any(edge => edge.isInterface
                        && Identity(edge.spec) == Identity(required))))
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
