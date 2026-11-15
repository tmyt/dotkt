using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // A current candidate view, not another declaration registry. Each reader
    // retains its actual method and uses the returned index to address it.
    sealed record LocalDelegateCandidate(int Index, string Name, string Identity, int Arity,
        TypeNode[] Parameters, TypeNode[] FunctionParameters);
    sealed record LocalDelegateSelection(int Index, TypeNode[] Parameters);

    static LocalDelegateSelection[] SelectLocalDelegateTargets(IReadOnlyList<LocalDelegateCandidate> candidates,
        string name, string identity, TypeNode[] wanted, TypeNode[] ownerArguments, TypeNode[] methodArguments,
        bool functionSignature, bool closure)
    {
        var matches = new List<LocalDelegateSelection>();
        foreach (var candidate in candidates)
        {
            if ((identity != null ? candidate.Identity != identity : candidate.Name != name)
                || candidate.Arity != methodArguments.Length || candidate.Parameters == null
                || candidate.Parameters.Length != wanted.Length || candidate.Parameters.Any(type => type == null)) continue;
            var selection = functionSignature ? candidate.FunctionParameters : candidate.Parameters;
            var closed = selection.Select(type => SupertypeGraph.SubstOwnerTvs(type, ownerArguments))
                .Select(type => SubstituteLocalDelegateMethodType(type, methodArguments)).ToArray();
            // An exact identity (or the physical closure's unique invoke) is
            // already selected. Its current physical signature may have moved.
            if (identity != null || closure || LocalDelegateKeys(selection).SequenceEqual(LocalDelegateKeys(wanted))
                || LocalDelegateKeys(closed).SequenceEqual(LocalDelegateKeys(wanted)))
                matches.Add(new(candidate.Index, candidate.Parameters));
        }
        return matches.ToArray();
    }

    static string[] LocalDelegateKeys(IEnumerable<TypeNode> types) => types.Select(SupertypeGraph.TypeKey).ToArray();

    // Shared with the genuine post-type DOM owner, retaining its exact
    // positional closure grammar rather than a second source-only matcher.
    static TypeNode SubstituteLocalDelegateMethodType(TypeNode type, TypeNode[] arguments) => type switch
    {
        TypeNode.Tv { Scope: "method" } variable when variable.I >= 0 && variable.I < arguments.Length => arguments[variable.I],
        TypeNode.Nullable nullable => new TypeNode.Nullable(SubstituteLocalDelegateMethodType(nullable.Of, arguments)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(SubstituteLocalDelegateMethodType(oblivious.Of, arguments)),
        TypeNode.Fqn { Args: { } nested } named => new TypeNode.Fqn(named.Name,
            nested.Select(argument => SubstituteLocalDelegateMethodType(argument, arguments)).ToArray()),
        TypeNode.Array array => new TypeNode.Array(SubstituteLocalDelegateMethodType(array.Elem, arguments)),
        TypeNode.ByRef reference => new TypeNode.ByRef(SubstituteLocalDelegateMethodType(reference.Of, arguments)),
        TypeNode.Fn function => new TypeNode.Fn(function.Suspend,
            SubstituteLocalDelegateMethodType(function.Ret, arguments),
            function.Params.Select(parameter => SubstituteLocalDelegateMethodType(parameter, arguments)).ToArray(),
            function.Recv == null ? null : SubstituteLocalDelegateMethodType(function.Recv, arguments), function.Clr,
            function.Ctx?.Select(context => SubstituteLocalDelegateMethodType(context, arguments)).ToArray()),
        _ => type,
    };

    internal static void ResolveLocalDelegateTargetsSource(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex references, IReadOnlyDictionary<string, string> localCarriers = null)
    {
        _refs = references ?? throw new ArgumentNullException(nameof(references));
        _localCarriers = localCarriers;
        var owners = new Dictionary<string, List<BirFunctionDeclaration>>(StringComparer.Ordinal);
        void Add(string owner, BirFunctionDeclaration[] methods)
        {
            if (!owners.TryGetValue(owner, out var declarations)) owners[owner] = declarations = new();
            declarations.AddRange(methods);
        }
        foreach (var root in roots)
        {
            if (root.FileClass.HasValue && root.Methods.HasValue) Add(root.FileClass.Value, root.Methods.Value);
            foreach (var type in root.Types.OrEmpty())
                if (type.Name.HasValue && type.Methods.HasValue) Add(type.Name.Value, type.Methods.Value);
        }
        var localTypes = owners.Keys.ToHashSet(StringComparer.Ordinal);
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
            var view = SourceLocalTarget(node);
            if (view == null || view.Owner is not TypeNode.Fqn owner || !owners.TryGetValue(owner.Name, out var methods)
                || view.Name == null || view.Wanted == null || view.Wanted.Any(type => type == null)
                || view.MethodArguments.Any(type => type == null)) return;
            var candidates = methods.Select((method, index) => {
                var parameters = method.Params.OrDefault();
                return new LocalDelegateCandidate(index, method.Name.OrDefault(), method.DeclarationId.OrDefault(),
                    method.TypeParams.OrEmpty().Length, parameters?.Select(parameter => parameter.Type.OrDefault()).ToArray(),
                    parameters?.Select(parameter => parameter.FunctionSignatureType.OrDefault() ?? parameter.Type.OrDefault()).ToArray());
            }).ToArray();
            var ownerArguments = owner.Args ?? Array.Empty<TypeNode>();
            var matches = SelectLocalDelegateTargets(candidates, view.Name, node.DeclarationIdentityFact.OrDefault(),
                view.Wanted, ownerArguments, view.MethodArguments, view.FunctionSignature, closure: false);
            if (matches.Length != 1 && node is BirNewDelegate or BirNewBoundDelegate)
                throw new InvalidOperationException($"bir2cir: {((BirGraphNode)node).Tag} target '{owner.Name}.{view.Name}' resolves to {matches.Length} exact local methods");
            if (matches.Length != 1) return;
            var chosen = matches[0]; var declaration = methods[chosen.Index];
            if (declaration.Params.Value.Any(parameter => parameter.FunctionSignatureType.HasValue))
                WriteSourceCalleeParameters(node, declaration.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray());
            // Only ldftn constructors name the method declaration itself.
            // Ordinary calls keep their caller-frame parameter descriptor.
            if (node is BirNewDelegate direct) direct.Sig = chosen.Parameters.Select(BirGraphValue.CopyType).ToArray();
            else if (node is BirNewBoundDelegate bound) bound.Sig = chosen.Parameters.Select(BirGraphValue.CopyType).ToArray();
            if (view.Arguments != null && chosen.Parameters.Length == view.Arguments.Length)
                for (var index = 0; index < chosen.Parameters.Length; index++)
                    MarkDelegateSlot((BirGraphValue)view.Arguments[index], CloseDelegateArgumentSlot(chosen.Parameters[index],
                        ownerArguments, view.MethodArguments), references, localTypes);
        }
        foreach (var root in roots) Walk(root);
    }

    sealed record SourceLocalTargetView(TypeNode Owner, string Name, TypeNode[] Wanted, TypeNode[] MethodArguments,
        bool FunctionSignature, IBirExpression[] Arguments);

    static SourceLocalTargetView SourceLocalTarget(BirGraphValue node) => node switch {
        BirNewDelegate call => new(call.CalleeOwner.OrDefault(), call.Method.OrDefault(), call.Sig.OrDefault(),
            call.TypeArgs.OrEmpty(), false, null),
        BirNewBoundDelegate call => new(call.CalleeOwner.OrDefault() ?? call.OwnerType.OrDefault(), call.Method.OrDefault(),
            call.Sig.OrDefault(), call.TypeArgs.OrEmpty(), false, null),
        BirCallStatic call => new(call.CalleeOwner.OrDefault() ?? call.Owner.OrDefault() ?? call.OwnerType.OrDefault(), call.Method.OrDefault(),
            call.FunctionCallSignature.OrDefault() ?? call.Sig.OrDefault(), call.TypeArgs.OrEmpty(), call.FunctionCallSignature.HasValue, call.Args.OrDefault()),
        BirCallInstance call => new(call.OwnerType.OrDefault(), call.Method.OrDefault(),
            call.FunctionCallSignature.OrDefault() ?? call.Sig.OrDefault(), call.TypeArgs.OrEmpty(), call.FunctionCallSignature.HasValue, call.Args.OrDefault()),
        BirConstrainedCall call => new(call.Iface.OrDefault(), call.Method.OrDefault(),
            call.FunctionCallSignature.OrDefault() ?? call.Sig.OrDefault(), call.TypeArgs.OrEmpty(), call.FunctionCallSignature.HasValue, call.Args.OrDefault()),
        // Initial binding deliberately excludes newClosure; physical delegate
        // adaptation runs later, after Unit/void and native slots are final.
        _ => null,
    };

    static void WriteSourceCalleeParameters(BirGraphValue node, TypeNode[] signature)
    {
        switch (node) {
            case BirNewDelegate value: value.CalleeParams = signature; break;
            case BirNewBoundDelegate value: value.CalleeParams = signature; break;
            case BirCallStatic value: value.CalleeParams = signature; break;
            case BirCallInstance value: value.CalleeParams = signature; break;
            case BirConstrainedCall value: value.CalleeParams = signature; break;
        }
    }
}
