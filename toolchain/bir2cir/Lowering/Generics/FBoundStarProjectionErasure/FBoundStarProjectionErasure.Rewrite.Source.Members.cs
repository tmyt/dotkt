using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    sealed class SourceMemberUse(BirGraphValue node)
    {
        internal BirGraphValue Node => node;
        internal bool Invocation => node is BirCallInstance;
        internal bool Exact => node switch { BirCallInstance value => value._exactExistentialBridgeOwnerCall.OrDefault() || value.MemberRef.HasValue,
            BirNewBoundDelegate value => value.MemberRef.HasValue, _ => true };
        internal TypeNode Owner { get => node switch { BirCallInstance value => value.OwnerType.OrDefault(),
            BirNewBoundDelegate value => value.OwnerType.OrDefault(), _ => null }; set {
            if (node is BirCallInstance call) call.OwnerType = BirGraphValue.CopyType(value);
            else ((BirNewBoundDelegate)node).OwnerType = BirGraphValue.CopyType(value);
        } }
        internal string Name { get => node is BirCallInstance call ? call.Method.OrDefault() : ((BirNewBoundDelegate)node).Method.OrDefault(); set {
            if (node is BirCallInstance call) call.Method = value; else ((BirNewBoundDelegate)node).Method = value;
        } }
        internal IBirExpression Receiver => node is BirCallInstance call ? call.Recv.OrDefault() : ((BirNewBoundDelegate)node).Recv.OrDefault();
        internal TypeNode[] Arguments => node.ApplicationTypeArguments.OrEmpty();
        internal OptionalField<TypeNode[]> Signature { get => node is BirCallInstance call ? call.Sig : ((BirNewBoundDelegate)node).Sig; set {
            if (node is BirCallInstance call) call.Sig = value; else ((BirNewBoundDelegate)node).Sig = value;
        } }
        internal OptionalField<TypeNode[]> ArgumentTypes => node is BirCallInstance call ? call.ArgTypes : ((BirNewBoundDelegate)node).ArgTypes;
        internal string Identity => node.DeclarationIdentityFact.OrDefault();
        internal TypeNode DeclaredReturn { get => node is BirCallInstance call ? call.Ret.OrDefault() : null; set {
            if (node is BirCallInstance call) call.Ret = BirGraphValue.CopyType(value);
        } }
        internal TypeNode[] SelectedSignature => Signature.OrDefault() ?? ArgumentTypes.OrDefault()
            ?? (node is BirCallInstance { ShapeTypes.HasValue: true } call
                && call.ShapeTypes.Value.Any(type => SignatureValueTypes.Of(type) is TypeNode.ByRef) ? call.ShapeTypes.Value : null);
        internal int ParameterCount => Signature.OrDefault()?.Length ?? ArgumentTypes.OrDefault()?.Length
            ?? (node as BirCallInstance)?.Args.OrEmpty().Length ?? 0;
        internal bool Property(out string name, out string accessor) => node is BirCallInstance call
            ? KotlinPropertyAccessors.TryCallIdentity(call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(),
                call.Method.OrDefault(), call.Prop.OrDefault(), out name, out accessor)
            : KotlinPropertyAccessors.TryCallIdentity(null, null, Name, null, out name, out accessor);
        internal void BindOwner(string name)
        {
            Owner = new TypeNode.Fqn(name);
            if (node is BirCallInstance call) { call.MemberVisibility = default; call.MemberOwnerTypeParams = default; call.Virtual = true; }
            else { var target = (BirNewBoundDelegate)node; target.MemberVisibility = default; target.MemberOwnerTypeParams = default;
                target.CalleeOwner = new TypeNode.Fqn(name); target.Virtual = true; }
        }
        internal void Parameters(IEnumerable<TypeNode> signature, IEnumerable<TypeNode> callee = null, bool writeCallee = true)
        {
            var types = signature.ToArray();
            Signature = types.Select(BirGraphValue.CopyType).ToArray();
            if (!writeCallee) return;
            var declarationTypes = (callee ?? types).Select(BirGraphValue.CopyType).ToArray();
            if (node is BirCallInstance call) call.CalleeParams = declarationTypes;
            else ((BirNewBoundDelegate)node).CalleeParams = declarationTypes;
        }
    }

    static BirGraphValue BindInheritedStarMemberSource(BirGraphValue node, SourceRewriteState state, bool alignResult = true)
    {
        if (node is not (BirCallInstance or BirNewBoundDelegate)) return node;
        var call = new SourceMemberUse(node);
        if (call.Exact || call.Owner is not TypeNode.Fqn { Args: { } args } owner || call.Name == null) return node;
        var context = state.Demand;
        var refs = state.References;
        state.Owners.TryGetValue(owner.Name, out var start);
        var projected = state.ProjectedType(call.Receiver as BirGraphValue);
        var ownerArguments = projected is { Args: { } projectedArgs } && (projected.Name == owner.Name
            || refs.TryExactPhysicalTypeName(projected.Name, projectedArgs.Length, out var exact) && exact == owner.Name)
            ? projectedArgs : args;
        var lexical = call.Receiver is BirThis || call.Receiver is BirGraphValue receiver && SourceExactOuter(receiver);
        var erasedCast = call.Receiver is BirCast cast && !cast._exactBridgeCast.OrDefault()
            && cast.Type.OrDefault() is TypeNode.Fqn { Args: { } castArguments } castOwner
            && castOwner.Name == owner.Name && castArguments.Any(ContainsStarOrTypeVariable);
        var existentialReceiver = projected != null || StripSourceNullability(SourceCarrierExpressionType(call.Receiver as BirGraphValue)) is TypeNode.Fqn receiverType
            && (state.Owners.Values.Any(candidate => candidate.ErasedName == receiverType.Name) || refs.IsExistentialPhysicalOwner(receiverType.Name));
        var signature = call.SelectedSignature;
        var reference = !lexical && signature?.Any(type => SignatureValueTypes.Of(type) is TypeNode.ByRef) == true;
        if (!args.Any(IsExistentialArgument) && !erasedCast && !existentialReceiver
            && !(!lexical && state.Projection.RequiresKotlinValueCarrier(owner)) && !reference) return node;
        var property = call.Property(out var sourceProperty, out var accessor);
        var sourceMember = property ? sourceProperty : call.Name;
        var authored = signature?.Any(type => type == null) == true ? null : signature;
        var arity = call.Arguments.Length;
        var count = call.ParameterCount;
        IReadOnlyList<TypeNode> OwnerArguments(string declaring) => declaring == owner.Name ? ownerArguments
            : start == null ? null : ProjectSourceOwnerArguments(start, ownerArguments, declaring, context);
        void CloseResult(TypeNode declaration, IReadOnlyList<TypeNode> declarationArguments = null)
        {
            if (declaration != null && call.DeclaredReturn != null && call.DeclaredReturn.Equals(declaration))
                call.DeclaredReturn = CloseDeclarationType(declaration, declarationArguments ?? args, call.Arguments);
        }
        BirGraphValue Align(TypeNode physical, TypeNode declaration, IReadOnlyList<TypeNode> declarationOwner)
        {
            if (!call.Invocation) return node;
            physical = SubstituteMethodTypeArguments(physical, call.Arguments);
            if (!alignResult) { SetSourceCallResult(node, physical); return node; }
            var semantic = declarationOwner == null ? null : SubstituteDeclarationTypeArguments(declaration, declarationOwner, call.Arguments);
            return semantic != null && ContainsExplicitExistential(semantic)
                && IsExistentialPhysicalCarrier(physical, state.Owners, refs)
                ? state.AlignExistential((IBirExpression)node, physical, semantic)
                : (BirGraphValue)AlignCallResultSource((IBirExpression)node, physical, protectExactCast: true);
        }
        if (start is { Needed: true } && FindDeclaringSourceOwner(start, sourceMember, accessor, count, arity,
            authored, call.Identity, context, state.Declarations) is { } selected)
        {
            var (declaring, row) = selected;
            if (row.ReferenceContract != null)
                return (BirGraphValue)BindReferenceContractSource((IBirExpression)node, row.ReferenceContract,
                    OwnerArguments(declaring.Name), property, sourceProperty, accessor);
            CloseResult(row.Declaration.Ret.OrDefault());
            call.BindOwner(declaring.ErasedName);
            call.Name = row.Slot.ExplicitClrName.OrDefault() ?? row.Slot.Name.OrDefault();
            call.Parameters(row.Slot.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()),
                row.Slot.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType));
            MarkPhysicalPropertyCallSource(node, property, sourceProperty, accessor,
                row.Declaration.DeclarationId.OrDefault() is { } identity ? ExistentialSlotIdentity(identity, declaring.Name) : null);
            var declarationResult = row.Declaration.Ret.OrDefault() ?? new TypeNode.Fqn("kotlin.Unit");
            return Align(EraseOwnerTv(declarationResult, state.Owners, refs), declarationResult, OwnerArguments(declaring.Name));
        }
        if (start is { Needed: true } && FindInheritedSourceBaseMember(start, sourceMember, accessor, count, arity,
            call.Identity, context) is { } inherited)
        {
            var (bridgeOwner, declaring, declaration) = inherited;
            CloseResult(declaration.Ret.OrDefault());
            call.BindOwner(bridgeOwner.ErasedName);
            call.Name = BaseStarMethodNameSource(declaring, (IBirTypeDeclaration)context.Preparation.Bindings.SourceDefinitions[declaring].Source, declaration);
            call.Parameters(declaration.Params.OrEmpty().Select(parameter => EraseOwnerTv(parameter.Type.OrDefault(), state.Owners, refs)),
                writeCallee: false);
            MarkPhysicalPropertyCallSource(node, property, sourceProperty, accessor,
                declaration.DeclarationId.OrDefault() is { } identity ? ExistentialSlotIdentity(identity, bridgeOwner.Name) : null);
            var declarationResult = declaration.Ret.OrDefault() ?? new TypeNode.Fqn("kotlin.Unit");
            return Align(EraseOwnerTv(declarationResult, state.Owners, refs), declarationResult, OwnerArguments(declaring));
        }
        if (authored?.Any(type => SignatureValueTypes.Of(type) is TypeNode.ByRef) == true)
            foreach (var (application, _) in SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = owner }, context.Preparation.SourceSupertypes, refs))
                if (refs.TryManagedReferenceContract(application, sourceMember, accessor, arity, authored, count, call.Identity,
                    out var referenceOwner, out var member, out var invoke))
                {
                    var slot = new BirFunctionDeclaration { Name = member.Name, Ret = BirGraphValue.CopyType(member.ReturnTypeNode),
                        Params = member.ParamTypeNodes.Select((type, index) => new BirParameter { Name = "p" + index, Type = BirGraphValue.CopyType(type) }).ToArray(),
                        DeclarationId = member.DeclarationId == null ? OptionalField<string>.Null : member.DeclarationId };
                    return (BirGraphValue)BindReferenceContractSource((IBirExpression)node,
                        new SourceReferenceContract(referenceOwner, slot, invoke), Array.Empty<TypeNode>(), property,
                        sourceProperty, accessor, constructedOwner: referenceOwner);
                }
        if (refs.TryStarProjectionMember(owner, sourceMember, accessor, arity, authored, count, call.Identity,
            out var erasedOwner, out var erasedMethod, out var erasedSignature, out var declarationReturn, out var physicalReturn, out var slotId))
        {
            CloseResult(declarationReturn);
            call.BindOwner(erasedOwner); call.Name = erasedMethod; call.Parameters(erasedSignature);
            // The selected slot owns the MethodSpec frame, not a new frontend call identity.
            if (slotId != null && call.Arguments.Length != 0) state.SelectedMethodArgumentSlots[node] = slotId;
            MarkPhysicalPropertyCallSource(node, property, sourceProperty, accessor);
            return Align(physicalReturn, declarationReturn, ownerArguments);
        }
        // A fake override can name the child as its receiver while its selected
        // declaration and its physical forwarding slot belong to different ancestors.
        if (call.Identity is { } id && refs.TryExistentialPhysicalOwner(owner.Name, out var carrier)
            && refs.TryDeclarationIdentity(id, out _, out var declaringName, out _, out _)
            && refs.TryInheritedStarSlot(carrier, id, arity, count, out var slotOwner, out var referenceSlot, out var sourceResult))
        {
            var frames = SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = owner },
                context.Preparation.SourceSupertypes, refs, sourceFrames: true, physicalSourceApplications: true)
                .Where(edge => edge.spec.Name == declaringName).Select(edge => edge.spec).Take(2).ToArray();
            if (frames.Length == 1)
            {
                // Reachable yields the physical start unchanged and source applications for its referenced parents.
                // Inner applications are own-first, whereas the declaration's type variables are enclosing-first.
                var application = ReferenceEquals(frames[0], owner)
                    ? owner with { Args = refs.SourceArgumentsOfPhysicalOwner(owner) } : frames[0];
                var frame = refs.SourceDeclarationArguments(application) ?? Array.Empty<TypeNode>();
                CloseResult(sourceResult, frame);
                call.BindOwner(slotOwner); call.Name = referenceSlot.Name;
                call.Parameters(referenceSlot.ParamTypeNodes, referenceSlot.SignatureParameters);
                MarkPhysicalPropertyCallSource(node, property, sourceProperty, accessor, referenceSlot.DeclarationId);
                return Align(referenceSlot.SuspendReturnType ?? referenceSlot.ReturnTypeNode, sourceResult, frame);
            }
        }
        if (count == 0 && !call.Signature.HasValue) call.Signature = Array.Empty<TypeNode>();
        return node;
    }

    static bool MatchesSourceMember(BirFunctionDeclaration declaration, string member, string accessor)
        => KotlinPropertyAccessors.TryIdentity(declaration, out var property, out var kind)
            ? accessor != null && property == member && kind == accessor
            : accessor == null && (declaration.ExistentialSourceMember.OrDefault() ?? declaration.Name.OrDefault()) == member;

    static (Owner Owner, SourceSlot Slot)? FindDeclaringSourceOwner(Owner start, string member, string accessor,
        int count, int arity, IReadOnlyList<TypeNode> signature, string identity, CarrierDemandPlan context,
        SourceCarrierDeclarations declarations)
    {
        var frontier = new List<Owner> { start };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (frontier.Count != 0)
        {
            var matches = new List<(Owner, SourceSlot)>();
            foreach (var owner in frontier)
                if (seen.Add(owner.Name))
                    foreach (var row in declarations.Slots(owner).Values)
                        if ((identity != null ? row.Declaration.DeclarationId.OrDefault() == identity : MatchesSourceMember(row.Declaration, member, accessor))
                            && !row.Declaration.Static.OrDefault() && row.Declaration.Params.OrEmpty().Length == count
                            && row.Declaration.TypeParams.OrEmpty().Length == arity
                            && (identity != null || signature == null || row.FrozenSignature.SequenceEqual(signature))) matches.Add((owner, row));
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) return null;
            frontier = frontier.SelectMany(owner => SourceAncestors(owner, context.Owners)).ToList();
        }
        return null;
    }

    static (Owner BridgeOwner, string Declaring, BirFunctionDeclaration Declaration)? FindInheritedSourceBaseMember(
        Owner start, string member, string accessor, int count, int arity, string identity, CarrierDemandPlan context)
    {
        var frontier = new List<Owner> { start };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (frontier.Count != 0)
        {
            var matches = new List<(Owner, string, BirFunctionDeclaration)>();
            foreach (var owner in frontier)
                if (seen.Add(owner.Name))
                    foreach (var (declaring, method) in InheritedConcreteBaseMethodsSource(owner, context))
                        if ((identity != null ? method.DeclarationId.OrDefault() == identity : MatchesSourceMember(method, member, accessor))
                            && !method.Static.OrDefault() && method.Vis.OrDefault() != "private"
                            && method.Params.OrEmpty().Length == count && method.TypeParams.OrEmpty().Length == arity)
                            matches.Add((owner, declaring, method));
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) return null;
            frontier = frontier.SelectMany(owner => SourceAncestors(owner, context.Owners)).ToList();
        }
        return null;
    }

    static IEnumerable<Owner> SourceAncestors(Owner owner, IReadOnlyDictionary<string, Owner> owners)
    {
        if (owner.BaseType is { } baseType && owners.TryGetValue(baseType.Name, out var baseOwner)) yield return baseOwner;
        foreach (var parent in owner.InterfaceTypes) if (owners.TryGetValue(parent.Name, out var ancestor)) yield return ancestor;
    }

    static IReadOnlyList<TypeNode> ProjectSourceOwnerArguments(Owner start, IReadOnlyList<TypeNode> arguments,
        string target, CarrierDemandPlan context)
    {
        var pending = new Queue<(Owner Owner, TypeNode[] Arguments, int Depth)>();
        pending.Enqueue((start, arguments.ToArray(), 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        int? nearest = null;
        while (pending.Count != 0)
        {
            var (owner, current, depth) = pending.Dequeue();
            if (nearest is int limit && depth > limit) break;
            var key = owner.Name + "|[" + string.Join(",", current.Select(TypeNode.ToJson)) + "]";
            if (!seen.Add(key)) continue;
            if (owner.Name == target) { nearest = depth; matches.TryAdd(key, current); continue; }
            var edges = owner.BaseType == null ? owner.InterfaceTypes : new[] { owner.BaseType }.Concat(owner.InterfaceTypes);
            foreach (var edge in edges)
                if (context.Owners.TryGetValue(edge.Name, out var ancestor)
                    && SubstituteDeclarationTypeArguments(edge, current, Array.Empty<TypeNode>()) is TypeNode.Fqn projected)
                {
                    var values = projected.Args ?? Array.Empty<TypeNode>();
                    if (values.Length == ((IBirTypeDeclaration)ancestor.DefinitionBinding.Source).TypeParams.OrEmpty().Length) pending.Enqueue((ancestor, values, depth + 1));
                }
        }
        return matches.Count == 1 ? matches.Values.Single() : null;
    }
}
