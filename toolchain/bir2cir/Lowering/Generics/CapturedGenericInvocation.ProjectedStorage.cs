using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using System.Reflection;

static partial class CapturedGenericInvocation
{
    sealed record LexicalTarget(TypeNode.Fqn Owner, BirTypeParameter[] OwnerFrame, BirFunctionDeclaration Method);
    sealed record CaptureRow(int Argument, TypeNode Owner, int Position, int Slot, int Nullable);

    // Runtime sees only declaration-authorized coordinates. Do not infer other
    // invariant slots from the values or allocate a table for ordinary calls.
    static IBirExpression CaptureOwners(CaptureRow[] rows) => rows is { Length: > 0 }
        ? NewArray(Type, rows.Select(r => ClassRef(r.Owner))) : Null();
    static IBirExpression CaptureCoordinates(CaptureRow[] rows) => rows is { Length: > 0 }
        ? NewArray(Int, rows.SelectMany(r => new[] { r.Argument, r.Position, r.Slot, r.Nullable }).Select(Number)) : Null();

    // An unrelated lexical split cannot reopen an exact CLR argument. A slot
    // needs recovery only for its own interface obligation or a bound on a
    // slot that was already reopened (Y : X).
    static int[] LostLexicalSlots(int[] candidates, IEnumerable<int> explicitSlots, Func<int,bool> hasInterface,
        Func<int,HashSet<int>,bool> dependsOn)
    {
        var lost = explicitSlots.Concat(candidates.Where(hasInterface)).ToHashSet();
        int previous;
        do {
            previous = lost.Count;
            foreach (var i in candidates)
                if (!lost.Contains(i) && dependsOn(i, lost)) lost.Add(i);
        } while (previous != lost.Count);
        return candidates.Where(lost.Contains).ToArray();
    }

    static bool TrySelectedMethod(Invocation site, TypeNode.Fqn owner, ReferenceMetadataIndex refs,
        out System.Reflection.MethodInfo selected)
    {
        var member = site.Member;
        // MemberRef already contains the declaring TypeDef and the open CLR signature.
        // Kotlin display types (in particular function views of delegates) are not its key.
        var declaring = refs.SelectedPhysicalMethod(site.Node.SelectedGenericCallable)?.DeclaringType
            ?? refs.PhysicalTypeNamed(owner.Name, owner.Args?.Length ?? 0)
            ?? ClrMemberResolution.ResolveOwnerType(owner, refs);
        var flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
            | (site.Instance ? BindingFlags.Instance : BindingFlags.Static);
        selected = declaring?.GetMethods(flags).Where(m => m.Name == member.Name.Value
            && m.GetGenericArguments().Length == member.GenericArity.Value
            && m.GetParameters().Length == member.ParameterTypes.Value.Length).SingleOrDefault(m =>
                member.StructurallyEquals(BirMemberReference.FromPhysical(
                    ClrMemberResolution.MemberRefOf(null, refs, m, "method", declaring, owner.Args))));
        return selected != null;
    }

    static Invocation SelectedInvocation(Invocation site, out BirCallStatic access)
    {
        access = null;
        if (site.Node is BirCallStatic wrapper && wrapper.NativeAccessorTarget is { } accessed)
        {
            access = wrapper;
            var m = (BirMemberReference)accessed.Member.DeepClone();
            if (m.DeclaringType.Value is TypeNode.Fqn { Args.Length: > 0 } declared &&
                site.Owner is TypeNode.Fqn { Args: { } applied }) m.DeclaringType = declared with { Args = applied };
            var args = wrapper.Args.Value[1..];
            site = new Invocation(accessed.Static
                ? new BirCallStatic { Owner = m.DeclaringType, Method = m.Name, TypeArgs = wrapper.TypeArgs,
                    Args = args, MemberRef = m, Ret = wrapper.Ret }
                : new BirCallInstance { OwnerType = m.DeclaringType, Method = m.Name, TypeArgs = wrapper.TypeArgs,
                    Args = args, Recv = OptionalField<IBirExpression>.Of(wrapper.Args.Value[0]), MemberRef = m, Ret = wrapper.Ret });
        }
        return site;
    }

