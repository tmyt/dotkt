using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    static bool IsCallerFrameRole(BirGenericFrameRole role) => role is not (
        BirGenericFrameRole.ReferencedDeclaration or BirGenericFrameRole.ResolvedDeclaration
        or BirGenericFrameRole.OverrideDeclaration or BirGenericFrameRole.SharedCaptureDeclaration
        or BirGenericFrameRole.LocalDeclaration or BirGenericFrameRole.SyntheticDeclaration
        or BirGenericFrameRole.FrozenSourceFact or BirGenericFrameRole.StorageSourceType);

    internal static void CollectTvKeys(TypeNode type, SortedSet<(string scope, int i)> keys)
    {
        TypedExecutableFrames.RewriteType(type, variable => {
            keys.Add((variable.Scope, variable.I));
            return null;
        });
    }

    internal static void CollectTvKeys(BirGraphValue node, SortedSet<(string scope, int i)> keys)
        => VisitCallerFrame(node, type => CollectTvKeys(type, keys));

    static void VisitCallerFrame(BirGraphValue node, Action<TypeNode> type)
    {
        if (node == null || node is IBirExecutablePayload) return;
        if (node is BirNewSuspendLambda suspend
            && (suspend._suspendDeclarationTypeArgs.HasValue || suspend.TypeFrame.OrDefault() == "dense"))
        {
            // Donor declaration fields are not caller facts. A dense SM's
            // capture descriptors still are, but its body/parameters are not.
            if (!suspend._suspendDeclarationTypeArgs.HasValue)
                foreach (var capture in suspend.Captures.OrEmpty()) VisitCallerFrame(capture, type);
            foreach (var argument in suspend.TypeArgs.OrEmpty()) type(argument);
            foreach (var value in suspend.CapValues.OrEmpty()) if (value != null) VisitCallerFrame((BirGraphValue)value, type);
            if (suspend.FuncType.HasValue) type(suspend.FuncType.Value);
            if (suspend.Sty.HasValue) type(suspend.Sty.Value);
            return;
        }
        node.VisitGenericFrame((role, value) => { if (IsCallerFrameRole(role)) type(value); },
            (role, child) => { if (IsCallerFrameRole(role)) VisitCallerFrame(child, type); });
    }

    internal static TypeNode RemapCallerType(TypeNode type, Dictionary<(string, int), int> map, bool classFrame = false)
        => TypedExecutableFrames.RewriteType(type, variable => map.TryGetValue((variable.Scope, variable.I), out var index)
            ? new TypeNode.Tv(classFrame ? "type" : variable.Scope, index) : null);

    internal static void RenumberTvs(BirGraphValue node, Dictionary<(string, int), int> map, bool classFrame = false)
        => RewriteCallerFrame(node, type => RemapCallerType(type, map, classFrame));

    static void RewriteCallerFrame(BirGraphValue node, Func<TypeNode, TypeNode> type)
    {
        if (node == null || node is IBirExecutablePayload) return;
        if (node is BirNewSuspendLambda suspend
            && (suspend._suspendDeclarationTypeArgs.HasValue || suspend.TypeFrame.OrDefault() == "dense"))
        {
            if (!suspend._suspendDeclarationTypeArgs.HasValue)
                foreach (var capture in suspend.Captures.OrEmpty()) RewriteCallerFrame(capture, type);
            if (suspend.TypeArgs.HasValue) suspend.TypeArgs = suspend.TypeArgs.Value.Select(type).ToArray();
            foreach (var value in suspend.CapValues.OrEmpty()) if (value != null) RewriteCallerFrame((BirGraphValue)value, type);
            if (suspend.FuncType.HasValue) suspend.FuncType = type(suspend.FuncType.Value);
            if (suspend.Sty.HasValue) suspend.Sty = type(suspend.Sty.Value);
            return;
        }
        node.RewriteGenericFrameInPlace((role, value) => IsCallerFrameRole(role) ? type(value) : value,
            (role, child) => { if (IsCallerFrameRole(role)) RewriteCallerFrame(child, type); });
    }

    internal BirGraphValue RenameSpliceSynthClasses(BirGraphValue node, int id, string semanticOwner)
    {
        if (node == null || node is IBirExecutablePayload) return node;
        if (node is BirCallInline inline) inline.SemanticOwner = semanticOwner;
        if (node is BirCallEvaluation evaluation) evaluation.SemanticOwner = semanticOwner;
        BirGraphValue synthetic = node switch {
            BirNewClosure value => value.SynthClass.OrDefault(), BirNewSam value => value.SynthClass.OrDefault(), _ => null,
        };
        if (synthetic != null)
        {
            string name;
            switch (synthetic)
            {
                case BirClosureDefinition closure:
                    closure.SemanticOwner = semanticOwner;
                    closure.OuterTypeParamCount = default;
                    closure.OuterTypeParamOffset = default;
                    name = closure.Name.OrDefault();
                    break;
                case BirClassDefinition type:
                    type.SemanticOwner = semanticOwner;
                    type.OuterTypeParamCount = default;
                    type.OuterTypeParamOffset = default;
                    name = type.Name.OrDefault();
                    break;
                default: throw new InvalidOperationException("Unaccounted inline synthetic declaration");
            }
            if (name != null && !name.EndsWith("$sp" + id, StringComparison.Ordinal))
                RenameFqnReferences(node, name, name + "$sp" + id);
        }
        node.VisitChildSlots(slot => slot.Replace(RenameSpliceSynthClasses(slot.Value, id, semanticOwner)));
        return node;
    }

    static void RenameFqnReferences(BirGraphValue node, string oldName, string newName)
    {
        if (node is IBirExecutablePayload) return;
        if (node is IBirTypeDeclaration type && type.Name.OrDefault() == oldName) type.Name = newName;
        if (node is BirClosureDefinition closure && closure.Name.OrDefault() == oldName) closure.Name = newName;
        node.RewriteTypeFieldsInPlace(type => MapInlineType(type,
            value => value is TypeNode.Fqn fqn && fqn.Name == oldName ? fqn with { Name = newName } : value));
        node.VisitChildren(child => RenameFqnReferences(child, oldName, newName));
    }

    // Exact synthetic concretization uses every physical type slot on the
    // detached declaration, not caller-frame substitution's descriptor policy.
    internal static void PruneConcreteSynthClasses(BirGraphValue node)
    {
        if (node == null || node is IBirExecutablePayload) return;
        BirGraphValue synthetic = node switch {
            BirNewClosure value => value.SynthClass.OrDefault(), BirNewSam value => value.SynthClass.OrDefault(), _ => null,
        };
        var arguments = node.ApplicationTypeArguments.OrDefault();
        if (synthetic != null && arguments is { Length: > 0 })
        {
            var parameters = synthetic switch {
                BirClassDefinition value => value.TypeParams.OrEmpty(),
                BirClosureDefinition value => value.TypeParams.OrEmpty(), _ => Array.Empty<BirTypeParameter>(),
            };
            var variables = new SortedSet<(string scope, int i)>();
            foreach (var argument in arguments) CollectTvKeys(argument, variables);
            if (parameters.Length > 0 && variables.Count == 0)
            {
                var name = synthetic switch {
                    BirClassDefinition value => value.Name.OrDefault(), BirClosureDefinition value => value.Name.OrDefault(), _ => null,
                };
                TypeNode Concretize(TypeNode type) => TypedExecutableFrames.RewriteType(type, variable =>
                    variable.Scope == "type" && variable.I >= 0 && variable.I < arguments.Length ? arguments[variable.I] : null);
                void Rewrite(BirGraphValue current)
                {
                    if (current is IBirExecutablePayload) return;
                    current.RewriteTypeFieldsInPlace(type => MapInlineType(Concretize(type),
                        value => value is TypeNode.Fqn fqn && fqn.Name == name ? fqn with { Args = null } : value));
                    current.VisitChildren(Rewrite);
                }
                Rewrite(synthetic);
                // Root provides the scalar source-fact operation; source slots
                // use ordinary arguments, not the expanded physical frame.
                switch (synthetic)
                {
                    case BirClassDefinition value:
                        if (value.KotlinSupertypesPre.HasValue)
                            value.KotlinSupertypesPre = ConcretizeSyntheticSourceFacts(value.KotlinSupertypesPre.Value, arguments);
                        value.TypeParams = default;
                        break;
                    case BirClosureDefinition value:
                        if (value.KotlinSupertypesPre.HasValue)
                            value.KotlinSupertypesPre = ConcretizeSyntheticSourceFacts(value.KotlinSupertypesPre.Value, arguments);
                        value.TypeParams = default;
                        break;
                }
                node.ApplicationTypeArguments = default;
            }
        }
        node.VisitChildren(PruneConcreteSynthClasses);
    }

    static SourceTypeFacts ConcretizeSyntheticSourceFacts(SourceTypeFacts facts, TypeNode[] physicalArguments)
    {
        var ordinaryArguments = facts.NullableFrame?.OrdinaryArguments(physicalArguments) ?? physicalArguments;
        return facts.ConcretizeSynthetic(type => TypedExecutableFrames.RewriteType(type, variable =>
            variable.Scope == "type" && variable.I >= 0 && variable.I < ordinaryArguments.Length
                ? ordinaryArguments[variable.I] : null));
    }

    static TypeNode MapInlineType(TypeNode type, Func<TypeNode, TypeNode> map)
    {
        if (type == null) return null;
        var mapped = type switch {
            TypeNode.Fqn value => value with { Args = value.Args?.Select(child => MapInlineType(child, map)).ToArray() },
            TypeNode.Fn value => value with {
                Ret = MapInlineType(value.Ret, map), Params = value.Params.Select(child => MapInlineType(child, map)).ToArray(),
                Recv = MapInlineType(value.Recv, map), Ctx = value.Ctx?.Select(child => MapInlineType(child, map)).ToArray(),
            },
            TypeNode.FnPtr value => value with {
                Ret = MapInlineType(value.Ret, map), Params = value.Params.Select(child => MapInlineType(child, map)).ToArray(),
            },
            TypeNode.Nullable value => value with { Of = MapInlineType(value.Of, map) },
            TypeNode.Oblivious value => value with { Of = MapInlineType(value.Of, map) },
            TypeNode.Projection value => value with { Of = MapInlineType(value.Of, map) },
            TypeNode.Array value => value with { Elem = MapInlineType(value.Elem, map) },
            TypeNode.ByRef value => value with { Of = MapInlineType(value.Of, map) },
            TypeNode.Ptr value => value with { Of = MapInlineType(value.Of, map) },
            TypeNode.Mod value => value with { M = MapInlineType(value.M, map), Of = MapInlineType(value.Of, map) },
            TypeNode.Tv or TypeNode.Star => type,
            _ => throw new InvalidOperationException("Unaccounted inline hygiene type variant: " + type.GetType().Name),
        };
        return map(mapped);
    }
}
