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
    readonly record struct Scope(BirTypeParameter[] Parameters, ProjectedClassStorageFrame.InterfaceSlot[] Slots);
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly Dictionary<string, Entry> emittedEntries = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> kinds;
    readonly Dictionary<string, BirClassDefinition> classes;
    readonly ReferenceMetadataIndex references;
    readonly Lazy<IReadOnlyDictionary<string, SupertypeGraph.Def>> classHierarchy;
    readonly bool referenceBuild;

    static string EntryId(string id) => DeclarationIdentityBinding.PhysicalOnlyId(id, "constraint-call");

    internal ProjectedCallableEntries(BirFileDeclaration[] files, ReferenceMetadataIndex references, bool referenceBuild)
    {
        this.references = references;
        classHierarchy = new(() => SupertypeGraph.CollectSource(files));
        this.referenceBuild = referenceBuild;
        classes = files.SelectMany(f => f.Types.OrEmpty()).OfType<BirClassDefinition>()
            .Where(t => t.Name.HasValue).ToLookup(t => t.Name.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
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
        // Access stubs must describe the selected projected MethodDef exactly,
        // including its extra generic witnesses. Keep the target private.
        BirFunctionDeclaration[] DeclareAccessors(BirFunctionDeclaration[] methods)
        {
            List<BirFunctionDeclaration> result = null;
            foreach (var m in methods)
            {
                if (m.ProjectedAccessBinding is not { } binding || Find(binding.Target) is not { } target) continue;
                result ??= new(methods);
                var name = m.Name.Value + "Projected";
                for (var i = 0; result.Any(f => f.Name.OrDefault() == name); i++) name = m.Name.Value + "Projected" + i;
                var entry = target with { Name = name };
                var id = EntryId(binding.Accessor);
                entries.Add(binding.Accessor, entry);
                emittedEntries.Add(id, entry);
                var copy = (BirFunctionDeclaration)m.DeepClone();
                copy.Name = name;
                copy.TypeParams = entry.Frame.Declare();
                copy.ProjectedAccessBinding = null;
                if (copy.Extern.OrDefault()) DeclarationIdentityBinding.RewriteUnsafeAccessorTarget(copy, target.Name);
                else copy.RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity { DeclarationId = id, DeclarationSourceName = name };
                result.Add(copy);
            }
            return result?.ToArray() ?? methods;
        }
        foreach (var file in files)
        {
            if (file.Methods.HasValue) file.Methods = DeclareAccessors(file.Methods.Value);
            foreach (var type in file.Types.OrEmpty())
                if (type.Methods.HasValue) type.Methods = DeclareAccessors(type.Methods.Value);
        }
        var hierarchy = bodies.Any(b => b.Method.SelectedGenericOverrides is { Count: > 0 })
            ? classHierarchy.Value : null;
        foreach (var (owner, method, entry) in bodies)
        {
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
            var bodyEntry = m.LocalBodyEntry;
            if (m.HasNativeOverrideSlot || m.RequiresClrOverride.OrDefault() || (bodyEntry?.Id ?? identity?.DeclarationId.OrDefault()) is not string id ||
                bodyEntry == null && id.Contains(DeclarationIdentityBinding.PhysicalOnlySuffix, StringComparison.Ordinal) ||
                m.TypeParams.OrEmpty() is not { Length: > 0 } parameters || Frame(parameters) is not { } frame) continue;
            // This is also the override point for C# subclasses: use an identifier
            // that C# source can name. The binding is still by identity, not suffix.
            var name = m.Name.Value + "Projected";
            for (var i = 0; !names.Add(name); i++) name = m.Name.Value + "Projected" + i;
            var entry = new Entry(name, frame, parameters, bodyEntry?.Frame ?? identity?.NullableRepresentationFrame.OrDefault());
            entries.Add(id, entry);
            emittedEntries.Add(EntryId(id), entry);
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
            if (bodyEntry is { } local) copy.LocalBodyEntry = (EntryId(id), local.Frame);
            else copy.RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity {
                DeclarationId = EntryId(id), DeclarationSourceName = name };
            if (owner != null && !m.Static.OrDefault())
            {
                if (m.SourceCallableVirtual) MakeExactFacade(owner, m, copy, frame);
                else if (m.Virtual.OrDefault())
                {
                    // The exact method implements a compiler-generated carrier
                    // slot, but the source member has no override point.
                    m.ClrFinal = true;
                    copy.Virtual = false;
                    copy.ClrFinal = default;
                }
            }
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
        // A generic owner's existential interface may still require the exact
        // signature. A final CLR slot implements it without exposing a second
        // override point. An interface's own exact entry is a nonvirtual default.
        exact.Virtual = owner.Kind.OrDefault() != "interface";
        exact.Override = false;
        exact.ClrFinal = exact.Virtual.OrDefault();
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

    void BindExactSuperCalls(BirFileDeclaration[] files)
    {
        foreach (var file in files) file.Visit(n => {
            if (n is BirCallInstance s && s.Super.OrDefault()) Bind(n, default, default);
        });
    }

    // A native MethodSpec can mention both the owning class's split slots
    // and those of the current Kotlin method. Bind its witnesses once, with
    // both scopes live, before either kind of argument can be erased.
    internal void RewriteBody(BirGraphValue root, IBirTypeDeclaration owner, BirTypeParameter[] ownerParameters,
        ProjectedClassStorageFrame.InterfaceSlot[] ownerSlots, Func<string,string> allocate,
        List<IBirTypeDeclaration> generated, List<BirFunctionDeclaration> helpers)
    {
        var ownerScope = new Scope(ownerParameters, ownerSlots);
        BirGraphValue Rewrite(BirGraphValue node, Scope methodScope)
        {
            if (node is IBirExecutablePayload or BirMemberReference) return node;
            if (node is BirFunctionDeclaration method)
                methodScope = MethodEntry(method) is { } entry ? new(entry.Original, entry.Frame.Interfaces) :
                    new(method.TypeParams.OrEmpty(), []);
            node.VisitChildSlots(slot => slot.Replace(Rewrite(slot.Value, methodScope)));
            if (node is BirNew construction && ReferencedConstruction(construction, ownerScope, methodScope) is { } factory)
                return factory;
            if (node is BirNewClosure or BirNewSam)
                ProjectClosureCreation(node, ownerScope, methodScope, allocate, generated);
            if (Bind(node, ownerScope, methodScope)) return node;
            return CapturedGenericInvocation.ProjectStorageCall(node, ownerSlots, methodScope.Slots ?? [], references, allocate, generated, owner, helpers);
        }
        Rewrite(root, default);
    }

    Entry MethodEntry(BirFunctionDeclaration method) =>
        (method.LocalBodyEntry?.Id ?? method.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault()) is string id
            ? emittedEntries.GetValueOrDefault(id) : null;

    internal void BindExactOwnerMethods(IBirTypeDeclaration owner, BirTypeParameter[] ownerParameters)
    {
        foreach (var method in owner.Methods.OrEmpty())
            if (MethodEntry(method) == null)
                method.Visit(n => Bind(n, new(ownerParameters, []), new(method.TypeParams.OrEmpty(), [])));
    }

    internal void Finish(BirFileDeclaration[] files, HashSet<string> emittedTypes)
    {
        if (referenceBuild || emittedEntries.Count == 0) { BindExactSuperCalls(files); return; }
        foreach (var file in files)
        {
            var generated = new List<IBirTypeDeclaration>();
            void Process(BirFunctionDeclaration[] methods, IBirTypeDeclaration owner)
            {
                var helpers = new List<BirFunctionDeclaration>();
                var ordinal = 0;
                string Allocate(string role) {
                    string name;
                    do name = (owner?.Name.Value ?? file.FileClass.Value) + "$" + role + ordinal++; while (!emittedTypes.Add(name));
                    return name;
                }
                foreach (var method in methods)
                    if (MethodEntry(method) != null) RewriteBody(method, owner,
                        owner == null ? [] : TypeParameterFrame.Declarations(owner).ToArray(), [], Allocate, generated, helpers);
                    else if (method.LocalBodyEntry.HasValue)
                        method.Visit(n => Bind(n, new(owner == null ? [] : TypeParameterFrame.Declarations(owner).ToArray(), []),
                            new(method.TypeParams.OrEmpty(), [])));
                if (helpers.Count != 0) owner.Methods = owner.Methods.OrEmpty().Concat(helpers).ToArray();
            }
            Process(file.Methods.OrEmpty(), null);
            foreach (var type in file.Types.OrEmpty()) Process(type.Methods.OrEmpty(), type);
            if (generated.Count != 0) file.Types = file.Types.OrEmpty().Concat(generated).ToArray();
        }
        BindExactSuperCalls(files);
    }

    Entry Find(string id)
    {
        if (entries.TryGetValue(id, out var found)) return found;
        if (!references.TryDeclarationIdentity(EntryId(id), out var name, out _, out _, out _) ||
            references.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true) is not { } parameters ||
            Frame(parameters) is not { } frame) return null;
        return entries[id] = new(name, frame, parameters, references.NullableMethodFrame(id));
    }

    TypeNode Proof(TypeNode ordinary, TypeNode bound, Scope ownerScope, Scope methodScope)
    {
        if (ordinary is not TypeNode.Tv v) return null;
        var scope = v.Scope == "method" ? methodScope : ownerScope;
        var i = Array.FindIndex(scope.Slots ?? [], slot => slot.OwnerIndex == v.I && slot.Bound.Equals(bound));
        if (i < 0 && bound is TypeNode.Fqn required)
            i = Array.FindIndex(scope.Slots ?? [], slot => slot.OwnerIndex == v.I && slot.Bound is TypeNode.Fqn available
                && SupertypeGraph.Reaches(available, required, classHierarchy.Value, references));
        return i < 0 ? null : new TypeNode.Tv(v.Scope, scope.Parameters.Length + i);
    }

    internal TypeNode[] ForwardOwnerConstructor(ProjectedConstructorPlan plan, TypeNode[] actual,
        BirTypeParameter[] parameters, ProjectedClassStorageFrame.InterfaceSlot[] slots)
        => ForwardConstructor(plan, actual, new(parameters, slots), default);

    TypeNode[] ForwardConstructor(ProjectedConstructorPlan plan, TypeNode[] actual, Scope owner, Scope method)
    {
        if (!plan.Facets.Any(f => actual[f.Primary] is TypeNode.Tv tv &&
                (tv.Scope == "method" ? method : owner).Slots?.Any(s => s.OwnerIndex == tv.I) == true)) return null;
        return plan.Close(actual, (f, required) => Proof(actual[f.Primary], required, owner, method) ?? actual[f.Primary]);
    }

    BirCallStatic ReferencedConstruction(BirNew creation, Scope owner, Scope method)
    {
        if (creation.ReferencedProjectedFactoryId is not { } id ||
            creation.Type.OrDefault() is not TypeNode.Fqn { Args: { } actual }) return null;
        var selected = references.SelectedPhysicalMethod(id);
        var plan = references.ConstructorPlan(id, nativeNames: true) ?? throw new InvalidOperationException($"No projected constructor for '{id}'");
        if (ForwardConstructor(plan, actual, owner, method) is not { } arguments) return null;
        var target = ClrMemberResolution.MemberRefOf(null, references, selected, "method", selected.DeclaringType, null);
        var signature = target.ParameterTypes.Select(t =>
            FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(t, [], arguments)).ToArray();
        return new BirCallStatic { Owner = target.DeclaringType, Method = target.Name, TypeArgs = arguments,
            Args = creation.Args, Sig = signature, Ret = target.ReturnType, MemberRef = BirMemberReference.FromPhysical(target) };
    }

    void ProjectClosureCreation(BirGraphValue node, Scope owner, Scope method, Func<string,string> allocate,
        List<IBirTypeDeclaration> generated)
    {
        if (method.Slots is not { Length: > 0 } && owner.Slots is not { Length: > 0 }) return;
        var target = node is BirNewClosure c ? c.ClosureType.OrDefault() : ((BirNewSam)node).SamType.OrDefault();
        if (target is not TypeNode.Fqn named || node.ApplicationTypeArguments.OrDefault() is not { } args) return;
        BirClassDefinition Definition(string name) => classes.GetValueOrDefault(name)
            ?? generated.OfType<BirClassDefinition>().FirstOrDefault(t => t.Name.OrDefault() == name);
        if (Definition(named.Name) is not { } original) return;
        var parameters = TypeParameterFrame.Declarations(original).ToArray();
        if (parameters.Length != args.Length || Frame(parameters) is not { } frame) return;
        var prefix = original.CapturedTypeParams.OrEmpty().Length;
        var split = frame.Interfaces.Where(s => s.OwnerIndex >= prefix && Proof(args[s.OwnerIndex],
            SupertypeGraph.SubstOwnerTvs(s.Bound, args), owner, method) != null).ToArray();
        if (split.Length == 0) return;
        var copy = (BirClassDefinition)original.DeepClone();
        copy.Name = allocate("closure");
        copy.RoundtripTypeHeaderPending = default;
        copy.RoundtripSupertypesPre = default;
        var full = ProjectedClassStorageFrame.Declare(parameters, split);
        copy.TypeParams = full[prefix..];
        var children = new List<IBirTypeDeclaration>();
        FBoundStarProjectionErasure.ProjectLexicalClosures(copy, named.Name, split.Length, full, Definition, allocate, children);
        copy.Visit(n => n.RewriteOwnTypeFieldsInPlace((field, _, type) =>
            n is BirMemberReference && field != "declaringType" ? type :
            SourceDeclarationSignature.RewriteType(type, f => f.Name == named.Name && f.Args?.Length == args.Length
                ? new TypeNode.Fqn(copy.Name.Value, f.Args.Concat(Enumerable.Range(args.Length, split.Length)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i))).ToArray()) : f)));
        foreach (var closure in children.Append(copy).OfType<BirClassDefinition>())
        {
            classes.Add(closure.Name.Value, closure);
            var helpers = new List<BirFunctionDeclaration>();
            RewriteBody(closure, closure, parameters, split, allocate, generated, helpers);
            if (helpers.Count != 0) closure.Methods = closure.Methods.OrEmpty().Concat(helpers).ToArray();
            generated.Add(closure);
        }
        node.ApplicationTypeArguments = args.Concat(split.Select(s => Proof(args[s.OwnerIndex],
            SupertypeGraph.SubstOwnerTvs(s.Bound, args), owner, method))).ToArray();
        if (node is BirNewClosure lambda) lambda.ClosureType = new TypeNode.Fqn(copy.Name.Value);
        else ((BirNewSam)node).SamType = new TypeNode.Fqn(copy.Name.Value);
    }

    bool Bind(BirGraphValue node, Scope ownerScope, Scope methodScope)
    {
        if (node.SelectedGenericCallable is not { } id ||
            node.ApplicationTypeArguments.OrDefault() is not { } actual || Find(id) is not { } target ||
            actual.Length != target.Original.Length) return false;
        var member = node switch {
            BirCallStatic s => s.MemberRef.OrDefault(), BirCallInstance c => c.MemberRef.OrDefault(),
            BirNewDelegate d => d.MemberRef.OrDefault(), BirNewBoundDelegate d => d.MemberRef.OrDefault(),
            BirConstrainedCall c => c.MemberRef.OrDefault(), _ => null };
        var ownerArgs = (member?.DeclaringType.OrDefault() as TypeNode.Fqn)?.Args ?? [];
        TypeNode Closed(TypeNode bound) => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(bound, ownerArgs, actual);
        var proofs = target.Frame.Interfaces.Select(slot => Proof(actual[slot.OwnerIndex], Closed(slot.Bound), ownerScope, methodScope)).ToArray();
        var arguments = actual.ToArray();
        bool Declares(TypeNode argument, TypeNode bound) => argument is TypeNode.Tv v
            && (v.Scope == "method" ? methodScope : ownerScope).Parameters is { } parameters
            && v.I < parameters.Length && parameters[v.I].Constraints.OrEmpty().Contains(bound);
        if (target.Representations is { } map)
            for (var i = 0; i < actual.Length; i++)
            {
                var source = map.SourcePosition(map.PhysicalSlot(i).SourceIndex);
                if (source == i) continue;
                var bounds = target.Original[i].Constraints.OrEmpty();
                if (bounds.Length != 1) continue;
                var bound = Closed(bounds[0]);
                if (Proof(actual[source], bound, ownerScope, methodScope) is { } proof) arguments[i] = proof;
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
            case BirNewDelegate d: d.Method = target.Name; break;
            case BirNewBoundDelegate d: d.Method = target.Name; break;
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