    internal static BirGraphValue ProjectStorageCall(BirGraphValue call,
        CallableWitnessScope ownerScope, CallableWitnessScope methodScope,
        ReferenceMetadataIndex refs, Func<string,string> allocate, List<IBirTypeDeclaration> generated, IBirTypeDeclaration lexical,
        List<BirFunctionDeclaration> lexicalMethods, IReadOnlyDictionary<string, TypeNode> locations)
    {
        if (!Invocation.Admits(call)) return call;
        var site = SelectedInvocation(new Invocation(call), out var access);
        var (member, arguments, values, receiver) = (site.Member, site.TypeArguments.OrDefault(),
            site.Arguments.OrDefault(), site.Receiver);
        if (member?.DeclaringType.OrDefault() is not TypeNode.Fqn owner || arguments == null || values == null) return call;
        TypeNode Location(IBirExpression value) => (value is BirByRefOf r ? r.Inner.OrDefault() : value) is BirLocal local
            ? locations?.GetValueOrDefault(local.Name.Value) : null;
        Dictionary<int, TypeNode> fixedAddresses = null;
        if (locations != null && member.ParameterTypes.Value.Length == values.Length)
            for (int i = 0; i < values.Length; i++)
                if (SignatureValueTypes.Of(member.ParameterTypes.Value[i]) is TypeNode.ByRef { Of: TypeNode.Tv { Scope: "method" } }
                    && Location(values[i]) is { } pointee) (fixedAddresses ??= new()).Add(i, pointee);
        var candidates = arguments.Select((argument, index) => (argument, index))
            .Where(row => CallableWitnessScope.For(row.argument, ownerScope, methodScope).Any())
            .ToArray();
        if ((candidates.Length == 0 && fixedAddresses == null) || !TrySelectedMethod(site, owner, refs, out var selected)) return call;
        var generic = selected.GetGenericArguments();
        bool UsesLostSlot(System.Type type, HashSet<int> lost) =>
            type.IsGenericParameter ? type.DeclaringMethod != null && lost.Contains(type.GenericParameterPosition)
            : type.HasElementType ? UsesLostSlot(type.GetElementType(), lost)
            : type.IsGenericType && type.GetGenericArguments().Any(t => UsesLostSlot(t, lost));
        var missing = LostLexicalSlots(candidates.Select(row => row.index).ToArray(), [],
            i => generic[i].GetGenericParameterConstraints().Any(t => t.IsInterface),
            (i, lost) => generic[i].GetGenericParameterConstraints().Any(t => UsesLostSlot(t, lost)));
        if (missing.Length == 0 && fixedAddresses == null) return call;
        // A different parameter can force this storage class to split. Preserve
        // each authored native argument whose own primary still proves its facets.
        var proofs = (from i in missing
            from evidence in CallableWitnessScope.For(arguments[i], ownerScope, methodScope)
            select (Index: i, Evidence: evidence)).ToArray();
        // Prefer the declared interface. A single actual witness may also
        // discharge a conjunction that none of the declared facets proves alone.
        var facets = proofs.Select(p => (Slot: p.Index, Bound: p.Evidence.Slot.Bound))
            .Concat(proofs.Select(p => (Slot: p.Index, Bound: (TypeNode)p.Evidence.Variable)))
            .Distinct().ToArray();
        var formal = selected.GetParameters();
        bool typed = (receiver != null && selected.DeclaringType.IsValueType)
            || formal.Any(p => RequiresTypedSlot(p.ParameterType)) || RequiresTypedSlot(selected.ReturnType);
        var unit = selected.ReturnType.FullName == "System.Void";
        LexicalTarget lexicalTarget = null;
        if (access == null && (site.Super || typed && !selected.IsPublic))
        {
            // Neither reflection's virtual Invoke nor a sibling adapter may
            // issue a nonvirtual base call. Protected access likewise belongs
            // to the lexical type. Retain the selected call in that context.
            TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, owner.Args ?? []);
            var frame = generic.Select(p => CloneParameter(ReferenceMetadataReader.GenericParamDeclaration(p, native: true), Close)).ToArray();
            var signature = member.ParameterTypes.Value.Select(t => Close(SignatureValueTypes.Of(t))).ToArray();
            var ret = Close(SignatureValueTypes.Of(member.ReturnType.Value));
            var forward = site.Instance ? site.SuperForward(signature, ret, generic.Length, false) :
                site.Forward(owner, generic.Length, signature, ret, false);
            var number = lexicalMethods.Count;
            string lexicalName;
            do lexicalName = "dotkt$projectedCall$" + number++;
            while (lexical.Methods.OrEmpty().Concat(lexicalMethods).Any(m => m.Name.OrDefault() == lexicalName));
            var method = Method(lexicalName, frame, signature, ret, Body(forward, unit, false), !site.Instance, typed ? "internal" : "private");
            lexicalMethods.Add(method);
            var ownerFrame = TypeParameterFrame.Declarations(lexical).ToArray();
            lexicalTarget = new(new TypeNode.Fqn(lexical.Name.Value, ownerFrame.Length == 0 ? null : Enumerable.Range(0, ownerFrame.Length)
                .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray()), ownerFrame, method);
        }
        if (typed || access != null) return ProjectAddressCall(site, AddressTarget.FromReference(selected, member),
            owner, facets, refs, allocate, generated, lexicalTarget, access, arguments, fixedAddresses);

