using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Physical method formals remain in their own binder (!!i). An enclosing !i
// in a constraint must still be substituted from the selected declaring owner.
sealed record ProjectedMethodPlan(BirTypeParameter[] Parameters, ProjectedClassStorageFrame.WitnessSlot[] Facets, int[] AddressOperands)
{
    internal int[] NativeIndices { get; init; } = [];
    internal int[] ReferenceIndices { get; init; } = [];
    internal string RepresentationDeclaration { get; init; }
    // Each value-capable slot carries a reference-only choice and independent
    // upper witnesses for its nominal and material inputs. Those inputs need
    // not denote the same CLR type for star-projected Kotlin arguments.
    internal const int WeakWitnessWidth = 3;
    internal int PrimaryArity => Parameters.Length - Facets.Length - NativeIndices.Length - AddressOperands.Length - WeakWitnessWidth * ReferenceIndices.Length;

    internal int ReferencePosition(int primary) => Array.IndexOf(ReferenceIndices, primary) is var i && i >= 0
        ? Parameters.Length - WeakWitnessWidth * ReferenceIndices.Length + i : -1;

    internal int UpperPosition(int primary, bool material) => ReferencePosition(primary) is var i && i >= 0
        ? i + ReferenceIndices.Length * (material ? 2 : 1) : -1;

    internal IEnumerable<T> UpperArguments<T>(Func<int, bool, T> select) =>
        ReferenceIndices.Select(i => select(i, false)).Concat(ReferenceIndices.Select(i => select(i, true)));

    internal TypeNode NativeArgument(TypeNode argument) =>
        argument is TypeNode.Tv { Scope: "method" } tv && Array.IndexOf(NativeIndices, tv.I) is var i && i >= 0
            ? new TypeNode.Tv("method", PrimaryArity + Facets.Length + i) : argument;

    // Select at the declaring CLR slot, not recursively from an outer container:
    // e.g. an inner nominally constrained slot keeps its class witness even if
    // the outer container is invariant.
    internal static TypeNode NativeUses(TypeNode type, ReferenceMetadataIndex references,
        Func<TypeNode.Tv, BirTypeParameter, TypeNode[], bool, TypeNode> variable) =>
        SourceDeclarationSignature.RewriteType(type, owner => {
            if (owner.Args is not { Length: > 0 } args || references?.ResolveForeignProjectionType(owner.Name, args)
                is not { IsGenericTypeDefinition: true } foreign) return owner;
            var rows = foreign.GetGenericArguments();
            return owner with { Args = args.Select((arg, i) => {
                if (arg is not TypeNode.Tv v) return arg;
                var row = ReferenceMetadataReader.GenericParamDeclaration(rows[i], native: true);
                if (row.Constraints.OrEmpty().Select(c => SupertypeGraph.SubstOwnerTvs(c, args))
                    .OfType<TypeNode.Fqn>().Any(c => !FBoundStarProjectionErasure.IsObjectish(c) && references.IsClassType(c))) return arg;
                return variable(v, row, args, (rows[i].GenericParameterAttributes &
                    System.Reflection.GenericParameterAttributes.VarianceMask) == 0) ?? arg;
            }).ToArray() };
        });

    internal TypeNode NativeUse(TypeNode type, ReferenceMetadataIndex references) =>
        type == null || NativeIndices.Length == 0 ? type : NativeUses(type, references, (v, _, _, _) => NativeArgument(v));

    internal static bool HasMethodLocation(BirParameter parameter, ReferenceMetadataIndex references)
    {
        if (parameter.Type.OrDefault() is not TypeNode.ByRef reference) return false;
        // A managed ref to a stack-only CLR construction has a nominal, invariant
        // pointee. Keep that construction in the signature and method body: it
        // cannot be transported as an opaque, potentially boxed Kotlin value.
        if (reference.Of is TypeNode.Fqn f && references.IsByRefLikeFqn(f)) return false;
        bool dependsOnMethod = false;
        BirGraphNameFields.VisitTypeNodes(reference.Of, t => dependsOnMethod |= t is TypeNode.Tv { Scope: "method" });
        return dependsOnMethod;
    }

