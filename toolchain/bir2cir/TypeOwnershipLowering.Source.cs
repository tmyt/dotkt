using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypeOwnershipLowering
{
    internal sealed class PreparedFacts
    {
        IReadOnlyDictionary<string, IBirTypeDeclaration> sourceTypes;
        internal IReadOnlyDictionary<string, IBirTypeDeclaration> SourceTypes => sourceTypes
            ?? throw new InvalidOperationException("Ownership source declarations have been released");

        internal PreparedFacts(IReadOnlyDictionary<string, IBirTypeDeclaration> types) => sourceTypes = types;

        // The post-inline continuation reads the same actual declarations as
        // initial preparation. Once every file has continued, no later owner
        // needs these source references and the module frontier releases them.
        internal void ReleaseSourceDeclarations()
        {
            if (sourceTypes == null) throw new InvalidOperationException("Ownership source declarations are already released");
            sourceTypes = null;
        }
    }

    // Lifted types start in Kotlin order [own..., owner..., other captured...].
    // CLR nested TypeDefs require the owner segment first. Local functions remain
    // lexical localFun declarations and are handled by LocalFunctionLowering.
    public static PreparedFacts PrepareOwnershipFacts(IReadOnlyList<BirFileDeclaration> roots)
    {
        var types = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var type in roots.SelectMany(root => root.Types.OrEmpty()))
            if (type.Name.OrDefault() is string name) types[name] = type;
        var prepared = new PreparedFacts(types);
        NormalizeOwnerCapturePrefixes(roots, types);
        foreach (var root in roots) PrepareSyntheticOwnerCaptures(root, types);
        return prepared;
    }

    // Only this file acquired new ingredients during inline expansion. Reuse
    // the initial live owner map, not a module rescan or another prefix pass.
    public static void PrepareSplicedOwnershipFacts(BirGraphValue root, PreparedFacts prepared) =>
        PrepareSyntheticOwnerCaptures(root, prepared.SourceTypes);

    static void RewriteOwnership(BirGraphValue root, Func<TypeNode, TypeNode> rewrite, bool lexicalOnly, bool syntheticFrame = false)
    {
        if (root is IBirExecutablePayload) return;
        bool Includes(BirOwnershipRole role) => role != BirOwnershipRole.FrozenFact
            && (!lexicalOnly || role != BirOwnershipRole.ForeignVector)
            && (!syntheticFrame || role is not (BirOwnershipRole.MemberDescriptor or BirOwnershipRole.SyntheticDeclaration));
        root.RewriteOwnershipInPlace((role, type) => Includes(role) ? rewrite(type) : type,
            (role, child) => { if (Includes(role)) RewriteOwnership(child, rewrite, lexicalOnly, syntheticFrame); });
    }

    static void NormalizeOwnerCapturePrefixes(IReadOnlyList<BirFileDeclaration> roots, IReadOnlyDictionary<string, IBirTypeDeclaration> types)
    {
        var permutations = new Dictionary<string, (int[] NewToOld, int[] OldToNew)>(StringComparer.Ordinal);
        foreach (var type in roots.SelectMany(root => root.Types.OrEmpty()).OfType<BirClassDefinition>())
        {
            if (!type.OuterTypeParamOffset.HasValue || type.OuterTypeParamOffset.Value == 0)
            { type.OuterTypeParamOffset = default; continue; }
            var offset = type.OuterTypeParamOffset.Value;
            if (!type.OuterTypeParamCount.HasValue || type.OuterTypeParamCount.Value <= 0 || !type.TypeParams.HasValue
                || offset < 0 || offset + type.OuterTypeParamCount.Value > type.TypeParams.Value.Length)
                throw new InvalidOperationException($"Kotlin type '{type.Name.OrDefault()}' has an invalid semantic owner parameter segment");
            var count = type.OuterTypeParamCount.Value;
            var parameters = type.TypeParams.Value;
            var newToOld = Enumerable.Range(offset, count).Concat(Enumerable.Range(0, offset))
                .Concat(Enumerable.Range(offset + count, parameters.Length - offset - count)).ToArray();
            var oldToNew = Inverse(newToOld);
            type.TypeParams = newToOld.Select(index => (BirTypeParameter)parameters[index].DeepClone()).ToArray();
            type.OuterTypeParamOffset = default;
            RewriteOwnership(type, value => RemapOwnerSlots(value, oldToNew), lexicalOnly: true);
            if (type.Name.OrDefault() is string name) permutations[name] = (newToOld, oldToNew);
        }
        // A child copied its owner's original frame, so compose the owner's full
        // permutation into that copied prefix. Slot positions, not parameter
        // names, identify correspondence: Kotlin allows name shadowing.
        var completed = new Dictionary<string, (int[] NewToOld, int[] OldToNew)>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        (int[] NewToOld, int[] OldToNew) Resolve(string name, IBirTypeDeclaration type)
        {
            if (completed.TryGetValue(name, out var known)) return known;
            if (!visiting.Add(name)) throw new InvalidOperationException($"cyclic semantic owner chain at Kotlin type '{name}'");
            try
            {
                var count = type.TypeParams.OrEmpty().Length;
                var direct = permutations.TryGetValue(name, out var selected) ? selected
                    : (NewToOld: Enumerable.Range(0, count).ToArray(), OldToNew: Enumerable.Range(0, count).ToArray());
                var newToOld = direct.NewToOld;
                if (type.SemanticOwner.OrDefault() is string ownerName && types.TryGetValue(ownerName, out var owner)
                    && type is BirClassDefinition { OuterTypeParamCount.HasValue: true } captured && captured.OuterTypeParamCount.Value > 0)
                {
                    var capturedCount = captured.OuterTypeParamCount.Value;
                    var ownerPermutation = Resolve(ownerName, owner);
                    if (capturedCount != ownerPermutation.NewToOld.Length || capturedCount > count)
                        throw new InvalidOperationException($"Kotlin type '{name}' captures {capturedCount} owner slots but semantic owner '{ownerName}' declares {ownerPermutation.NewToOld.Length}");
                    var prefix = Enumerable.Range(0, count).ToArray();
                    for (var index = 0; index < capturedCount; index++) prefix[index] = ownerPermutation.NewToOld[index];
                    if (prefix.Where((old, index) => old != index).Any())
                    {
                        if (!type.TypeParams.HasValue) throw new InvalidOperationException($"Kotlin type '{name}' captures a generic owner but has no type parameter declarations");
                        var parameters = type.TypeParams.Value;
                        type.TypeParams = prefix.Select(index => (BirTypeParameter)parameters[index].DeepClone()).ToArray();
                        var inverse = Inverse(prefix);
                        RewriteOwnership((BirGraphValue)type, value => RemapOwnerSlots(value, inverse), lexicalOnly: true);
                        newToOld = prefix.Select(index => direct.NewToOld[index]).ToArray();
                    }
                }
                return completed[name] = (newToOld, Inverse(newToOld));
            }
            finally { visiting.Remove(name); }
        }
        foreach (var (name, type) in types) Resolve(name, type);
        permutations.Clear();
        foreach (var (name, permutation) in completed)
            if (permutation.NewToOld.Where((old, index) => old != index).Any()) permutations[name] = permutation;
        if (permutations.Count == 0) return;
        TypeNode Applications(TypeNode type) => type switch {
            TypeNode.Fqn value => Named(value), TypeNode.Nullable value => new TypeNode.Nullable(Applications(value.Of)),
            TypeNode.Oblivious value => new TypeNode.Oblivious(Applications(value.Of)),
            TypeNode.Projection value => new TypeNode.Projection(value.Variance, Applications(value.Of)),
            TypeNode.Array value => new TypeNode.Array(Applications(value.Elem)), TypeNode.ByRef value => new TypeNode.ByRef(Applications(value.Of)),
            TypeNode.Fn value => new TypeNode.Fn(value.Suspend, Applications(value.Ret), value.Params.Select(Applications).ToArray(),
                value.Recv == null ? null : Applications(value.Recv), value.Clr, value.Ctx?.Select(Applications).ToArray()), _ => type,
        };
        TypeNode.Fqn Named(TypeNode.Fqn value)
        {
            if (value.Args == null) return value;
            var arguments = value.Args.Select(Applications).ToArray();
            if (!permutations.TryGetValue(value.Name, out var permutation)) return value with { Args = arguments };
            if (arguments.Length != permutation.NewToOld.Length)
                throw new InvalidOperationException($"Kotlin lifted type application '{value.Name}' has {arguments.Length} arguments [{string.Join(", ", arguments.Select(argument => TypeJson.Write(argument).ToJsonString()))}] but its declaration has {permutation.NewToOld.Length}");
            return value with { Args = permutation.NewToOld.Select(index => arguments[index]).ToArray() };
        }
        foreach (var root in roots) RewriteOwnership(root, Applications, lexicalOnly: false);
    }

    static void PrepareSyntheticOwnerCaptures(BirGraphValue root, IReadOnlyDictionary<string, IBirTypeDeclaration> types)
    {
        if (root is IBirExecutablePayload) return;
        var synthetic = root switch { BirNewClosure value => (BirGraphValue)value.SynthClass.OrDefault(),
            BirNewSam value => value.SynthClass.OrDefault(), _ => null };
        if (synthetic != null)
        {
            var (name, owner) = synthetic switch {
                BirClassDefinition value => (value.Name.OrDefault(), value.SemanticOwner.OrDefault()),
                BirClosureDefinition value => (value.Name.OrDefault(), value.SemanticOwner.OrDefault()),
                _ => throw new InvalidOperationException("Unknown synthetic ownership declaration"),
            };
            if (owner != null && types.TryGetValue(owner, out var ownerType) && ownerType.TypeParams.HasValue && ownerType.TypeParams.Value.Length != 0)
            {
                var ownerParameters = ownerType.TypeParams.Value;
                var parameters = NullableRepresentationViews.TypeParameters(synthetic).OrEmpty();
                var arguments = root.ApplicationTypeArguments.OrEmpty();
                if (arguments.Length != parameters.Length)
                    throw new InvalidOperationException($"synthetic type '{name}' has {parameters.Length} parameters but {arguments.Length} construction arguments");
                var ownerFacts = NullableRepresentationViews.TypeFacts((BirGraphValue)ownerType);
                var facts = NullableRepresentationViews.TypeFacts(synthetic);
                var ownerFrame = ownerFacts?.NullableFrame ?? new NullableRepresentationFrame(ownerParameters.Length, Array.Empty<int>());
                var captureFrame = facts?.NullableFrame ?? new NullableRepresentationFrame(parameters.Length, Array.Empty<int>());
                if (ownerFrame.PhysicalArity != ownerParameters.Length || captureFrame.PhysicalArity != parameters.Length)
                    throw new InvalidOperationException($"Synthetic owner capture '{name}' has an incomplete declaration frame");
                var plan = PlanCapture(arguments, ownerFrame, captureFrame);
                if (ClosureSynthesis.HasPreboundFrame(synthetic) && plan.OldToNew.Where((value, index) => value != index).Any())
                    RewriteOwnership(synthetic, type => RemapOwnerSlots(type, plan.OldToNew), lexicalOnly: true, syntheticFrame: true);
                var remapped = NullableRepresentationViews.TypeParameters(synthetic).OrEmpty();
                NullableRepresentationViews.SetTypeParameters(synthetic, ownerParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone())
                    .Concat(plan.Remaining.Select(index => (BirTypeParameter)remapped[index].DeepClone())).ToArray());
                switch (synthetic) {
                    case BirClassDefinition value: value.OuterTypeParamCount = ownerParameters.Length; break;
                    case BirClosureDefinition value: value.OuterTypeParamCount = ownerParameters.Length; break;
                }
                root.ApplicationTypeArguments = Enumerable.Range(0, ownerParameters.Length).Select(index => (TypeNode)new TypeNode.Tv("type", index))
                    .Concat(plan.Remaining.Select(index => BirGraphValue.CopyType(arguments[index]))).ToArray();
                if (plan.Frame.RequiresMetadata || facts?.NullableFrame != null)
                    NullableRepresentationViews.SetTypeFacts(synthetic, (facts ?? SourceTypeFacts.Empty).RebaseCapturedFrame(plan.Frame, plan.SourceMap, ownerFacts));
            }
        }
        root.VisitChildren(child => PrepareSyntheticOwnerCaptures(child, types));
    }
}
