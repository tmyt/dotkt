using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    sealed record LocalConstructorCandidate(int Index, string Identity, TypeNode[] Parameters,
        TypeNode[] DeclarationParameters);

    // One exact declaration-selection rule. The owning adapter below supplies
    // current physical rows; argument carriers never stand in for selected IDs
    // or the frontend's open declaration signature.
    static LocalConstructorCandidate[] SelectLocalConstructors(
        IReadOnlyList<LocalConstructorCandidate> candidates, int argumentCount, TypeNode[] wanted,
        string identity, TypeNode[] ownerArguments, bool declarationSignature, bool closeOwnerFrame)
    {
        if (wanted == null || wanted.Length != argumentCount || wanted.Any(type => type == null)) return [];
        return candidates.Where(candidate => {
            if (candidate.Parameters == null || candidate.Parameters.Length != argumentCount) return false;
            if (identity != null) return candidate.Identity == identity;
            var parameters = declarationSignature ? candidate.DeclarationParameters : candidate.Parameters;
            if (parameters.Any(type => type == null)) return false;
            return parameters.Select(type => closeOwnerFrame && ownerArguments.Length != 0
                    ? SupertypeGraph.SubstOwnerTvs(type, ownerArguments) : type)
                .Select(SupertypeGraph.TypeKey).SequenceEqual(wanted.Select(SupertypeGraph.TypeKey));
        }).ToArray();
    }

    static Dictionary<string, IBirTypeDeclaration> LocalConstructorDefinitions(
        IReadOnlyList<BirFileDeclaration> roots)
    {
        var definitions = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty())
                // Shared generated declarations can occur in multiple files.
                // Preserve the FIRST actual vector and its constructor ordinals.
                if (type.Name.HasValue) definitions.TryAdd(type.Name.Value, type);
        return definitions;
    }

    internal static void ResolveLocalConstructors(IReadOnlyList<BirFileDeclaration> roots)
    {
        var definitions = LocalConstructorDefinitions(roots);

        void Bind(BirGraphValue call, TypeNode.Fqn owner, IBirExpression[] arguments,
            TypeNode[] useSiteSignature, TypeNode[] memberSignature, string identity, bool delegation, string context)
        {
            if (!definitions.TryGetValue(owner.Name, out var target) || !target.Ctors.HasValue) return;
            if (!delegation)
            {
                if (useSiteSignature == null)
                    throw new InvalidDataException($"bir2cir: malformed current `new` node for {context}: required `argTypes` is absent or is not an array");
                if (useSiteSignature.Length != arguments.Length)
                    throw new InvalidDataException($"bir2cir: malformed current `new` node for {context}: `argTypes` count {useSiteSignature.Length} does not match `args` count {arguments.Length}");
                for (var index = 0; index < useSiteSignature.Length; index++)
                    if (useSiteSignature[index] == null)
                        throw new InvalidDataException($"bir2cir: malformed current `new` node for {context}: `argTypes[{index}]` is not a structured Type node");
            }
            var candidates = target.Ctors.Value.Select((constructor, index) => {
                var parameters = constructor.Params.OrDefault();
                return new LocalConstructorCandidate(index, constructor.DeclarationId.OrDefault(),
                    parameters?.Select(parameter => parameter?.Type.OrDefault()).ToArray(),
                    parameters?.Select(parameter => parameter?.FunctionSignatureType.OrDefault()
                        ?? parameter?.Type.OrDefault()).ToArray());
            }).ToArray();
            var ownerArguments = owner.Args ?? [];
            // A delegation signature names the target declaration's frame.
            // Only an unselected use-site new.argTypes closes that frame while
            // matching; closing an open selected signature would merge overloads.
            var selected = SelectLocalConstructors(candidates, arguments.Length, memberSignature ?? useSiteSignature,
                identity, ownerArguments, declarationSignature: memberSignature != null || delegation,
                closeOwnerFrame: memberSignature == null && !delegation);
            if (selected.Length != 1)
                throw new InvalidOperationException($"bir2cir: {context} resolves to {selected.Length} exact local constructors on '{owner.Name}'");
            var winner = selected[0];
            switch (call)
            {
                case BirNew construction: construction.LocalCtorIndex = winner.Index; break;
                case BirConstructor constructor: constructor.LocalCtorIndex = winner.Index; break;
            }
            if (winner.Parameters.Length == arguments.Length && winner.Parameters.All(type => type != null))
            {
                var targets = winner.Parameters.Select(type => BirGraphValue.CopyType(ownerArguments.Length == 0
                    ? type : SupertypeGraph.SubstOwnerTvs(type, ownerArguments))).ToArray();
                if (useSiteSignature != null && useSiteSignature.Length == arguments.Length)
                    for (var index = 0; index < arguments.Length; index++)
                        if (useSiteSignature[index] is { } flowed && arguments[index] is { } argument
                            && CollectionViewFaces.IsViewSeam(flowed, targets[index]))
                            arguments[index] = new BirCast { Type = BirGraphValue.CopyType(targets[index]),
                                E = OptionalField<IBirExpression>.Of(argument) };
                switch (call)
                {
                    case BirNew construction:
                        construction.ArgTypes = targets;
                        // The old stamping consumer reads `args`, not the
                        // constructor declaration's thisArgs/baseArgs vectors.
                        for (var index = 0; index < arguments.Length; index++)
                            MarkDelegateSlot((BirGraphValue)arguments[index], CloseDelegateArgumentSlot(
                                winner.Parameters[index], ownerArguments, []), _refs, _localTypes);
                        break;
                    case BirConstructor constructor: constructor.DelegationSig = targets; break;
                }
            }
            switch (call)
            {
                case BirNew construction:
                    construction.MemberSignature = default;
                    construction.ConstructorDeclarationId = default;
                    break;
                case BirConstructor constructor: constructor.DelegationDeclarationId = default; break;
            }
        }

        // Delegation binding precedes the complete postorder construction walk.
        foreach (var type in definitions.Values)
        {
            var own = new TypeNode.Fqn(type.Name.Value);
            var baseType = type.Base.OrDefault() as TypeNode.Fqn;
            foreach (var constructor in type.Ctors.OrEmpty())
            {
                if (constructor.ThisArgs.HasValue)
                    Bind(constructor, own, constructor.ThisArgs.Value, constructor.DelegationSig.OrDefault(),
                        null, constructor.DelegationDeclarationId.OrDefault(), true, $"this-delegation in {own.Name}");
                else if (baseType != null && definitions.ContainsKey(baseType.Name) && constructor.BaseArgs.HasValue)
                    Bind(constructor, baseType, constructor.BaseArgs.Value, constructor.DelegationSig.OrDefault(),
                        null, constructor.DelegationDeclarationId.OrDefault(), true, $"base-delegation in {own.Name}");
            }
        }

        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
            if (node is BirNew construction && construction.Type.OrDefault() is TypeNode.Fqn owner
                && definitions.ContainsKey(owner.Name) && construction.Args.HasValue)
                Bind(construction, owner, construction.Args.Value, construction.ArgTypes.OrDefault(),
                    construction.MemberSignature.OrDefault(), construction.ConstructorDeclarationId.OrDefault(), false,
                    $"new {owner.Name}");
        }
        foreach (var root in roots) Walk(root);
    }
}