    internal static IEnumerable<TypeNode> MaterialSignature(BirParameter[] operands, TypeNode result) =>
        operands.Select(p => p.DelegateRestorationType ?? p.Type.OrDefault()).Append(result).Where(t => t != null);

    internal static ProjectedMethodPlan Declare(ProjectedClassStorageFrame frame, BirParameter[] operands,
        ReferenceMetadataIndex references, NullableRepresentationFrame representations, TypeNode result, Func<BirTypeParameter, bool> nominal,
        GenericRepresentationPolicy values, IEnumerable<int> independent)
    {
        var formals = frame.Declare().ToList();
        // Only the projected entry separates the nominal class witness from the
        // value's representation inside a nullable native argument. The nominal
        // witness need not implement that representation; source bounds remain.
        if (representations != null)
            foreach (var index in representations.NullableIndices)
            {
                var ordinary = representations.SourcePosition(index);
                var nullable = representations.NullableVariable(new TypeNode.Tv("method", index));
                var row = formals[ordinary];
                row.Constraints = row.Constraints.OrEmpty().Where(bound => bound != nullable).ToArray();
            }
        var facets = frame.Interfaces.Select((slot, i) =>
            new ProjectedClassStorageFrame.WitnessSlot(slot.OwnerIndex, slot.Bound, slot.NativeArgument) {
                Evidence = formals[frame.PrimaryArity + i].Constraints.OrEmpty() }).ToArray();
        var native = new SortedSet<int>();
        var requirements = new List<(int Source, BirTypeParameter Row, TypeNode[] Arguments)>();
        foreach (var type in MaterialSignature(operands, result))
            NativeUses(type, references, (v, row, args, invariant) => {
                if (v.Scope != "method") return null;
                if (invariant && (facets.Any(f => f.OwnerIndex == v.I) || nominal(formals[v.I]))) native.Add(v.I);
                requirements.Add((v.I, row, args));
                return null;
            });
        // The storage slot may represent several concrete types by one Kotlin
        // carrier. A native construction needs a distinct material argument;
        // its joint interface bounds are already part of the published frame.
        var materialBounds = new Dictionary<int, TypeNode[]>();
        var independentIndices = independent.Distinct().ToArray();
        IEnumerable<TypeNode> DeclaredProofs(int origin)
        {
            // A : K, K : I conveys the same storage-safe interface proof as
            // A : I. Carry that proof, not the nominal K chosen for a star.
            var pending = new Stack<int>();
            var seen = new HashSet<int>();
            pending.Push(origin);
            while (pending.TryPop(out var i))
            {
                if (!seen.Add(i)) continue;
                foreach (var facet in facets.Where(f => f.OwnerIndex == i)) yield return facet.Bound;
                foreach (var bound in formals[i].Constraints.OrEmpty())
                    if (bound is TypeNode.Tv { Scope: "method" } v) pending.Push(v.I);
                    else if (bound is TypeNode.Fqn named)
                        foreach (var proof in values.StorageBoundRows(named))
                            if (!proof.Erased) yield return proof.Bound;
            }
        }
        foreach (var ordinary in independentIndices)
        {
            native.Add(ordinary);
            IEnumerable<TypeNode> proofs = DeclaredProofs(ordinary);
            if (representations?.SourceIndex(ordinary) is int source &&
                representations.StorageIndices.Contains(source))
            {
                // The selected native slot can require storage-safe interfaces
                // supplied by either a nominal base or a Kotlin interface.
                var storage = representations.Variable(new TypeNode.Tv("method", source), NullableRepresentationFrame.Role.Storage);
                proofs = proofs.Concat(formals[storage.I].Constraints.OrEmpty());
            }
            materialBounds[ordinary] = proofs.Distinct().ToArray();
        }
        foreach (var group in facets.GroupBy(f => f.OwnerIndex))
        {
            var ordinary = group.Key;
            var bounds = group.Select(f => f.Bound).Distinct().ToArray();
            // The selected frame may join independently inherited slots. Its
            // constraints, not the implementation's substituted spelling, define the entry.
            if (!values.NeedsVirtualStorageWitness(formals[ordinary].Constraints.OrEmpty().Concat(bounds).OfType<TypeNode.Fqn>())) continue;
            native.Add(ordinary);
            materialBounds[ordinary] = (materialBounds.GetValueOrDefault(ordinary) ?? []).Concat(bounds).Distinct().ToArray();
        }
        // A dependent native row B : A must stay in the same frame as A.
        // The nominal B : A edge remains on the original Kotlin formals.
        bool changed;
        do {
            changed = false;
            foreach (var r in requirements)
                if (!native.Contains(r.Source) && r.Row.Constraints.OrEmpty().Any(c => {
                    bool depends = false;
                    BirGraphNameFields.VisitTypeNodes(SupertypeGraph.SubstOwnerTvs(c, r.Arguments), t =>
                        depends |= t is TypeNode.Tv { Scope: "method" } v && native.Contains(v.I));
                    return depends;
                })) changed |= native.Add(r.Source);
        } while (changed);
        // These formals carry the invariant argument, not the class witness.
        // Their constraints belong to the CLR constructions that use them.
        var firstNative = formals.Count;
        var indices = native.ToArray();
        TypeNode InNativeFrame(TypeNode arg) => TypedExecutableFrames.RewriteType(arg, v =>
            v.Scope == "method" && Array.IndexOf(indices, v.I) is var i && i >= 0
                ? new TypeNode.Tv("method", firstNative + i) : null);
        foreach (var index in indices)
        {
            var uses = requirements.Where(r => r.Source == index).ToArray();
            var row = new BirTypeParameter { Name = "$native$" + index };
            var bounds = uses.SelectMany(r => r.Row.Constraints.OrEmpty().Select(c =>
                SupertypeGraph.SubstOwnerTvs(c, r.Arguments.Select(InNativeFrame).ToArray()))).Concat(
                    (materialBounds.GetValueOrDefault(index) ?? []).Select(InNativeFrame)).Where(c =>
                    c != new TypeNode.Tv("method", formals.Count)).Distinct().ToArray();
            var ordinary = formals[index];
            // Splitting a nominal class witness from the material argument
            // does not turn a reference-only source parameter into a value type.
            var referenceOnly = materialBounds.ContainsKey(index) && (ordinary.SpecialConstraints.OrEmpty().Contains("class")
                || nominal(ordinary) && ordinary.Constraints.OrEmpty().OfType<TypeNode.Fqn>().All(bound =>
                    references.PhysicalConstraintType(bound) is not TypeNode.Fqn { Name: "System.ValueType" or "System.Enum" }));
            var special = uses.SelectMany(r => r.Row.SpecialConstraints.OrEmpty()).Concat(referenceOnly ? ["class"] : Array.Empty<string>())
                .Where(s => s != "allowsRefStruct").Distinct().ToArray();
            if (bounds.Length != 0) row.Constraints = bounds;
            if (special.Length != 0) row.SpecialConstraints = special;
            formals.Add(row);
        }
        var addresses = new List<int>();
        for (int i = 0; i < operands.Length; i++)
        {
            if (!HasMethodLocation(operands[i], references)) continue;
            addresses.Add(i);
            formals.Add(new BirTypeParameter { Name = "$location$" + i });
        }
        // A weak virtual slot can be implemented by either a value or a
        // reference-constrained receiver. Only the latter needs a separate
        // class proof when the material row itself does not already supply it.
        var referenceIndices = independentIndices.Where(i =>
            !formals[firstNative + Array.IndexOf(indices, i)].SpecialConstraints.OrEmpty().Contains("class")).ToArray();
        foreach (var i in referenceIndices)
            formals.Add(new BirTypeParameter { Name = "$referenceMaterial$" + i, SpecialConstraints = new[] { "class" } });
        foreach (var prefix in new[] { "$primaryUpper$", "$materialUpper$" })
            foreach (var i in referenceIndices)
                formals.Add(new BirTypeParameter { Name = prefix + i });
        return new(formals.ToArray(), facets, addresses.ToArray()) { NativeIndices = indices, ReferenceIndices = referenceIndices };
    }

