using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MapVarianceRealign
{
    internal static BirFileDeclaration Apply(BirFileDeclaration root,
        IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams, ReferenceMetadataIndex refs)
    {
        var typeFrames = CollectTypeFrames(root, refs?.NullableTypeFrames);
        ProcessMethods(root.Methods.OrEmpty(), calleeTypeParams, refs, null, typeFrames);
        foreach (var owner in root.Types.OrEmpty())
            ProcessMethods(owner.Methods.OrEmpty(), calleeTypeParams, refs,
                NullableRepresentationViews.TypeFacts((BirGraphValue)owner)?.NullableFrame, typeFrames);
        return root;
    }

    static Dictionary<string, NullableRepresentationFrame> CollectTypeFrames(BirFileDeclaration root,
        IReadOnlyDictionary<string, NullableRepresentationFrame> referenced)
    {
        var typeFrames = referenced == null ? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
            : new Dictionary<string, NullableRepresentationFrame>(referenced, StringComparer.Ordinal);
        // The old owner overlaid only this file, not every local declaration in
        // the module. An explicit local declaration without a frame shadows one
        // from a reference just as completely as a declaration with a frame.
        foreach (var owner in root.Types.OrEmpty())
            if (owner.Name.HasValue)
            {
                var frame = NullableRepresentationViews.TypeFacts((BirGraphValue)owner)?.NullableFrame;
                if (frame == null) typeFrames.Remove(owner.Name.Value);
                else typeFrames[owner.Name.Value] = frame;
            }
        return typeFrames;
    }

    static void ProcessMethods(IEnumerable<BirFunctionDeclaration> methods,
        IReadOnlyDictionary<string, TypeNode[]> declarations, ReferenceMetadataIndex refs,
        NullableRepresentationFrame ownerFrame,
        IReadOnlyDictionary<string, NullableRepresentationFrame> typeFrames)
    {
        foreach (var method in methods)
        {
            var methodFrame = method.NullableRepresentationFrame.OrDefault();
            TypeNode NullableArgument(TypeNode argument)
            {
                if (argument is TypeNode.Tv variable)
                {
                    var callerFrame = variable.Scope == "type" ? ownerFrame : methodFrame;
                    if (callerFrame == null)
                        throw new InvalidOperationException("Collection factory requires a missing caller nullable frame");
                    var semantic = callerFrame.SemanticVariable(variable);
                    return semantic is TypeNode.Nullable ? variable : callerFrame.NullableVariable((TypeNode.Tv)semantic);
                }
                return argument is TypeNode.Nullable ? argument : new TypeNode.Nullable(argument);
            }
            var env = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var parameter in method.Params.OrEmpty())
                if (parameter.Name.HasValue && parameter.Type.HasValue) env[parameter.Name.Value] = parameter.Type.Value;
            // A receiver's method type-variable index selects its precise
            // invariant-collection bound, undoing use-site variance approximation.
            var constraints = new Dictionary<int, TypeNode>();
            var parameters = method.TypeParams.OrEmpty();
            for (var index = 0; index < parameters.Length; index++)
                foreach (var constraint in parameters[index].Constraints.OrEmpty())
                    if (InvariantGenericArgs(constraint) != null) { constraints[index] = constraint; break; }
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            // Whole-body precollection is intentional. This pass does not use
            // sequential lexical inference, including in nested body regions.
            foreach (var statement in method.Body.OrEmpty()) Walk((BirGraphValue)statement, node => {
                if (node is not BirVariable { Name.HasValue: true } variable) return;
                if (variable.Type.HasValue) env.TryAdd(variable.Name.Value, variable.Type.Value);
                if (variable.Init.OrDefault() is BirLocal { Name.HasValue: true } local)
                    aliases.TryAdd(variable.Name.Value, local.Name.Value);
            });
            foreach (var statement in method.Body.OrEmpty()) Walk((BirGraphValue)statement, node => {
                if (node is BirVariable { Name.HasValue: true, Type.HasValue: true } variable
                    && aliases.ContainsKey(variable.Name.Value))
                {
                    var root = ResolveAliasRoot(variable.Name.Value, aliases);
                    if (env.TryGetValue(root, out var source)
                        && RealignedType(variable.Type.Value, source, constraints) is { } aligned && aligned != variable.Type.Value)
                    { variable.Type = aligned; env[variable.Name.Value] = aligned; }
                }
            });
            foreach (var statement in method.Body.OrEmpty()) Walk((BirGraphValue)statement, node => {
                switch (node)
                {
                    case BirCallStatic call:
                        Realign(call.TypeArgs.OrDefault(), call.DeclarationId.OrDefault(), call.Args.OrDefault(), env, declarations, typeFrames);
                        break;
                    case BirCallInstance call:
                        Realign(call.TypeArgs.OrDefault(), call.DeclarationId.OrDefault(), call.Args.OrDefault(), env, declarations, typeFrames);
                        // Inlined member calls can still name the approximated
                        // owner. Restore its arguments from the alias-root bound.
                        if (call.OwnerType.OrDefault() is { } owner && InvariantGenericArgs(owner) is { } arguments
                            && arguments.Any(IsObjectish) && call.Recv.OrDefault() is BirLocal { Name.HasValue: true } local
                            && env.TryGetValue(ResolveAliasRoot(local.Name.Value, aliases), out var source)
                            && source is TypeNode.Tv variable && constraints.TryGetValue(variable.I, out var bound)
                            && InvariantGenericArgs(bound) is { } boundArguments && boundArguments.Length == arguments.Length)
                        {
                            var aligned = RealignArgs((TypeNode.Fqn)owner, arguments, boundArguments);
                            if (aligned != owner) call.OwnerType = aligned;
                        }
                        break;
                    case BirNew construction: RealignFactoryCtorArgTypes(construction, refs, NullableArgument); break;
                }
            });
        }
    }

    // All writes target the existing owning records/vectors. Executable carriers
    // were opaque string/byte leaves to the former walker and stay opaque here.
    static void Walk(BirGraphValue node, Action<BirGraphValue> action)
    {
        if (node == null || node is IBirExecutablePayload) return;
        action(node);
        node.VisitChildren(child => Walk(child, action));
    }

    // A marked fresh collection literal may infer narrower element arguments
    // than its receiving constructor slot. Widen that allocation to the slot;
    // never reinterpret the identity of an existing collection value.
    static void RealignFactoryCtorArgTypes(BirNew construction, ReferenceMetadataIndex refs,
        Func<TypeNode, TypeNode> nullableArgument)
    {
        if (!construction.ArgTypes.HasValue || !construction.Args.HasValue) return;
        var count = Math.Min(construction.ArgTypes.Value.Length, construction.Args.Value.Length);
        for (var index = 0; index < count; index++)
        {
            if (construction.Args.Value[index] is not BirCallStatic call) continue;
            string kind;
            if (call.DeclarationId.HasValue)
            {
                if (!refs.TryDeclarationFactory(call.DeclarationId.Value, out kind, out _, out _)) continue;
            }
            else kind = call.Method.HasValue ? refs.CollectionFactoryKind(call.Method.Value) : null;
            if (kind == null || !call.TypeArgs.HasValue || call.TypeArgs.Value.Length == 0
                || UnwrapNullableOblivious(construction.ArgTypes.Value[index]) is not TypeNode.Fqn { Args: { } target }) continue;
            var frame = call.DeclarationId.HasValue ? refs.NullableMethodFrame(call.DeclarationId.Value) : null;
            if (target.Length != 1 || (frame?.SourceArity ?? call.TypeArgs.Value.Length) != 1) continue;
            try
            {
                // ArgTypes already lives in the caller's frame. Never apply the
                // constructor owner's type substitution to it a second time.
                call.TypeArgs = frame == null ? target.Select(BirGraphValue.CopyType).ToArray()
                    : frame.Close(target, argument => argument, nullableArgument, argument => argument, nullableArgument);
            }
            catch (ArgumentException error)
            {
                throw new InvalidOperationException($"Collection factory {call.Method.OrDefault()} frame closure failed: {error.Message}", error);
            }
        }
    }

    static void Realign(TypeNode[] arguments, string identity, IBirExpression[] values,
        Dictionary<string, TypeNode> env, IReadOnlyDictionary<string, TypeNode[]> declarations,
        IReadOnlyDictionary<string, NullableRepresentationFrame> typeFrames)
    {
        if (arguments == null || arguments.Length == 0 || identity == null || values == null
            || !declarations.TryGetValue(identity, out var parameters)) return;
        Dictionary<int, TypeNode> substitution = null;
        for (var index = 0; index < Math.Min(parameters.Length, values.Length); index++)
        {
            if (parameters[index] is not { } parameter || InvariantGenericArgs(parameter) is not { } parameterArguments
                || ActualArgType(values[index], env) is not { } actual || InvariantGenericArgs(actual) is not { } actualArguments) continue;
            var formal = ArgumentRoles((TypeNode.Fqn)parameter, parameterArguments, typeFrames);
            var concrete = ArgumentRoles((TypeNode.Fqn)actual, actualArguments, typeFrames);
            foreach (var (role, type) in formal)
                if (type is TypeNode.Tv { Scope: "method" } variable && concrete.TryGetValue(role, out var argument)
                    && argument is not TypeNode.Tv && variable.I >= 0 && variable.I < arguments.Length)
                    (substitution ??= new Dictionary<int, TypeNode>())[variable.I] = argument;
        }
        if (substitution == null) return;
        foreach (var (index, concrete) in substitution)
            if (arguments[index] != concrete) arguments[index] = BirGraphValue.CopyType(concrete);
    }

    // A local uses its declaration; otherwise only the node's own Type/Ret
    // fields supply this policy's evidence, not general Surface/Sty inference.
    static TypeNode ActualArgType(IBirExpression value, Dictionary<string, TypeNode> env)
        => value is BirLocal { Name.HasValue: true } local && env.TryGetValue(local.Name.Value, out var declared)
            ? declared : (value as BirGraphValue)?.OwnTypeOrReturnStamp;
}
