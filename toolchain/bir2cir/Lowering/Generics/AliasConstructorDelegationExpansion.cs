using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Source declarations own alias constructor adapters. Their executable snapshots
// stay in Kotlin vocabulary until independently materialized at a consuming use.
sealed partial class AliasConstructorDelegationExpansion
{
    readonly IReadOnlyDictionary<string, BirClassDefinition> aliases;
    readonly ReferenceMetadataIndex references;
    readonly Dictionary<(string Owner, int Index), BirConstructorAdapterPayload> memo = new();
    readonly HashSet<BirNew> terminalConstructions = new(ReferenceEqualityComparer.Instance);
    static int localCounter;

    AliasConstructorDelegationExpansion(IReadOnlyDictionary<string, BirClassDefinition> aliases, ReferenceMetadataIndex references)
    {
        this.aliases = aliases;
        this.references = references;
    }

    public static AliasConstructorDelegationExpansion Collect(IEnumerable<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, bool carryForReference)
    {
        var files = roots.ToArray();
        var definitions = new Dictionary<string, BirClassDefinition>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var type in file.Types.OrEmpty().OfType<BirClassDefinition>())
            {
                var owner = ReferenceMetadataIndex.BareOwnerFqn(type.Name.Value);
                if (refs.Aliases.ContainsKey(owner) || type.Attrs.OrEmpty().Any(attribute =>
                    attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" }))
                    if (!definitions.TryAdd(owner, (BirClassDefinition)type.DeepClone()))
                        throw new InvalidOperationException($"Duplicate alias constructor owner '{owner}'");
            }
        bool IsAlias(string owner) => owner != null && (definitions.ContainsKey(owner) || refs.Aliases.ContainsKey(owner));
        void Consume(BirGraphValue node, string owner = null, string baseOwner = null)
        {
            if (node is BirClassDefinition type)
            {
                owner = type.Name.OrDefault();
                baseOwner = (type.Base.OrDefault() as TypeNode.Fqn)?.Name;
            }
            if (node is BirNew construction && IsAlias((construction.Type.OrDefault() as TypeNode.Fqn)?.Name))
                construction.ConstructorDeclarationId = default;
            if (node is BirConstructor constructor && IsAlias(constructor.ThisArgs.HasValue ? owner : baseOwner))
                constructor.DelegationDeclarationId = default;
            node.VisitChildren(child => Consume(child, owner, baseOwner));
        }
        foreach (var file in files) Consume(file);
        foreach (var alias in definitions.Values) TypedCallEvalLowering.Apply(alias, isValue);
        var expansion = new AliasConstructorDelegationExpansion(definitions, refs);
        if (carryForReference)
            foreach (var file in files)
                foreach (var type in file.Types.OrEmpty().OfType<BirClassDefinition>())
                {
                    var owner = ReferenceMetadataIndex.BareOwnerFqn(type.Name.Value);
                    if (!definitions.TryGetValue(owner, out var alias)) continue;
                    foreach (var (constructor, index) in type.Ctors.OrEmpty().Select((constructor, index) => (constructor, index)))
                    {
                        var adapter = expansion.Expand(owner, alias, index, new());
                        if (SameSignature(Parameters(constructor).Types, adapter.TerminalSignature.Value) && IsIdentity(adapter)) continue;
                        constructor.Attrs = constructor.Attrs.OrEmpty().Append(ConstructorAdapterCarrier.Create(adapter)).ToArray();
                    }
                }
        return expansion;
    }

    public void Apply(BirFileDeclaration root)
    {
        foreach (var type in root.Types.OrEmpty().OfType<BirClassDefinition>())
        {
            var name = type.Name.OrDefault();
            // Alias constructors define adapters; they are not runtime TypeDefs
            // whose base delegations need materialization at this boundary.
            if (name == null || !aliases.ContainsKey(ReferenceMetadataIndex.BareOwnerFqn(name))) ExpandType(type);
        }
        RewriteConstructions(root);
    }

    BirGraphValue RewriteConstructions(BirGraphValue node)
    {
        node.VisitChildSlots(slot => slot.Replace(RewriteConstructions(slot.Value)));
        if (node is BirNew construction && !terminalConstructions.Contains(construction)
            && ExpandConstruction(construction) is { } expanded)
            return RewriteConstructions(expanded);
        return node;
    }

    BirConstructorAdapterPayload SelectAdapter(string owner, TypeNode[] signature, TypeNode[] ownerArguments)
    {
        if (aliases.TryGetValue(owner, out var alias))
        {
            var index = SelectConstructor(owner, alias, signature, ownerArguments);
            return Specialize(Expand(owner, alias, index, new()), ownerArguments);
        }
        return references?.TryAliasConstructorAdapter(owner, signature, ownerArguments, out var adapter) == true ? adapter : null;
    }

    BirValueBlock ExpandConstruction(BirNew construction)
    {
        if (construction.Type.OrDefault() is not TypeNode.Fqn type || !construction.Args.HasValue || !construction.ArgTypes.HasValue) return null;
        var owner = ReferenceMetadataIndex.BareOwnerFqn(type.Name);
        var ownerArguments = type.Args ?? Array.Empty<TypeNode>();
        var signature = construction.ArgTypes.Value.Select(slot => SupertypeGraph.SubstOwnerTvs(slot, ownerArguments)).ToArray();
        var adapter = SelectAdapter(owner, signature, ownerArguments);
        if (adapter == null || SameSignature(signature, adapter.TerminalSignature.Value) && IsIdentity(adapter)) return null;
        var host = new BirConstructor();
        var replacements = MaterializeArguments(host, construction.Args.Value, adapter);
        var materialized = Materialize(adapter, replacements);
        FreshenLocals(materialized, construction, host);
        var terminal = construction with { Args = materialized.Arguments.Value, ArgTypes = adapter.TerminalSignature.Value.ToArray() };
        terminalConstructions.Add(terminal);
        return new BirValueBlock { Stmts = host.PreStmts.OrEmpty().Concat(materialized.Statements.Value).ToArray(), Result = terminal };
    }

    void ExpandType(BirClassDefinition type)
    {
        if (type.Base.OrDefault() is not TypeNode.Fqn baseType) return;
        var owner = ReferenceMetadataIndex.BareOwnerFqn(baseType.Name);
        var ownerArguments = baseType.Args ?? Array.Empty<TypeNode>();
        foreach (var constructor in type.Ctors.OrEmpty())
        {
            if (constructor.ThisArgs.HasValue || !constructor.BaseArgs.HasValue) continue;
            var signature = constructor.DelegationSig.Value.Select(slot => SupertypeGraph.SubstOwnerTvs(slot, ownerArguments)).ToArray();
            var adapter = SelectAdapter(owner, signature, ownerArguments);
            if (adapter == null || SameSignature(signature, adapter.TerminalSignature.Value) && IsIdentity(adapter)) continue;
            // Complete nested arguments before reserving names for the outer call.
            for (var index = 0; index < constructor.BaseArgs.Value.Length; index++)
                constructor.BaseArgs.Value[index] = (IBirExpression)RewriteConstructions((BirGraphValue)constructor.BaseArgs.Value[index]);
            var replacements = MaterializeArguments(constructor, constructor.BaseArgs.Value, adapter);
            var materialized = Materialize(adapter, replacements);
            FreshenLocals(materialized, constructor);
            constructor.PreStmts = constructor.PreStmts.OrEmpty().Concat(materialized.Statements.Value).ToArray();
            constructor.BaseArgs = materialized.Arguments.Value;
            constructor.DelegationSig = adapter.TerminalSignature.Value.ToArray();
        }
    }

    BirConstructorAdapterPayload Expand(string owner, BirClassDefinition alias, int index, HashSet<(string Owner, int Index)> active)
    {
        var key = (owner, index);
        if (memo.TryGetValue(key, out var cached)) return cached;
        if (!active.Add(key)) throw new InvalidOperationException($"Cyclic alias constructor delegation in '{owner}'");
        var constructor = alias.Ctors.Value[index];
        var parameters = Parameters(constructor);
        BirConstructorAdapterPayload result;
        if (constructor.ThisArgs.HasValue)
        {
            var target = SelectConstructor(owner, alias, constructor.DelegationSig.Value, OwnArgs(alias));
            result = Compose(parameters, constructor.PreStmts.OrEmpty(), constructor.ThisArgs.Value, Expand(owner, alias, target, active));
        }
        else if (constructor.BaseArgs.HasValue && alias.Base.OrDefault() is TypeNode.Fqn baseType
            && aliases.TryGetValue(ReferenceMetadataIndex.BareOwnerFqn(baseType.Name), out var baseAlias))
        {
            var baseOwner = ReferenceMetadataIndex.BareOwnerFqn(baseType.Name);
            var baseArguments = baseType.Args ?? Array.Empty<TypeNode>();
            var target = SelectConstructor(baseOwner, baseAlias, constructor.DelegationSig.Value, baseArguments);
            result = Compose(parameters, constructor.PreStmts.OrEmpty(), constructor.BaseArgs.Value,
                Specialize(Expand(baseOwner, baseAlias, target, active), baseArguments));
        }
        else
            result = new BirConstructorAdapterPayload {
                Parameters = parameters.Names, Signature = parameters.Types, Statements = Array.Empty<IBirStatement>(),
                Arguments = parameters.Names.Select((name, slot) => (IBirExpression)new BirLocal { Name = name, Sty = parameters.Types[slot] }).ToArray(),
                TerminalSignature = parameters.Types,
            };
        active.Remove(key);
        memo[key] = result;
        return result;
    }

    static BirConstructorAdapterPayload Compose((string[] Names, TypeNode[] Types) parameters,
        IBirStatement[] prefix, IBirExpression[] arguments, BirConstructorAdapterPayload tail)
    {
        if (arguments.Length != tail.Parameters.Value.Length) throw new InvalidOperationException("Alias constructor argument count differs from its declaration");
        var statements = prefix.Select(value => (IBirStatement)((BirGraphValue)value).DeepClone()).ToList();
        var replacements = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var local = FreshLocal();
            statements.Add(new BirVariable { Name = local, Type = tail.Signature.Value[index],
                Init = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)arguments[index]).DeepClone()) });
            replacements[tail.Parameters.Value[index]] = new BirLocal { Name = local, Sty = tail.Signature.Value[index] };
        }
        var materialized = Materialize(tail, replacements);
        statements.AddRange(materialized.Statements.Value);
        return new BirConstructorAdapterPayload { Parameters = parameters.Names, Signature = parameters.Types,
            Statements = statements.ToArray(), Arguments = materialized.Arguments.Value, TerminalSignature = tail.TerminalSignature };
    }

    static int SelectConstructor(string owner, BirClassDefinition type, TypeNode[] signature, TypeNode[] ownerArguments)
    {
        var matches = type.Ctors.Value.Select((constructor, index) => (constructor, index))
            .Where(candidate => SameSignature(Parameters(candidate.constructor).Types
                .Select(slot => SupertypeGraph.SubstOwnerTvs(slot, ownerArguments)).ToArray(), signature)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException($"Alias constructor '{owner}' matched {matches.Length} declarations");
        return matches[0].index;
    }

    static Dictionary<string, IBirExpression> MaterializeArguments(BirConstructor host, IBirExpression[] arguments, BirConstructorAdapterPayload adapter)
    {
        if (arguments.Length != adapter.Parameters.Value.Length) throw new InvalidOperationException("Alias constructor argument arity differs from its adapter");
        var used = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(host, used);
        foreach (var value in arguments) CollectNames((BirGraphValue)value, used);
        CollectNames(adapter, used);
        var statements = host.PreStmts.OrEmpty().ToList();
        var replacements = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = FreshName(FreshLocal(), used);
            statements.Add(new BirVariable { Name = name, Type = adapter.Signature.Value[index], Init = OptionalField<IBirExpression>.Of(arguments[index]) });
            replacements[adapter.Parameters.Value[index]] = new BirLocal { Name = name, Sty = adapter.Signature.Value[index] };
        }
        host.PreStmts = statements.ToArray();
        return replacements;
    }

    internal static BirConstructorAdapterPayload Specialize(BirConstructorAdapterPayload adapter, TypeNode[] ownerArguments)
    {
        if (ownerArguments.Length == 0) return adapter;
        return (BirConstructorAdapterPayload)adapter.DeepClone().Rewrite(node =>
            node.RewriteTypeFields(type => SupertypeGraph.SubstOwnerTvs(type, ownerArguments)));
    }

    static BirConstructorAdapterPayload Materialize(BirConstructorAdapterPayload adapter, IReadOnlyDictionary<string, IBirExpression> replacements)
        => (BirConstructorAdapterPayload)ConstructorAdapterLocals.Substitute(adapter, replacements);
    static void FreshenLocals(BirConstructorAdapterPayload adapter, params BirGraphValue[] consumers)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in consumers) CollectNames(consumer, used);
        ConstructorAdapterLocals.Freshen(adapter, name => FreshName(name, used));
    }
    static (string[] Names, TypeNode[] Types) Parameters(BirConstructor constructor) =>
        (constructor.Params.OrEmpty().Select(parameter => parameter.Name.Value).ToArray(), constructor.Params.OrEmpty().Select(parameter => parameter.Type.Value).ToArray());
    static TypeNode[] OwnArgs(BirClassDefinition type) => Enumerable.Range(0, type.TypeParams.OrEmpty().Length).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
    static string FreshLocal() => $"cir$alias${System.Threading.Interlocked.Increment(ref localCounter)}";
    static string FreshName(string basis, ISet<string> used)
    {
        var result = basis;
        for (var suffix = 1; !used.Add(result); suffix++) result = $"{basis}${suffix}";
        return result;
    }
    static bool IsIdentity(BirConstructorAdapterPayload adapter) => adapter.Arguments.Value.Length == adapter.Parameters.Value.Length
        && adapter.Arguments.Value.Select((value, index) => value is BirLocal read && read.Name.Value == adapter.Parameters.Value[index]).All(value => value);
    static bool SameSignature(IReadOnlyList<TypeNode> left, IReadOnlyList<TypeNode> right) => left.Count == right.Count
        && left.Select((slot, index) => slot.Equals(right[index])).All(equal => equal);
    static void CollectNames(BirGraphValue root, ISet<string> names) => root.Visit(node => {
        var name = node switch {
            BirLocal value => value.Name.OrDefault(), BirVariable value => value.Name.OrDefault(), BirSetLocal value => value.Name.OrDefault(),
            BirFieldGet value => value.Name.OrDefault(), BirFunctionDeclaration value => value.Name.OrDefault(), BirParameter value => value.Name.OrDefault(),
            BirLambdaParameter value => value.Name.OrDefault(), BirCatch value => value.Var.OrDefault(), BirForArray value => value.Var.OrDefault(),
            BirForIn value => value.Var.OrDefault(), BirForRange value => value.Var.OrDefault(), BirFor value => value.Var.OrDefault(),
            BirForEach value => value.Var.OrDefault(), BirRepeat value => value.Var.OrDefault(), _ => null,
        };
        if (name != null) names.Add(name);
    });
}
