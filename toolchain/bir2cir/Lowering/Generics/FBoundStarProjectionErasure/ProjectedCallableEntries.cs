using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Exact Kotlin declarations keep their CLR constraints. Projected callers
// may select a companion body with separate class and interface witnesses.
// Both entries are published by the producer; callers use declaration IDs,
// never a search for a matching member name or the runtime type of a value.
sealed class ProjectedCallableEntries
{
    sealed record Entry(string Name, ProjectedClassStorageFrame Frame, BirTypeParameter[] Original,
        NullableRepresentationFrame Representations);
    sealed record Body(IBirTypeDeclaration Owner, BirFunctionDeclaration Method, Entry Entry);
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> kinds;
    readonly ReferenceMetadataIndex references;
    readonly bool referenceBuild;

    static string EntryId(string id) => DeclarationIdentityBinding.PhysicalOnlyId(id, "constraint-call");

    internal ProjectedCallableEntries(BirFileDeclaration[] files, ReferenceMetadataIndex references, bool referenceBuild)
    {
        this.references = references;
        this.referenceBuild = referenceBuild;
        kinds = files.SelectMany(f => f.Types.OrEmpty()).Where(t => t.Name.HasValue)
            .ToLookup(t => t.Name.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind.OrDefault(), StringComparer.Ordinal);
        var bodies = new List<Body>();
        var localSlots = new Dictionary<string, ReferencedDeclarationSlot>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Methods.HasValue) file.Methods = Declare(file.Methods.Value, null, bodies, localSlots);
            foreach (var type in file.Types.OrEmpty())
                if (type.Methods.HasValue) type.Methods = Declare(type.Methods.Value, type, bodies, localSlots);
        }
        var hierarchy = bodies.Any(b => b.Method.SelectedGenericOverrides is { Count: > 0 })
            ? SupertypeGraph.CollectSource(files) : null;
        foreach (var (owner, method, entry) in bodies)
        {
            method.Visit(n => Bind(n, "method", entry.Original, entry.Frame.Interfaces));
            if (owner == null || method.SelectedGenericOverrides is not { Count: > 0 } slots) continue;
            foreach (var id in slots.Distinct(StringComparer.Ordinal))
            {
                bool local = localSlots.TryGetValue(id, out var slot);
                if (!local && !references.TryDeclarationSignatureSlot(EntryId(id), out slot)) continue;
                var constructed = SupertypeGraph.Reachable(hierarchy[owner.Name.Value], hierarchy, references)
                    .Select(edge => edge.spec).Distinct().Single(s => s.Name == slot.Owner);
                TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, constructed.Args ?? []);
                var descriptor = new BirMethodImpl { Owner = constructed, Member = slot.Name, Arity = slot.Arity,
                    Params = slot.Parameters.Select(Close).ToArray(), Ret = Close(slot.Return) };
                if (!local && !slot.IsInterface)
                {
                    var selected = references.SelectedPhysicalMethod(EntryId(id)) ??
                        throw new InvalidOperationException("Selected projected base MethodDef is absent");
                    descriptor.MemberRef = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, references,
                        selected, "method", selected.DeclaringType, constructed.Args));
                }
                if (slot.IsInterface) method.ClrInterfaceImpls = method.ClrInterfaceImpls.OrEmpty().Append(descriptor).ToArray();
                else
                {
                    method.ClrBaseImpls = method.ClrBaseImpls.OrEmpty().Append(descriptor).ToArray();
                    if (method.Name.Value == slot.Name) method.Override = true;
                }
            }
        }
    }

    ProjectedClassStorageFrame Frame(BirTypeParameter[] parameters) => ProjectedClassStorageFrame.Plan(parameters,
        (_, bound) => (bound, kinds.GetValueOrDefault(bound.Name) ?? (references.IsInterfaceType(bound) ? "interface" :
            references.IsClassType(bound) || references.PhysicalTypeForProof(bound.Name, bound.Args?.Length ?? 0)?.IsClass == true
                ? "class" : null)));

    BirFunctionDeclaration[] Declare(BirFunctionDeclaration[] source, IBirTypeDeclaration owner, List<Body> bodies,
        Dictionary<string, ReferencedDeclarationSlot> slots)
    {
        var result = new List<BirFunctionDeclaration>(source);
        var names = source.Select(m => m.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        foreach (var m in source)
        {
            var identity = m.RoundtripDeclarationIdentity.OrDefault();
            if (m.HasNativeOverrideSlot || m.RequiresClrOverride.OrDefault() || identity?.DeclarationId.OrDefault() is not string id ||
                id.Contains(DeclarationIdentityBinding.PhysicalOnlySuffix, StringComparison.Ordinal) ||
                m.TypeParams.OrEmpty() is not { Length: > 0 } parameters || Frame(parameters) is not { } frame) continue;
            // This is also the override point for C# subclasses: use an identifier
            // that C# source can name. The binding is still by identity, not suffix.
            var name = m.Name.Value + "Projected";
            for (var i = 0; !names.Add(name); i++) name = m.Name.Value + "Projected" + i;
            var entry = new Entry(name, frame, parameters, identity.NullableRepresentationFrame.OrDefault());
            entries.Add(id, entry);
            var copy = (BirFunctionDeclaration)m.DeepClone();
            copy.Name = name;
            copy.Generated = true;
            copy.TypeParams = frame.Declare();
            copy.ClrInterfaceImpls = default;
            copy.ClrBaseImpls = default;
            copy.ClrOverrideRef = default;
            copy.RequiresClrOverride = default;
            copy.Override = false;
            copy.Attrs = default;
            copy.RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity {
                DeclarationId = EntryId(id), DeclarationSourceName = name };
            if (owner != null && !m.Static.OrDefault() && (m.Virtual.OrDefault() || m.Override.OrDefault() || m.Abstract.OrDefault()))
                MakeExactFacade(owner, m, copy, frame);
            result.Add(copy);
            bodies.Add(new(owner, copy, entry));
            if (owner != null && !copy.Static.OrDefault())
                slots.Add(id, new(owner.Name.Value, name, copy.TypeParams.Value.Length, copy.Params.OrEmpty()
                    .Select(FunctionSignatureIdentity.SignatureType).ToArray(), copy.Ret.Value,
                    owner.Kind.OrDefault() == "interface", false));
        }
        return result.ToArray();
    }

    // There is only one virtual slot. The exact entry retains both constraints
    // and forwards its already-proved arguments, even when called from C#.
    void MakeExactFacade(IBirTypeDeclaration owner, BirFunctionDeclaration exact,
        BirFunctionDeclaration implementation, ProjectedClassStorageFrame frame)
    {
        exact.FacadeModalityFlags = exact.Abstract.OrDefault() ? KotlinFacadeModality.Abstract :
            exact.ClrFinal.OrDefault() ? 0 : KotlinFacadeModality.Open;
        exact.Abstract = false;
        exact.Virtual = false;
        exact.Override = false;
        exact.ClrFinal = default;
        exact.ClrBaseImpls = default;
        exact.ClrInterfaceImpls = default;
        if (referenceBuild) { exact.Body = RefBodySquash.SourceThrowStubBody(); return; }
        var ownerArity = TypeParameterFrame.Count(owner);
        var ownerType = new TypeNode.Fqn(owner.Name.Value, ownerArity == 0 ? null :
            Enumerable.Range(0, ownerArity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        var original = Enumerable.Range(0, exact.TypeParams.Value.Length).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
        var call = new BirCallInstance { OwnerType = ownerType, Method = implementation.Name, Virtual = true,
            Recv = new BirThis(), Ret = exact.Ret, CalleeRet = exact.Ret,
            TypeArgs = original.Concat(frame.Interfaces.Select(slot => original[slot.OwnerIndex])).ToArray(),
            Sig = exact.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray(),
            Args = exact.Params.OrEmpty().Select(p => (IBirExpression)new BirLocal { Name = p.Name }).ToArray() };
        exact.Body = exact.Ret.Value is TypeNode.Fqn { Name: "void" or "System.Void" }
            ? new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }, new BirReturn() }
            : new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(call) } };
    }

    internal void BindExactSuperCalls(BirFileDeclaration[] files)
    {
        foreach (var file in files) file.Visit(n => {
            if (n is BirCallInstance s && s.Super.OrDefault()) Bind(n, "", [], []);
        });
    }

    Entry Find(string id)
    {
        if (entries.TryGetValue(id, out var found)) return found;
        if (!references.TryDeclarationIdentity(EntryId(id), out var name, out _, out _, out _) ||
            references.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true) is not { } parameters ||
            Frame(parameters) is not { } frame) return null;
        return entries[id] = new(name, frame, parameters, references.NullableMethodFrame(id));
    }

    internal bool Bind(BirGraphValue node, string scope, BirTypeParameter[] parameters,
        ProjectedClassStorageFrame.InterfaceSlot[] available)
    {
        if (node.SelectedGenericCallable is not { } id ||
            node.ApplicationTypeArguments.OrDefault() is not { } actual || Find(id) is not { } target ||
            actual.Length != target.Original.Length) return false;
        var member = node switch {
            BirCallStatic s => s.MemberRef.OrDefault(), BirCallInstance c => c.MemberRef.OrDefault(),
            BirConstrainedCall c => c.MemberRef.OrDefault(), _ => null };
        var ownerArgs = (member?.DeclaringType.OrDefault() as TypeNode.Fqn)?.Args ?? [];
        TypeNode Closed(TypeNode bound) => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(bound, ownerArgs, actual);
        TypeNode Proof(TypeNode ordinary, TypeNode bound)
        {
            if (ordinary is not TypeNode.Tv v || v.Scope != scope) return null;
            var i = Array.FindIndex(available, slot => slot.OwnerIndex == v.I && slot.Bound.Equals(bound));
            return i < 0 ? null : new TypeNode.Tv(scope, parameters.Length + i);
        }
        var proofs = target.Frame.Interfaces.Select(slot => Proof(actual[slot.OwnerIndex], Closed(slot.Bound))).ToArray();
        var arguments = actual.ToArray();
        bool Declares(TypeNode argument, TypeNode bound) => argument is TypeNode.Tv v && v.Scope == scope
            && v.I < parameters.Length && parameters[v.I].Constraints.OrEmpty().Contains(bound);
        if (target.Representations is { } map)
            for (var i = 0; i < actual.Length; i++)
            {
                var source = map.SourcePosition(map.PhysicalSlot(i).SourceIndex);
                if (source == i) continue;
                var bounds = target.Original[i].Constraints.OrEmpty();
                if (bounds.Length != 1) continue;
                var bound = Closed(bounds[0]);
                if (Proof(actual[source], bound) is { } proof) arguments[i] = proof;
                else if (!Declares(actual[i], bound) && Declares(actual[source], bound)) arguments[i] = actual[source];
            }
        if (proofs.All(p => p == null) && !(node is BirCallInstance super && super.Super.OrDefault()))
        {
            node.ApplicationTypeArguments = arguments;
            return true;
        }
        switch (node) {
            case BirCallStatic s: s.Method = target.Name; break;
            case BirCallInstance c: c.Method = target.Name; break;
            case BirConstrainedCall c: c.Method = target.Name; break;
            default: throw new InvalidOperationException("Expected a selected generic call");
        }
        node.ApplicationTypeArguments = arguments.Concat(proofs.Select((p, i) => p ?? actual[target.Frame.Interfaces[i].OwnerIndex])).ToArray();
        if (member != null)
        {
            member.Name = target.Name;
            member.GenericArity = node.ApplicationTypeArguments.Value.Length;
        }
        return true;
    }
}
