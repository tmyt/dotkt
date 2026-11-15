using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Context = FBoundStarProjectionErasure.CarrierRealizationContext;

static partial class OwnerConstrainedMethodLowering
{
    internal static void ConfigurePreparedMethodsSource(CurrentDeclarationBindings bindings, ReferenceMetadataIndex references)
    {
        if (!PreparedMethods || !ReferenceEquals(bindings, MethodBindings) || !ReferenceEquals(references, References)
            || bindings.IsBound) throw new InvalidOperationException("Method preparation does not own this source continuation");
        foreach (var row in Methods) { _ = row.MethodBinding.Source; _ = row.OwnerBinding.Source; }
        // Exact generated captures can still be registered below; the same
        // source registrations are released when method preparation completes.
    }

    static BirTypeParameter[] ContinuationParameters(BirGraphValue declaration) => declaration switch {
        BirFunctionDeclaration method => method.TypeParams.OrEmpty(),
        IBirTypeDeclaration owner => owner.TypeParams.OrEmpty(),
        BirConstructor or BirFileDeclaration => Array.Empty<BirTypeParameter>(),
        _ => throw new InvalidOperationException("Unaccounted constraint declaration: " + declaration.GetType().Name),
    };

    static void RewriteConstraintsSource(BirFunctionDeclaration method, IReadOnlyList<BirTypeParameter> owner,
        BirTypeParameter[] parameters, Context context, Func<TypeNode, bool> depends,
        Func<TypeNode, TypeNode> projected, Func<TypeNode, TypeNode> classifier)
    {
        var binding = method != null ? context.Bindings.GetCapturedSource(method) : null;
        var plan = PlanConstraintRewrite(parameters.Select(parameter => parameter?.Constraints.OrDefault()).ToArray(),
            owner.Select(parameter => parameter?.Constraints.OrEmpty() ?? Array.Empty<TypeNode>()).ToArray(),
            method?.NullableRepresentationFrame.OrDefault(), method?._ownerConstraintOverrideBounds.OrDefault(),
            context.References, depends, projected, classifier,
            index => binding != null ? context.NativeProofs.Bounds(binding, index) : Enumerable.Empty<TypeNode>());
        for (var index = 0; index < parameters.Length; index++)
            if (plan[index] is { } row)
            {
                parameters[index].Constraints = row.Retained;
                if (row.Removed.Length > 0) parameters[index].ErasedInnerConstraints = row.Removed;
            }
    }

    static OwnerConstraintDispatchBounds ContinuationDispatch(BirGraphValue declaration) => declaration switch {
        BirFunctionDeclaration method => method._ownerConstraintDispatchBounds.OrDefault(),
        BirClassDefinition owner => owner._ownerConstraintDispatchBounds.OrDefault(),
        BirInterfaceDefinition owner => owner._ownerConstraintDispatchBounds.OrDefault(),
        _ => null,
    };

    internal static void SetDispatchBoundsSource(BirGraphValue declaration, IReadOnlyDictionary<int, TypeNode[]> bounds)
    {
        var fact = new OwnerConstraintDispatchBounds(bounds);
        switch (declaration)
        {
            case BirFunctionDeclaration method: method._ownerConstraintDispatchBounds = fact; break;
            case BirClassDefinition owner: owner._ownerConstraintDispatchBounds = fact; break;
            case BirInterfaceDefinition owner: owner._ownerConstraintDispatchBounds = fact; break;
            default: throw new InvalidOperationException("Unaccounted dispatch declaration: " + declaration.GetType().Name);
        }
    }

    internal static void PreserveDispatchBoundsSource(BirGraphValue declaration, IReadOnlyList<BirTypeParameter> parameters)
        => SetDispatchBoundsSource(declaration, parameters.Select((parameter, index) => (parameter, index))
            .Where(row => row.parameter.ErasedInnerConstraints.HasValue)
            .ToDictionary(row => row.index, row => row.parameter.ErasedInnerConstraints.Value));

    internal static void CloseOwnersSource(Context context, Func<TypeNode, bool> depends,
        Func<TypeNode, TypeNode> projected, Func<TypeNode, TypeNode> classifier)
    {
        foreach (var row in Methods)
        {
            var method = (BirFunctionDeclaration)row.MethodBinding.Source;
            var owner = row.OwnerBinding.Source;
            var parameters = method.TypeParams.OrEmpty().Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            RewriteConstraintsSource(method, ContinuationParameters(owner), parameters, context, depends, projected, classifier);
            ConstrainedTypeParameterReceiverBinding.CloseMethodOwners(method, owner, parameters, context.References);
        }
    }