    internal void ApplyAddressFormals(BirParameter[] parameters)
    {
        for (int i = 0; i < AddressOperands.Length; i++)
            parameters[AddressOperands[i]].Type = AddressFormal(i);
    }

    TypeNode AddressFormal(int i) => new TypeNode.ByRef(new TypeNode.Tv("method", PrimaryArity + Facets.Length + NativeIndices.Length + i));

    internal TypeNode[] SelectedSignature(TypeNode[] exact, ReferenceMetadataIndex references)
    {
        var selected = exact.Select(t => NativeUse(t, references)).ToArray();
        for (int i = 0; i < AddressOperands.Length; i++) selected[AddressOperands[i]] = AddressFormal(i);
        return selected;
    }

    internal TypeNode[] AddressArguments(TypeNode[] exact, TypeNode[] owner, TypeNode[] actual) =>
        AddressOperands.Select(i => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(
            ((TypeNode.ByRef)SignatureValueTypes.Of(exact[i])).Of, owner, actual)).ToArray();

    BirTypeParameter[] CopyRows() => Parameters.Select(p => (BirTypeParameter)p.DeepClone()).ToArray();

    internal BirTypeParameter[] CloneParameters()
    {
        var rows = CopyRows();
        foreach (var i in ReferenceIndices)
        {
            var material = ((TypeNode.Tv)NativeArgument(new TypeNode.Tv("method", i))).I;
            foreach (var (input, isMaterial) in new[] { (i, false), (material, true) })
                rows[input].Constraints = rows[input].Constraints.OrEmpty().Append(new TypeNode.Tv("method", UpperPosition(i, isMaterial))).ToArray();
        }
        return rows;
    }