        // The original native method is selected at compile time. Open only
        // its type argument on the actual object at the call boundary: the
        // projected class's primary slot guarantees the class, not its interfaces.
        var result = FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(member.ReturnType.Value, owner.Args ?? [], arguments);
        return IntersectionCall(site, lexicalTarget?.Owner ?? owner, lexicalTarget == null ? selected.MetadataToken : 0,
            lexicalTarget?.Method.Name.Value ?? selected.Name,
            formal.Select(p => ReferenceMetadataIndex.ForeignStarRuntimeTypeKey(p.ParameterType)), arguments, facets, result, unit, refs);
    }

    static BirGraphValue IntersectionCall(Invocation site, TypeNode.Fqn owner, int token, string method,
        IEnumerable<string> parameterKeys, TypeNode[] arguments, (int Slot, TypeNode Bound)[] facets,
        TypeNode result, bool unit, ReferenceMetadataIndex refs, CaptureRow[] captures = null)
    {
        var name = unit ? "starProjectionInvokeIntersectionUnit" : "starProjectionInvokeIntersection";
        var runtimeType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var lookup = new TypeNode[] { any, runtimeType, new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.String"),
            new TypeNode.Array(new TypeNode.Fqn("kotlin.String")), new TypeNode.Array(runtimeType), new TypeNode.Fqn("kotlin.IntArray"),
            new TypeNode.Array(runtimeType), new TypeNode.Array(any), new TypeNode.Nullable(new TypeNode.Array(runtimeType)),
            new TypeNode.Nullable(new TypeNode.Fqn("kotlin.IntArray")) };
        if (!refs.TryResolveStaticMemberSignature(RuntimeOwner, name, 0, true, lookup, null, out _, out var helper, out var declaring))
            throw new InvalidOperationException($"Projected invocation has no runtime binding for '{name}'");
        var target = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs, helper, "method", declaring, null));
        var invoke = new BirNativeStatic {
            Type = new TypeNode.Fqn(RuntimeOwner), Method = name, MemberRef = target,
            Ret = target.ReturnType, ResolvedMemberReturn = target.ReturnType,
            Args = new IBirExpression[] { site.Receiver ?? Null(), ClassRef(owner), Number(token), Text(method),
                NewArray(String, parameterKeys.Select(p => (IBirExpression)Text(p))),
                NewArray(Type, arguments.Select(t => (IBirExpression)ClassRef(t))),
                NewArray(Int, facets.Select(f => (IBirExpression)Number(f.Slot))),
                NewArray(Type, facets.Select(f => (IBirExpression)ClassRef(f.Bound))), NewArray(Object, site.Arguments.Value),
                CaptureOwners(captures), CaptureCoordinates(captures) },
        };
        ForeignStarProjectionBinding.RequireRuntimeFallback();
        return unit || IsObject(result) ? invoke : Cast(result, invoke);
    }
}