    internal static void CloseBodyOwnerViewsSource(BirGraphValue body, BirTypeParameter[] methodParameters,
        BirTypeParameter[] ownerParameters, Context context, Func<TypeNode, bool> depends,
        Func<TypeNode, TypeNode> projected, Func<TypeNode, TypeNode> classifier)
    {
        var parameters = methodParameters.Select(parameter => parameter == null ? null : (BirTypeParameter)parameter.DeepClone()).ToArray();
        RewriteConstraintsSource(null, ownerParameters, parameters, context, depends, projected, classifier);
        ConstrainedTypeParameterReceiverBinding.CloseMethodOwners(body, ownerParameters, parameters, context.References);
    }

    internal static void ApplySource(Context context, Func<TypeNode, bool> depends,
        Func<TypeNode, TypeNode> projected, Func<TypeNode, TypeNode> classifier)
    {
        foreach (var row in Methods)
        {
            var method = (BirFunctionDeclaration)row.MethodBinding.Source;
            var parameters = method.TypeParams.Value;
            RewriteConstraintsSource(method, ContinuationParameters(row.OwnerBinding.Source), parameters, context, depends, projected, classifier);
            method._ownerConstraintOverrideBounds = default;
            PreserveDispatchBoundsSource(method, parameters);
        }
    }

