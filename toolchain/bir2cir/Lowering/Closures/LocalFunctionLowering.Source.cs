using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A lexical declaration ID selects one static CLR MethodDef on the explicit
// Kotlin source owner (or facade for an erased alias). Move the actual record;
// neither names nor executable bodies reconstruct declaration ownership.
static partial class LocalFunctionLowering
{
    sealed record Binding(string Name, string Owner, int[] OwnerArgPositions, int[] SemanticOwnerArgOrder)
    {
        internal int[] ByRefCaptureSlots { get; init; } = Array.Empty<int>();
        internal TypeNode[] ParameterTypes { get; init; }
        internal string DeclarationId { get; init; }
    }

    public static void Apply(BirFileDeclaration file, ReferenceMetadataIndex refs,
        Action<BirFunctionDeclaration, NullableRepresentationFrame> projectValueSignature = null)
    {
        var facade = file.FileClass.OrDefault()
            ?? throw new InvalidOperationException("BIR file has no fileClass while lowering local functions");
        if (!file.Methods.HasValue) file.Methods = Array.Empty<BirFunctionDeclaration>();
        var types = file.Types.OrEmpty().Where(type => type.Name.HasValue)
            .ToDictionary(type => type.Name.Value, StringComparer.Ordinal);
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var counter = 0;

        IBirTypeDeclaration TargetOwner(string semanticOwner, out string physicalOwner)
        {
            if (semanticOwner == facade || refs.Aliases.ContainsKey(ReferenceMetadataIndex.BareOwnerFqn(semanticOwner)))
            { physicalOwner = facade; return null; }
            if (!types.TryGetValue(semanticOwner, out var owner))
                throw new InvalidOperationException($"local function has missing semantic source type '{semanticOwner}'");
            physicalOwner = semanticOwner;
            if (!owner.Methods.HasValue) owner.Methods = Array.Empty<BirFunctionDeclaration>();
            return owner;
        }

        void Hoist(BirLocalFunction local, string semanticOwner)
        {
            if (!local.Id.HasValue || !local.Decl.HasValue)
                throw new InvalidOperationException("malformed BIR localFun declaration");
            var id = local.Id.Value;
            var declaration = local.Decl.Value;
            var owner = TargetOwner(semanticOwner, out var physicalOwner);
            var sourceName = declaration.SourceName.OrDefault() ?? "local";
            var name = $"dotkt$local{counter++}_{sourceName}";
            declaration.SourceName = default;
            declaration.Name = name;
            declaration.Generated = true;
            var identity = DeclarationIdentityBinding.PhysicalOnlyId(facade + ":" + id, "local-function");
            declaration.DeclarationId = identity;
            declaration.DeclarationSourceName = sourceName;
            var byRef = PrepareCaptureLocations(declaration);
            projectValueSignature?.Invoke(declaration, owner == null ? null : NullableRepresentationViews.TypeFacts((BirGraphValue)owner)?.NullableFrame);
            var binding = PrepareGenericOwnerBinding(declaration, physicalOwner, owner) with {
                Name = name, Owner = physicalOwner, DeclarationId = identity, ByRefCaptureSlots = byRef,
                SemanticOwnerArgOrder = SemanticOwnerArgOrder(owner),
                ParameterTypes = declaration.Params.OrEmpty().Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray(),
            };
            if (!bindings.TryAdd(id, binding)) throw new InvalidOperationException($"duplicate BIR local function declaration id '{id}'");
            // Register before visiting the body: recursion is an ordinary ID edge.
            CollectNode(declaration, semanticOwner);
            if (owner == null) file.Methods = file.Methods.Value.Append(declaration).ToArray();
            else owner.Methods = owner.Methods.Value.Append(declaration).ToArray();
        }

        int[] SemanticOwnerArgOrder(IBirTypeDeclaration owner)
        {
            if (owner?.TypeParams.HasValue != true) return Array.Empty<int>();
            var total = owner.TypeParams.Value.Length;
            if (owner.Mods.OrDefault()?.Inner.OrDefault() != true
                || owner is not BirClassDefinition { OuterTypeParamCount.HasValue: true } captured
                || captured.OuterTypeParamCount.Value == 0) return Enumerable.Range(0, total).ToArray();
            var count = captured.OuterTypeParamCount.Value;
            if (count > total || !owner.SemanticOwner.HasValue || !types.TryGetValue(owner.SemanticOwner.Value, out var parent))
                throw new InvalidOperationException($"inner local-function owner '{owner.Name.OrDefault()}' has an invalid semantic owner frame");
            var parentOrder = SemanticOwnerArgOrder(parent);
            if (parentOrder.Length != count)
                throw new InvalidOperationException($"inner local-function owner '{owner.Name.OrDefault()}' captures {count} slots, but its semantic owner declares {parentOrder.Length}");
            return Enumerable.Range(count, total - count).Concat(parentOrder).ToArray();
        }

        void CollectNode(BirGraphValue node, string semanticOwner)
        {
            if (node == null || node is IBirExecutablePayload) return;
            node.VisitSourceChildren(slot => CollectNode(slot.Value, semanticOwner), region => {
                var survivors = new List<IBirStatement>();
                var removed = false;
                foreach (var statement in region.Statements)
                {
                    if (statement is BirLocalFunction local) { Hoist(local, semanticOwner); removed = true; }
                    else { CollectNode((BirGraphValue)statement, semanticOwner); survivors.Add(statement); }
                }
                if (removed)
                    (region.ReplaceStatements ?? throw new InvalidOperationException("A lexical statement vector has no owning setter"))(survivors.ToArray());
                if (region.Result is { } result) CollectNode(result.Value, semanticOwner);
            });
        }

        foreach (var method in file.Methods.Value.ToArray()) CollectNode(method, facade);
        foreach (var field in file.Fields.OrEmpty()) CollectNode(field, facade);
        foreach (var owner in types.Values.ToArray())
        {
            var name = owner.Name.Value;
            foreach (var method in owner.Methods.OrEmpty().ToArray()) CollectNode(method, name);
            foreach (var constructor in owner.Ctors.OrEmpty()) CollectNode(constructor, name);
            foreach (var field in owner.Fields.OrEmpty()) CollectNode(field, name);
            foreach (var property in owner.Properties.OrEmpty()) CollectNode(property, name);
        }
        RewriteUses(file);

        BirGraphValue RewriteUses(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            if (node is BirCallLocal or BirLocalFunctionReference)
            {
                var id = node is BirCallLocal call ? call.Id.OrDefault() : ((BirLocalFunctionReference)node).Id.OrDefault();
                if (id == null || !bindings.TryGetValue(id, out var binding))
                    throw new InvalidOperationException($"unbound BIR {((BirGraphNode)node).Tag} declaration id '{id}'");
                node = RewriteUse(node, binding);
            }
            node.VisitChildSlots(slot => slot.Replace(RewriteUses(slot.Value)));
            return node;
        }
    }