    internal TypeNode[] ForwardTo(ProjectedMethodPlan target, Func<TypeNode,TypeNode> physicalKey)
    {
        var primary = PrimaryArity;
        if (primary != target.PrimaryArity) throw new InvalidOperationException("Projected override primary frames disagree");
        var slots = target.Facets.Select(t => Array.FindIndex(Facets,
            s => s.OwnerIndex == t.OwnerIndex && physicalKey(s.Bound) == physicalKey(t.Bound))).ToArray();
        if (slots.Any(s => s < 0)) throw new InvalidOperationException("Projected override lacks an inherited witness");
        var locations = target.AddressOperands.Select(i => Array.IndexOf(AddressOperands, i)).ToArray();
        if (locations.Any(i => i < 0)) throw new InvalidOperationException("Projected override lacks an inherited address");
        if (slots.Length == Facets.Length && slots.Select((s, i) => s == i).All(s => s)
            && NativeIndices.SequenceEqual(target.NativeIndices) && target.AddressOperands.SequenceEqual(AddressOperands)
            && ReferenceIndices.SequenceEqual(target.ReferenceIndices)) return null;
        var natives = target.NativeIndices.Select(i => Array.IndexOf(NativeIndices, i)).ToArray();
        if (natives.Any(i => i < 0)) throw new InvalidOperationException("Projected override lacks an invariant CLR argument");
        var references = target.ReferenceIndices.Select(ReferencePosition).ToArray();
        if (references.Any(i => i < 0)) throw new InvalidOperationException("Projected override lacks a reference-material proof");
        return Enumerable.Range(0, primary).Concat(slots.Select(s => primary + s)).Concat(natives.Select(i => primary + Facets.Length + i))
            .Concat(locations.Select(i => primary + Facets.Length + NativeIndices.Length + i))
            .Concat(references).Concat(target.UpperArguments(UpperPosition)).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
    }

    internal ProjectedMethodPlan OnOwner(TypeNode[] arguments)
    {
        var parameters = CopyRows();
        foreach (var p in parameters)
            if (p.Constraints.HasValue) p.Constraints = p.Constraints.Value.Select(c => SupertypeGraph.SubstOwnerTvs(c, arguments)).ToArray();
        return this with { Parameters = parameters, Facets = Facets.Select((f, i) => f with {
            Bound = SupertypeGraph.SubstOwnerTvs(f.Bound, arguments),
            Evidence = parameters[PrimaryArity + i].Constraints.OrEmpty() }).ToArray() };
    }

