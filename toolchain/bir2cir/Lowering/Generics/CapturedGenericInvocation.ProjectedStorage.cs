using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using System.Reflection;

static partial class CapturedGenericInvocation
{
    sealed record LexicalTarget(TypeNode.Fqn Owner, BirTypeParameter[] OwnerFrame, BirFunctionDeclaration Method);
    sealed record CaptureRow(int Argument, TypeNode Owner, int Position, int Slot, int Nullable, bool StorageOnly);

    // Runtime sees only declaration-authorized coordinates. Do not infer other
    // invariant slots from the values or allocate a table for ordinary calls.
    static IBirExpression CaptureOwners(CaptureRow[] rows) => rows is { Length: > 0 }
        ? NewArray(Type, rows.Select(r => ClassRef(r.Owner))) : Null();
    static IBirExpression CaptureCoordinates(CaptureRow[] rows, int argumentOffset = 0) => rows is { Length: > 0 }
        ? NewArray(Int, rows.SelectMany(r => new[] { r.Argument + argumentOffset, r.Position, r.Slot, r.Nullable })
            .Prepend(rows.Count(r => !r.StorageOnly)).Select(Number)) : Null();

    static int[] LexicalCandidates(TypeNode[] actual, CallableWitnessScope owner, CallableWitnessScope method) =>
        Enumerable.Range(0, actual.Length).Where(i => CallableWitnessScope.For(actual[i], owner, method).Any()
            || CallableWitnessScope.LostNullableBounds(actual[i], owner, method).Length != 0).ToArray();

    sealed record FixedApplication(TypeNode[] Arguments, (int Slot, TypeNode Bound)[] Facets);

    static IEnumerable<TypeNode> LostDependencies(BirTypeParameter row, TypeNode[] ownerArgs, TypeNode[] actual,
        TypeNode.Tv[] lost) => lost.Length == 0 ? [] : row.Constraints.OrEmpty().OfType<TypeNode.Tv>()
            .Select(v => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(v, ownerArgs, actual))
            .Where(t => t is TypeNode.Tv v && lost.Contains(v)).Distinct();

