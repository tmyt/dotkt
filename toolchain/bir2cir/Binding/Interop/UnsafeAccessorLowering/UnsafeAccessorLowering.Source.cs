using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    sealed class SourceHost
    {
        internal readonly BirFileDeclaration File;
        internal readonly IBirTypeDeclaration Type;
        internal readonly string Name;
        internal SourceHost(BirFileDeclaration file, IBirTypeDeclaration type, string name)
        {
            File = file; Type = type; Name = name;
            if (type == null) { if (!file.Methods.HasValue) file.Methods = Array.Empty<BirFunctionDeclaration>(); }
            else if (!type.Methods.HasValue) type.Methods = Array.Empty<BirFunctionDeclaration>();
        }
        internal string NestedIn => Type?.NestedIn.OrDefault();
        internal BirFunctionDeclaration[] Methods => Type == null ? File.Methods.OrEmpty() : Type.Methods.OrEmpty();
        internal BirConstructor[] Constructors => Type?.Ctors.OrEmpty() ?? Array.Empty<BirConstructor>();
        internal BirFieldDeclaration[] Fields => Type == null ? File.Fields.OrEmpty() : Type.Fields.OrEmpty();
        internal BirFunctionDeclaration[] LookupMethods;
        internal BirConstructor[] LookupConstructors;
        internal BirFieldDeclaration[] LookupFields;
        internal void Append(BirFunctionDeclaration method, SuspendColdLowering.SourceDeclarationTransitions transitions)
        {
            if (Type == null) File.Methods = Methods.Append(method).ToArray();
            else Type.Methods = Methods.Append(method).ToArray();
            transitions.PublishMethods(File, Type, new[] { method });
        }
    }

    sealed record SourceAccessor(string EntryName, string HolderName, TypeNode[] Signature,
        int OwnerTypeParamCount, int MethodTypeParamCount, string Identity = null);

    sealed class SourceAccessContext
    {
        internal readonly Dictionary<string, SourceHost> Hosts;
        internal readonly ReferenceMetadataIndex References;
        internal readonly SuspendColdLowering.SourceDeclarationTransitions Transitions;
        internal readonly Dictionary<string, SourceAccessor> Accessors = new(StringComparer.Ordinal);
        readonly FamilyAccessPolicy<SourceHost> _family;
        readonly Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> _physicalType;

        internal SourceAccessContext(IReadOnlyList<BirFileDeclaration> roots, Dictionary<string, SourceHost> hosts,
            ReferenceMetadataIndex refs, SuspendColdLowering.SourceDeclarationTransitions transitions)
        {
            Hosts = hosts; References = refs; Transitions = transitions;
            _physicalType = TypedPhysicalValueCoercion.ResultTypeOracle(roots);
            _family = new(hosts, SupertypeGraph.CollectSource(roots), refs, h => h.Name, h => h.NestedIn,
                h => h.Type?.Vis.OrDefault(), SourceSelfType,
                (caller, target) => SourcePrivateAccess(caller, target, hosts));
        }
        internal bool Allows(SourceHost caller, IBirExpression receiver, TypeNode.Fqn owner, string visibility,
            bool isStatic, IReadOnlyDictionary<string, TypeNode> scope, IEnumerable<TypeNode> signature)
        {
            if (visibility != "protected" || isStatic || receiver == null) return false;
            return _family.Allows(caller, receiver is BirThis ? SourceSelfType(caller) : _physicalType((BirGraphValue)receiver, scope),
                owner, visibility, isStatic, true, signature);
        }
    }

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs,
        SuspendColdLowering.SourceDeclarationTransitions transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        var callers = new List<SourceHost>();
        foreach (var file in roots)
        {
            if (file.FileClass.HasValue) callers.Add(new(file, null, file.FileClass.Value));
            foreach (var type in file.Types.OrEmpty())
                if (type.Name.HasValue) callers.Add(new(file, type, type.Name.Value));
        }
        // Duplicate physical hosts share lookup snapshots, but executable inputs
        // and appends retain their exact occurrence. Lookup is first-host-wins.
        var hosts = callers.GroupBy(h => h.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => {
            var occurrences = g.ToArray();
            var methods = occurrences.SelectMany(h => h.Methods).ToArray();
            var constructors = occurrences.SelectMany(h => h.Constructors).ToArray();
            var fields = occurrences.SelectMany(h => h.Fields).ToArray();
            foreach (var occurrence in occurrences)
            { occurrence.LookupMethods = methods; occurrence.LookupConstructors = constructors; occurrence.LookupFields = fields; }
            return occurrences[0];
        }, StringComparer.Ordinal);
        if (hosts.Count == 0) return;
        var context = new SourceAccessContext(roots, hosts, refs, transitions);
        foreach (var caller in callers)
        {
            foreach (var method in caller.Methods.ToArray())
                if (method.Body.HasValue) RewriteSourceSequence(method.Body.Value, SourceScope(method), caller, context);
            foreach (var ctor in caller.Constructors)
            {
                var scope = SourceScope(ctor);
                // Delegation operands can read locals authored by preStmts;
                // carry that scope through both delegation and the body.
                if (ctor.PreStmts.HasValue) scope = RewriteSourceSequence(ctor.PreStmts.Value, scope, caller, context);
                RewriteSourceExpressions(ctor.BaseArgs.OrEmpty(), scope, caller, context);
                RewriteSourceExpressions(ctor.ThisArgs.OrEmpty(), scope, caller, context);
                if (ctor.Body.HasValue) RewriteSourceSequence(ctor.Body.Value, scope, caller, context);
            }
            foreach (var field in caller.Fields)
                if (field.Init.HasValue) field.Init = OptionalField<IBirExpression>.Of((IBirExpression)
                    RewriteSource((BirGraphValue)field.Init.Value, EmptySourceScope(), caller, context));
        }
    }

    static ImmutableDictionary<string, TypeNode> EmptySourceScope()
        => ImmutableDictionary.Create<string, TypeNode>(StringComparer.Ordinal);
    static ImmutableDictionary<string, TypeNode> SourceScope(BirGraphValue declaration)
    {
        var scope = EmptySourceScope();
        declaration.VisitSourceParameters((name, type) => scope = scope.SetItem(name, type));
        return scope;
    }
    static ImmutableDictionary<string, TypeNode> RewriteSourceSequence(IBirStatement[] statements,
        ImmutableDictionary<string, TypeNode> scope, SourceHost caller, SourceAccessContext context)
    {
        foreach (ref var statement in statements.AsSpan())
        {
            statement = (IBirStatement)RewriteSource((BirGraphValue)statement, scope, caller, context);
            if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                scope = scope.SetItem(variable.Name.Value, variable.Type.Value);
        }
        return scope;
    }
    static void RewriteSourceExpressions(IBirExpression[] values, ImmutableDictionary<string, TypeNode> scope,
        SourceHost caller, SourceAccessContext context)
    {
        for (var index = 0; index < values.Length; index++)
            values[index] = (IBirExpression)RewriteSource((BirGraphValue)values[index], scope, caller, context);
    }
    static BirGraphValue RewriteSource(BirGraphValue node, ImmutableDictionary<string, TypeNode> scope,
        SourceHost caller, SourceAccessContext context)
    {
        if (node == null || node is IBirExecutablePayload) return node;
        var inner = scope;
        node.VisitSourceParameters((name, type) => inner = inner.SetItem(name, type));
        node.VisitSourceChildren(slot => slot.Replace(RewriteSource(slot.Value, inner, caller, context)), region => {
            var current = RewriteSourceSequence(region.Statements, inner, caller, context);
            if (region.Result is { } result) result.Replace(RewriteSource(result.Value, current, caller, context));
        });
        if (node is BirEventAdd add && add.MemberVisibility.OrDefault() is "private" or "protected" or "internal")
            node = ClrMemberResolution.RestrictedEventCall(add, context.References) ?? node;
        else if (node is BirEventRemove remove && remove.MemberVisibility.OrDefault() is "private" or "protected" or "internal")
            node = ClrMemberResolution.RestrictedEventCall(remove, context.References) ?? node;
        return node switch {
            BirCallInstance or BirCallStatic or BirConstrainedCall => RewriteSourceMethod(node, caller, context, inner),
            BirNewBoundDelegate bound => RewriteSourceBound(bound, caller, context, inner),
            BirNew construction => RewriteSourceConstructor(construction, caller, context),
            BirFieldGet or BirFieldSetExpression or BirSetField or BirStaticFieldGet or BirStaticFieldSet or BirLateinitGet
                => RewriteSourceField(node, caller, context, inner),
            _ => DropSourceMemberFacts(node, preserveConstructorSignature: node is BirNewClr),
        };
    }

    internal static void DropFacts(IReadOnlyList<BirFileDeclaration> roots)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            DropSourceMemberFacts(node, node is BirNew or BirNewClr);
            node.VisitChildSlots(slot => Walk(slot.Value));
        }
        foreach (var root in roots) Walk(root);
    }

    static TypeNode.Fqn SourceSelfType(SourceHost host)
    {
        var count = host.Type == null ? 0 : TypeParameterFrame.Count(host.Type);
        return new(host.Name, count == 0 ? null : Enumerable.Range(0, count).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
    }
    static bool SourcePrivateAccess(string caller, string target, IReadOnlyDictionary<string, SourceHost> hosts)
    {
        if (caller == target) return true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (caller != null && seen.Add(caller) && hosts.TryGetValue(caller, out var host))
        { if (host.NestedIn == target) return true; caller = host.NestedIn; }
        return false;
    }
    static string SourceTypeKey(TypeNode type) => type == null ? "null" : TypeNode.ToJson(type);
    static TypeNode CopySourceType(TypeNode type) => type == null ? null : BirGraphValue.CopyType(type);
    static TypeNode[] CopySourceTypes(IEnumerable<TypeNode> types) => types.Select(CopySourceType).ToArray();
    static TypeNode OpenSourceOwner(TypeNode.Fqn owner, int count) => new TypeNode.Fqn(owner.Name,
        count == 0 ? null : Enumerable.Range(0, count).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
    static TypeNode SourceCallOwner(SourceHost caller, SourceAccessor definition, TypeNode.Fqn target)
        => definition.HolderName == null ? SourceSelfType(caller)
            : new TypeNode.Fqn(definition.HolderName, target.Args is { Length: > 0 } ? CopySourceTypes(target.Args) : null);
    // Referenced native holders use the selected physical owner's complete
    // frame, not a potentially different Kotlin projection of that owner.
    static BirTypeParameter[] SourceOwnerParameters(SourceHost host, BirTypeParameter[] semantic,
        TypeNode.Fqn owner, ReferenceMetadataIndex refs)
        => host != null ? (host.Type == null ? Array.Empty<BirTypeParameter>() : TypeParameterFrame.Declarations(host.Type).ToArray())
            : refs?.OwnerTypeParametersInPhysicalFrame(owner.Name)?.ToArray() ?? semantic;
    static BirTypeParameter[] SourceMethodParameters(BirFunctionDeclaration target, BirTypeParameter[] semantic)
        => target?.TypeParams.OrDefault() ?? semantic;

    static T SelectSourceMethod<T>(T[] candidates, TypeNode[] signature, TypeNode[] ownerArgs,
        Func<T, BirParameter[]> parameters) where T : class
    {
        if (candidates.Length == 0) return null;
        var exact = candidates.Where(candidate => {
            var declared = parameters(candidate);
            for (var index = 0; index < declared.Length; index++)
            {
                var type = declared[index].Type.OrDefault();
                if (SourceTypeKey(ownerArgs == null ? type : SubstituteOwnerSlots(type, ownerArgs)) != SourceTypeKey(signature[index])) return false;
            }
            return true;
        }).ToArray();
        return exact.Length == 1 ? exact[0] : candidates.Length == 1 ? candidates[0] : null;
    }
    static ReferenceMetadataIndex.SourceUnsafeAccessorMethod SourceReferencedMethod(BirFunctionDeclaration local,
        string id, TypeNode.Fqn owner, string method, int arity, bool isStatic, TypeNode[] signature,
        TypeNode result, BirTypeParameter[] methodParameters, bool propertyAccessor, ReferenceMetadataIndex refs)
    {
        if (local != null) return null;
        if (id == null)
            return !propertyAccessor && refs?.HasDotKtOwner(owner.Name) == true && signature.All(t => t != null)
                && refs.TryUnsafeAccessorVirtualSourceMethod(owner.Name, method, arity, isStatic, signature, result,
                    owner.Args ?? Array.Empty<TypeNode>(), methodParameters, out var virtualTarget) ? virtualTarget : null;
        if (refs?.TryUnsafeAccessorSourceMethod(id, owner.Name, arity, isStatic, out var target) == true) return target;
        throw new InvalidOperationException($"UnsafeAccessor target '{owner.Name}.{method}' has no exact referenced MethodDef for declaration identity '{id}'");
    }
}