    internal sealed record Application(bool Projected, TypeNode[] Arguments);

    internal Application Close(TypeNode[] actual, TypeNode[] ownerArgs, BirMethodWitness[] candidates,
        BirTypeParameter[] original, NullableRepresentationFrame representations, bool requireProjectedEntry,
        Func<TypeNode, TypeNode, TypeNode[], TypeNode> lexicalProof, Func<TypeNode, TypeNode, bool> declares,
        Func<TypeNode, TypeNode, bool> proves)
    {
        var arguments = actual.ToArray();
        foreach (var candidate in candidates)
            if (Facets.Any(f => f.OwnerIndex == candidate.Argument.Value))
                arguments[candidate.Argument.Value] = candidate.Primary.Value;
        TypeNode Closed(TypeNode bound) => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(bound, ownerArgs, arguments);
        TypeNode Facet(ProjectedClassStorageFrame.WitnessSlot slot)
        {
            var bound = Closed(slot.Bound);
            var constraints = (slot.Evidence ?? [slot.Bound]).Select(Closed).ToArray();
            var evidence = candidates.FirstOrDefault(c => c.Argument.Value == slot.OwnerIndex);
            if (evidence != null)
            {
                var choices = new[] { actual[slot.OwnerIndex] }.Concat(evidence.Interfaces.Value).Append(evidence.Primary.Value);
                return choices.FirstOrDefault(t => constraints.All(c => proves(t, c)))
                    ?? throw new NotSupportedException("No single CLR type argument satisfies the projected method's interface constraints");
            }
            return lexicalProof(actual[slot.OwnerIndex], bound, constraints);
        }
        if (representations is { } map)
            for (var i = 0; i < actual.Length; i++)
            {
                var source = map.SourcePosition(map.PhysicalSlot(i).SourceIndex);
                if (source == i) continue;
                var bounds = original[i].Constraints.OrEmpty();
                if (bounds.Length != 1) continue;
                var bound = Closed(bounds[0]);
                if (lexicalProof(actual[source], bound, [bound]) is { } proof) arguments[i] = proof;
                else if (!declares(actual[i], bound) && declares(actual[source], bound)) arguments[i] = actual[source];
            }
        var proofs = Facets.Select(Facet).ToArray();
        if (proofs.All(p => p == null) && !requireProjectedEntry) return new(false, arguments);
        // Nominal and material arguments close independently. For B : A,
        // if A's class witness changed, B's nominal row can use that same
        // witness; its more precise CLR container argument remains separate.
        var closedNominal = new HashSet<int>();
        void CloseNominal(int i)
        {
            if (!closedNominal.Add(i) || Parameters[i].Constraints.OrEmpty() is not
                [TypeNode.Tv { Scope: "method" } upper] || !NativeIndices.Contains(upper.I)) return;
            CloseNominal(upper.I);
            if (arguments[upper.I] != actual[upper.I] && arguments[i] != arguments[upper.I] &&
                !declares(arguments[i], arguments[upper.I]) && !proves(arguments[i], arguments[upper.I]))
                arguments[i] = arguments[upper.I];
        }
        foreach (var i in NativeIndices) CloseNominal(i);
        // A projected callee may need just the interface, not the class.
        // Bind its ordinary parameter to evidence for the constraints on
        // that selected entry, leaving its independent nullable slot alone.
        if (representations != null)
            foreach (var source in representations.NullableIndices)
            {
                var i = representations.SourcePosition(source);
                var bounds = Parameters[i].Constraints.OrEmpty().Select(Closed).ToArray();
                if (actual[i] is TypeNode.Tv && bounds.Length != 0 && !bounds.All(c => declares(actual[i], c)) &&
                    lexicalProof(actual[i], bounds[0], bounds) is { } witness) arguments[i] = witness;
            }
        return new(true, arguments.Concat(proofs.Select((p, i) => p ?? actual[Facets[i].OwnerIndex])).ToArray());
    }
}