    static FixedApplication BindFixedWitnesses(BirTypeParameter[] frame, TypeNode[] signature, TypeNode[] ownerArgs, TypeNode[] actual, BirMethodWitness[] explicitProofs,
        int[] candidates, CallableWitnessScope owner, CallableWitnessScope method, Func<TypeNode.Fqn,bool> isInterface,
        Dictionary<int, TypeNode> locations = null)
    {
        var arguments = explicitProofs.Length != 0 || locations != null ? actual.ToArray() : actual;
        foreach (var proof in explicitProofs) arguments[proof.Argument.Value] = proof.Primary.Value;
        if (locations != null)
            foreach (var (operand, storage) in locations)
            {
                var pointee = (TypeNode.ByRef)SignatureValueTypes.Of(signature[operand]);
                arguments[((TypeNode.Tv)pointee.Of).I] = storage;
            }
        // Reopen a lexical argument only for its own interface obligation
        // or a dependency on a recovered argument (Y : X).
        var lost = explicitProofs.Select(p => p.Argument.Value).Concat(candidates.Where(i =>
            frame[i].Constraints.OrEmpty().OfType<TypeNode.Fqn>().Any(isInterface))).ToHashSet();
        var dependent = new List<(int Slot, TypeNode Bound)>();
        foreach (var i in candidates)
        {
            foreach (var upper in LostDependencies(frame[i], ownerArgs, actual, CallableWitnessScope.LostNullableBounds(actual[i], owner, method)))
            {
                lost.Add(i);
                dependent.Add((i, upper));
            }
        }
        int previous;
        do {
            previous = lost.Count;
            foreach (var i in candidates)
                if (!lost.Contains(i) && frame[i].Constraints.OrEmpty().Any(t => ContainsCapturedSlot(t, lost))) lost.Add(i);
        } while (previous != lost.Count);
        var lexical = (from i in candidates where lost.Contains(i)
            from proof in CallableWitnessScope.For(actual[i], owner, method) select (i, proof)).ToArray();
        // Preserve an explicit occurrence's evidence. Within lexical evidence,
        // prefer declared bounds. A live variable may prove a conjunction
        // that none of the individual declared bounds satisfies.
        var facets = explicitProofs.SelectMany(p => p.Interfaces.Value.Select(b => (p.Argument.Value, b)))
            .Concat(lexical.Select(p => (p.i, p.proof.Slot.Bound)))
            .Concat(lexical.Select(p => (p.i, (TypeNode)p.proof.Variable))).Concat(dependent).Distinct().ToArray();
        return new(arguments, facets);
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

    static Dictionary<int,TypeNode> FixedAddresses(TypeNode[] signature, IBirExpression[] values, IReadOnlyDictionary<string, TypeNode> locations)
    {
        if (locations == null || values == null || signature.Length != values.Length) return null;
        Dictionary<int,TypeNode> result = null;
        for (int i = 0; i < values.Length; i++)
            if (SignatureValueTypes.Of(signature[i]) is TypeNode.ByRef { Of: TypeNode.Tv { Scope: "method" } }
                && (values[i] is BirByRefOf r ? r.Inner.OrDefault() : values[i]) is BirLocal local
                && locations.TryGetValue(local.Name.Value, out var storage)) (result ??= new()).Add(i, storage);
        return result;
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
        var fixedAddresses = FixedAddresses(member.ParameterTypes.Value, values, locations);
        var candidates = LexicalCandidates(arguments, ownerScope, methodScope);
        if ((candidates.Length == 0 && fixedAddresses == null) || !TrySelectedMethod(site, owner, refs, out var selected)) return call;
        var methodFrame = AddressTarget.ReadFrame(selected.GetGenericArguments());
        var application = BindFixedWitnesses(methodFrame, member.ParameterTypes.Value, owner.Args ?? [], arguments, [], candidates, ownerScope, methodScope, t => refs.IsInterfaceType(t), fixedAddresses);
        if (application.Facets.Length == 0 && fixedAddresses == null) return call;
        var target = AddressTarget.FromReference(selected, member, methodFrame);
        bool typed = (receiver != null && target.ValueOwner) || target.TypedParameters.Contains(true) || target.TypedResult;
        var unit = selected.ReturnType.FullName == "System.Void";
        LexicalTarget lexicalTarget = null;
        if (access == null && (site.Super || typed && !selected.IsPublic))
        {
            // Neither reflection's virtual Invoke nor a sibling adapter may
            // issue a nonvirtual base call. Protected access likewise belongs
            // to the lexical type. Retain the selected call in that context.
            TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, owner.Args ?? []);
            var frame = target.MethodFrame.Select(p => CloneParameter(p, Close)).ToArray();
            var signature = member.ParameterTypes.Value.Select(t => Close(SignatureValueTypes.Of(t))).ToArray();
            var ret = Close(SignatureValueTypes.Of(member.ReturnType.Value));
            var forward = site.Instance ? site.SuperForward(signature, ret, frame.Length, false) :
                site.Forward(owner, frame.Length, signature, ret, false);
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
        if (typed || access != null) return ProjectAddressCall(site, target,
            owner, application.Facets, refs, allocate, generated, lexicalTarget, access, application.Arguments, fixedAddresses);

        // The original native method is selected at compile time. Open only
        // its type argument on the actual object at the call boundary: the
        // projected class's primary slot guarantees the class, not its interfaces.
        var result = FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(member.ReturnType.Value, owner.Args ?? [], arguments);
        return IntersectionCall(site, lexicalTarget?.Owner ?? owner, lexicalTarget == null ? selected.MetadataToken : 0,
            lexicalTarget?.Method.Name.Value ?? selected.Name,
            target.Keys, application.Arguments, application.Facets, result, unit, refs);
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