    internal static Dictionary<string, HashSet<int>> PropagateCapturedBoundsSource(Context context,
        IReadOnlyList<BirFileDeclaration> roots, Func<TypeNode, bool> depends,
        Func<TypeNode, TypeNode> projected, Func<TypeNode, TypeNode> classifier)
    {
        var definitions = context.SourceDefinitions.ToDictionary(pair => pair.Key,
            pair => (IBirTypeDeclaration)pair.Value.Source, StringComparer.Ordinal);
        var owners = definitions.Values.Cast<BirGraphValue>().Concat(roots).ToArray();
        IEnumerable<BirFunctionDeclaration> OwnerMethods(BirGraphValue owner) => owner switch {
            IBirTypeDeclaration type => type.Methods.OrEmpty(), BirFileDeclaration file => file.Methods.OrEmpty(),
            _ => Array.Empty<BirFunctionDeclaration>(),
        };
        var declarations = owners.SelectMany(owner => OwnerMethods(owner).Select(method => (Method: method, Owner: owner))).ToArray();
        var captured = declarations.Where(row => row.Method.Generated.OrDefault() && row.Method.DeclarationId.HasValue)
            .GroupBy(row => row.Method.DeclarationId.Value, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var registered = Methods.Select(row => (BirFunctionDeclaration)row.MethodBinding.Source)
            .ToHashSet((IEqualityComparer<BirFunctionDeclaration>)ReferenceEqualityComparer.Instance);
        var pending = new Queue<(BirGraphValue Method, BirGraphValue Owner, BirTypeParameter[] Parameters, BirTypeParameter[] Original)>();
        var seen = new Dictionary<BirGraphValue, HashSet<BirGraphValue>>(ReferenceEqualityComparer.Instance);
        IEnumerable<(BirGraphValue Method, BirGraphValue Owner)> Callers()
        {
            foreach (var row in Methods) yield return (row.MethodBinding.Source, row.OwnerBinding.Source);
            foreach (var row in declarations) yield return (row.Method, row.Owner);
            foreach (var owner in owners.OfType<IBirTypeDeclaration>())
                foreach (var constructor in owner.Ctors.OrEmpty()) yield return (constructor, (BirGraphValue)owner);
        }
        foreach (var (method, owner) in Callers())
        {
            if (!seen.TryGetValue(method, out var ownerSet)) seen[method] = ownerSet = new(ReferenceEqualityComparer.Instance);
            if (!ownerSet.Add(owner)) continue;
            var parameters = ContinuationParameters(method).Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            var original = parameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            RewriteConstraintsSource(method as BirFunctionDeclaration, ContinuationParameters(owner), parameters,
                context, depends, projected, classifier);
            pending.Enqueue((method, owner, parameters, original));
        }
        var changed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        static bool Same(OptionalField<TypeNode[]> field, TypeNode[] value) => field.HasValue
            && field.Value.Length == value.Length && field.Value.Zip(value).All(pair => TypeNodeDataEquality.Equal(pair.First, pair.Second));
        while (pending.TryDequeue(out var caller))
        {
            void Walk(BirGraphValue node)
            {
                if (node is IBirExecutablePayload) return;
                var construction = node switch { BirNewClosure value => value.ClosureType.OrDefault() as TypeNode.Fqn,
                    BirNewSam value => value.SamType.OrDefault() as TypeNode.Fqn,
                    BirNew value => value.Type.OrDefault() as TypeNode.Fqn, _ => null };
                BirGraphValue target = null;
                BirGraphValue targetOwner = null;
                var targetScope = "type";
                if (node is BirNewDelegate { DeclarationId.HasValue: true } callable
                    && captured.TryGetValue(callable.DeclarationId.Value, out var selected))
                { target = selected.Method; targetOwner = selected.Owner; targetScope = "method"; }
                else if (construction != null && definitions.TryGetValue(construction.Name, out var constructedDefinition))
                    target = (BirGraphValue)constructedDefinition;
                var generated = target switch { BirFunctionDeclaration method => method.Generated.OrDefault(),
                    IBirTypeDeclaration type => type.Generated.OrDefault(), _ => false };
                var hasParameters = target switch { BirFunctionDeclaration method => method.TypeParams.HasValue,
                    IBirTypeDeclaration type => type.TypeParams.HasValue, _ => false };
                if (generated && hasParameters)
                {
                    var parameters = ContinuationParameters(target);
                    var arguments = node.ApplicationTypeArguments.HasValue ? node.ApplicationTypeArguments.Value : construction?.Args;
                    if (arguments != null && arguments.Length == parameters.Length)
                    {
                        var positions = new Dictionary<(string, int), int>();
                        for (var index = 0; index < arguments.Length; index++)
                            if (arguments[index] is TypeNode.Tv variable) positions.TryAdd((variable.Scope, variable.I), index);
                        TypeNode[] Rebind(IEnumerable<TypeNode> bounds) => bounds.Select(bound => MapVariables(bound, variable =>
                            positions.TryGetValue((variable.Scope, variable.I), out var index)
                                ? new TypeNode.Tv(targetScope, index) : variable)).ToArray();
                        var targetChanged = false;
                        for (var index = 0; index < arguments.Length; index++)
                        {
                            if (arguments[index] is not TypeNode.Tv variable) continue;
                            var sourceParameters = variable.Scope == "method" ? caller.Parameters : ContinuationParameters(caller.Owner);
                            if (variable.I >= sourceParameters.Length || sourceParameters[variable.I] is not { } source) continue;
                            var sourceConstraints = source.Constraints.OrEmpty();
                            var erased = source.ErasedInnerConstraints.OrDefault();
                            if (sourceConstraints.Length == 0 && erased == null) continue;
                            var originals = variable.Scope == "method" ? caller.Original : ContinuationParameters(caller.Owner);
                            var original = variable.I < originals.Length ? originals[variable.I] : null;
                            var originalConstraints = original == null ? Array.Empty<TypeNode>()
                                : original.Constraints.OrEmpty().Concat(original.ErasedInnerConstraints.OrEmpty()).ToArray();
                            var complete = true;
                            foreach (var bound in originalConstraints) MapVariables(bound, tv => {
                                if (!positions.ContainsKey((tv.Scope, tv.I))) complete = false;
                                return tv;
                            });
                            if (!complete || !Rebind(originalConstraints).ToHashSet()
                                .SetEquals(parameters[index].Constraints.OrEmpty())) continue;
                            var constraints = Rebind(sourceConstraints);
                            var dispatch = Rebind(erased ?? Array.Empty<TypeNode>());
                            var parameter = parameters[index];
                            if (Same(parameter.Constraints, constraints) && (erased == null && !parameter.ErasedInnerConstraints.HasValue
                                || Same(parameter.ErasedInnerConstraints, dispatch))) continue;
                            if (target is BirFunctionDeclaration capturedMethod)
                            {
                                if (registered.Add(capturedMethod)) Record(capturedMethod, targetOwner, context.Bindings);
                            }
                            else if (target is IBirTypeDeclaration && NullableRepresentationViews.TypeFacts(target)?.NullableFrame == null)
                                NullableRepresentationViews.SetTypeFacts(target,
                                    (NullableRepresentationViews.TypeFacts(target) ?? SourceTypeFacts.Empty)
                                        .Merge(new Dictionary<int, TypeNode[]> { [index] = parameter.Constraints.OrEmpty().Select(BirGraphValue.CopyType).ToArray() }));
                            parameter.Constraints = constraints;
                            parameter.ErasedInnerConstraints = erased != null ? OptionalField<TypeNode[]>.Of(dispatch) : default;
                            var plans = ContinuationDispatch(target)?.Bounds.ToDictionary(pair => pair.Key, pair => pair.Value)
                                ?? new Dictionary<int, TypeNode[]>();
                            if (erased != null) plans[index] = dispatch; else plans.Remove(index);
                            SetDispatchBoundsSource(target, plans);
                            if (targetScope == "type")
                            {
                                if (!changed.TryGetValue(construction.Name, out var slots)) changed[construction.Name] = slots = new();
                                slots.Add(index);
                            }
                            targetChanged = true;
                        }
                        if (targetChanged)
                        {
                            void Enqueue(BirGraphValue method, BirGraphValue owner)
                            {
                                var frame = ContinuationParameters(method);
                                pending.Enqueue((method, owner, frame, frame.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray()));
                            }
                            if (target is BirFunctionDeclaration) Enqueue(target, targetOwner);
                            else foreach (var method in ((IBirTypeDeclaration)target).Methods.OrEmpty()) Enqueue(method, target);
                        }
                    }
                }
                node.VisitChildren(Walk);
            }
            foreach (var statement in FBoundStarProjectionErasure.SourceCallableBody(caller.Method).OrEmpty()) Walk((BirGraphValue)statement);
        }
        return changed;
    }
}