    static BirGraphValue RewriteUse(BirGraphValue source, Binding binding)
    {
        var call = source as BirCallLocal;
        var reference = source as BirLocalFunctionReference;
        if (binding.ByRefCaptureSlots.Length != 0 && call == null)
            throw new InvalidOperationException("a borrowed local-function capture requires a direct call");
        // These direct-call slots remain lvalues. The selected declaration's
        // final byref signature below is the sole argument-location contract.
        if (binding.ByRefCaptureSlots.Length != 0 && (!call.Args.HasValue || !call.Sig.HasValue))
            throw new InvalidOperationException("a borrowed local-function capture requires a direct call");
        var arguments = call?.TypeArgs ?? reference.TypeArgs;
        var ownerArguments = Enumerable.Range(0, binding.OwnerArgPositions.Length).Select(slot => {
            var position = binding.OwnerArgPositions[slot];
            return position >= 0 && arguments.HasValue && position < arguments.Value.Length
                ? BirGraphValue.CopyType(arguments.Value[position]) : new TypeNode.Tv("type", slot);
        }).ToArray();
        var semanticArguments = binding.SemanticOwnerArgOrder.Length == ownerArguments.Length
            ? binding.SemanticOwnerArgOrder.Select(index => ownerArguments[index]).ToArray() : ownerArguments;
        var owner = new TypeNode.Fqn(binding.Owner, ownerArguments.Length == 0 ? null : semanticArguments);
        OptionalField<TypeNode[]> remaining = arguments;
        if (arguments.HasValue)
        {
            var consumed = binding.OwnerArgPositions.Where(position => position >= 0).ToHashSet();
            var retained = arguments.Value.Where((_, index) => !consumed.Contains(index)).Select(BirGraphValue.CopyType).ToArray();
            remaining = retained.Length == 0 ? default : OptionalField<TypeNode[]>.Of(retained);
        }
        var signature = binding.ParameterTypes.Select(BirGraphValue.CopyType).ToArray();
        if (call != null)
            return new BirCallStatic {
                Owner = OptionalField<TypeNode>.Null, CalleeOwner = owner, Method = binding.Name,
                DeclarationId = binding.DeclarationId, Sig = signature, TypeArgs = remaining,
                Args = call.Args, Ret = call.Ret, Sty = call.Sty, SuspendCall = call.SuspendCall, RetSuspendFnTypePre = call.RetSuspendFnTypePre,
            };
        return new BirNewDelegate {
            CalleeOwner = owner, Method = binding.Name, DeclarationId = binding.DeclarationId,
            Sig = signature, TypeArgs = remaining, FuncType = reference.FuncType,
        };
    }
}
