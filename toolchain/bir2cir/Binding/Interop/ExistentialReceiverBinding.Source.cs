using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExistentialReceiverBinding
{
    internal static Index Collect(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyDictionary<string, string> sourceDeclarations = null)
    {
        var index = new Index();
        if (sourceDeclarations != null) index.SourceDeclarations = sourceDeclarations;
        foreach (var root in roots)
            foreach (var owner in root.Types.OrEmpty())
            {
                if (owner.Name.OrDefault() is not string name) continue;
                foreach (var field in owner.Fields.OrEmpty())
                    if (field.Name.HasValue && field.Type.HasValue)
                        index.Fields[(name, field.Name.Value)] = BirGraphValue.CopyType(field.Type.Value);
                var source = owner switch {
                    BirInterfaceDefinition declaration => declaration.KotlinType.OrDefault(),
                    _ => null,
                };
                if (!owner.Generated.OrDefault() || source is not TypeNode.Fqn { Args: { Length: > 0 } arguments } semantic
                    || !arguments.All(argument => argument is TypeNode.Star) || !owner.Methods.HasValue) continue;
                var members = new List<Member>();
                foreach (var method in owner.Methods.Value)
                {
                    if (method.Static.OrDefault() || !method.Name.HasValue) continue;
                    var property = KotlinPropertyAccessors.TryIdentity(method, out var propertyName, out var accessor);
                    var identity = method.DeclarationId.OrDefault();
                    members.Add(new(method.Name.Value, property ? propertyName : method.ExistentialSourceMember.OrDefault() ?? method.Name.Value,
                        property ? accessor : null, method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault())
                            .Where(type => type != null).Select(BirGraphValue.CopyType).ToArray(),
                        BirGraphValue.CopyType(method.Ret.OrDefault()), method.TypeParams.OrEmpty().Length, identity,
                        method.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).Select(BirGraphValue.CopyType).ToArray(),
                        identity == null ? null : index.SourceDeclarations.GetValueOrDefault(identity)));
                }
                index.Members[name] = members;
                index.SemanticOwnerByPhysical[name] = semantic.Name;
                index.Bases[name] = owner.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().Select(parent => parent.Name).ToArray();
            }
        return index;
    }

    internal static void Apply(BirFileDeclaration root, Index index, ReferenceMetadataIndex refs)
    {
        foreach (var method in root.Methods.OrEmpty()) VisitBody(method.Params, method.Body, method.Ret.OrDefault());
        foreach (var owner in root.Types.OrEmpty())
        {
            foreach (var method in owner.Methods.OrEmpty()) VisitBody(method.Params, method.Body, method.Ret.OrDefault());
            foreach (var constructor in owner.Ctors.OrEmpty()) VisitBody(constructor.Params, constructor.Body, null);
        }

        void VisitBody(OptionalField<BirParameter[]> parameters, OptionalField<IBirStatement[]> body, TypeNode result)
        {
            if (!parameters.HasValue || !body.HasValue) return;
            var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var parameter in parameters.Value)
                if (parameter.Name.HasValue && parameter.Type.HasValue) locals[parameter.Name.Value] = parameter.Type.Value;
            Statements(body.Value, locals, result);
        }
        void Statements(IBirStatement[] body, Dictionary<string, TypeNode> locals, TypeNode result = null)
        {
            for (var at = 0; at < body.Length; at++) body[at] = (IBirStatement)Visit((BirGraphValue)body[at], locals, result);
        }
        BirGraphValue Visit(BirGraphValue node, Dictionary<string, TypeNode> locals, TypeNode expected = null)
        {
            if (node is IBirExecutablePayload) return node;
            void Expression(OptionalField<IBirExpression> value, Action<OptionalField<IBirExpression>> write, TypeNode wanted = null)
            {
                if (value.HasValue) write(OptionalField<IBirExpression>.Of((IBirExpression)Visit((BirGraphValue)value.Value, locals, wanted)));
            }
            switch (node)
            {
                case BirReturn value:
                    Expression(value.Value, replacement => value.Value = replacement, expected);
                    return node;
                case BirVariable variable:
                    Expression(variable.Init, replacement => variable.Init = replacement, variable.Type.OrDefault());
                    if (variable.Name.HasValue && variable.Type.HasValue) locals[variable.Name.Value] = variable.Type.Value;
                    return node;
                case BirValueBlock block:
                    if (block.Stmts.HasValue) Statements(block.Stmts.Value, locals);
                    Expression(block.Result, replacement => block.Result = replacement, expected ?? block.Type.OrDefault());
                    return node;
                case BirConditional conditional:
                    Expression(conditional.Cond, replacement => conditional.Cond = replacement);
                    var result = expected ?? conditional.Type.OrDefault();
                    Expression(conditional.Then, replacement => conditional.Then = replacement, result);
                    Expression(conditional.Else, replacement => conditional.Else = replacement, result);
                    return node;
            }
            if (node is BirCallInstance or BirPropertyGet or BirPropertySet)
                node = BindSourceCall(node, locals, index, refs, expected);
            if (node is BirClosureDefinition or BirInlineLambda or BirNewClosure or BirNewDelegate or BirNewSuspendLambda or BirForEach or BirRepeat)
                return node;
            // Only actual executable children participate. Saved declarations,
            // selected foreign headers and opaque payloads are not source uses.
            node.VisitOwnChildSlots((_, role, slot) => {
                if (!NullableRepresentationViews.Frozen(role)) slot.Replace(Visit(slot.Value, locals));
            });
            return node;
        }
    }

    static BirGraphValue BindSourceCall(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> locals,
        Index index, ReferenceMetadataIndex refs, TypeNode expected)
    {
        var (method, receiver, owner, signature, arguments, methodArguments, name, kind, prop) = node switch {
            BirCallInstance call => (call.Method.OrDefault(), call.Recv.OrDefault(), call.OwnerType.OrDefault(),
                call.Sig.OrDefault() ?? call.ArgTypes.OrDefault(), call.Args.OrDefault(), call.TypeArgs.OrEmpty(),
                call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Prop.OrDefault()),
            BirPropertyGet get => (get.Name.OrDefault(), get.Recv.OrDefault(), (TypeNode)null, get.Sig.OrDefault(),
                Array.Empty<IBirExpression>(), Array.Empty<TypeNode>(), get.PropertyName.OrDefault(), get.PropertyAccessor.OrDefault(), (string)null),
            BirPropertySet set => (set.Name.OrDefault(), set.Recv.OrDefault(), (TypeNode)null, set.Sig.OrDefault(),
                set.Value.HasValue ? new[] { set.Value.Value } : Array.Empty<IBirExpression>(), Array.Empty<TypeNode>(),
                set.PropertyName.OrDefault(), set.PropertyAccessor.OrDefault(), (string)null),
            _ => throw new InvalidOperationException("Unexpected existential receiver operation"),
        };
        if (method == null || method.StartsWith("$star$", StringComparison.Ordinal)) return node;
        var property = KotlinPropertyAccessors.TryCallIdentity(name, kind,
            node is BirCallInstance ? method : null, prop, out var propertyName, out var accessor);
        if (!property && node is BirPropertyGet or BirPropertySet) return node;
        var authoredOwner = owner as TypeNode.Fqn;
        var physicalReceiver = authoredOwner != null && (index.SemanticOwnerByPhysical.ContainsKey(authoredOwner.Name)
            || refs.IsExistentialPhysicalOwner(authoredOwner.Name)) ? authoredOwner : SourceReceiverType(receiver as BirGraphValue, locals, index) as TypeNode.Fqn;
        if (physicalReceiver == null || !index.SemanticOwnerByPhysical.ContainsKey(physicalReceiver.Name)
            && !refs.IsExistentialPhysicalOwner(physicalReceiver.Name)) return node;
        var count = signature?.Length ?? arguments?.Length ?? (node is BirPropertySet ? 1 : 0);
        if (signature?.Any(type => type == null) == true) signature = null;
        var selected = SelectSlot(index, refs, physicalReceiver, authoredOwner, property ? propertyName : method,
            accessor, count, methodArguments.Length, signature, node.DeclarationIdentityFact.OrDefault());
        if (selected == null) return node;
        BirCallInstance replacement;
        switch (node)
        {
            case BirCallInstance call: replacement = call; break;
            case BirPropertyGet get:
                replacement = new BirCallInstance { Recv = get.Recv, Args = Array.Empty<IBirExpression>(),
                    Ret = get.Ret, Sty = get.Sty, Super = get.Super,
                    ResolvedMemberParams = get.ResolvedMemberParams, ResolvedMemberReturn = get.ResolvedMemberReturn,
                    _foreignDeclarationSignature = get._foreignDeclarationSignature };
                get.Recv = default;
                break;
            case BirPropertySet set:
                replacement = new BirCallInstance { Recv = set.Recv, Args = arguments,
                    Sty = set.Sty, Super = set.Super, ResolvedMemberParams = set.ResolvedMemberParams,
                    ResolvedMemberReturn = set.ResolvedMemberReturn, _foreignDeclarationSignature = set._foreignDeclarationSignature };
                set.Recv = default; set.Value = default;
                break;
            default: throw new InvalidOperationException("Unexpected existential receiver operation");
        }
        replacement.DeclarationId = selected.Identity == null ? default : OptionalField<string>.Of(selected.Identity);
        replacement.MemberRef = default; replacement.MemberVisibility = default; replacement.MemberOwnerTypeParams = default;
        replacement.OwnerType = BirGraphValue.CopyType(selected.Owner);
        replacement.Method = selected.Method;
        if (property) { KotlinPropertyAccessors.PreserveCallIdentity(replacement, propertyName, accessor); replacement.Prop = default; }
        if (selected.Parameters != null) replacement.Sig = selected.Parameters.Select(BirGraphValue.CopyType).ToArray();
        if (selected.Signature != null) replacement.CalleeParams = selected.Signature.Select(BirGraphValue.CopyType).ToArray();
        replacement.Virtual = true;
        var physical = FBoundStarProjectionErasure.SubstituteMethodTypeArguments(selected.Result, methodArguments);
        if (replacement.ResultTypeStamp == null && expected != null) replacement.Sty = BirGraphValue.CopyType(expected);
        return (BirGraphValue)FBoundStarProjectionErasure.AlignCallResultSource(replacement, physical, protectExactCast: false);
    }

    static TypeNode SourceReceiverType(BirGraphValue receiver, IReadOnlyDictionary<string, TypeNode> locals, Index index)
    {
        if (receiver is BirCast cast) return cast.Type.OrDefault();
        if (receiver is BirLocal { Name.HasValue: true } local && locals.TryGetValue(local.Name.Value, out var type)) return type;
        if (receiver is BirFieldGet { OwnerType.Value: TypeNode.Fqn owner, Name.HasValue: true } field
            && index.Fields.TryGetValue((owner.Name, field.Name.Value), out var declaration))
            return SupertypeGraph.SubstOwnerTvs(declaration, owner.Args ?? Array.Empty<TypeNode>());
        TypeNode result = null, own = null, stamp = null;
        receiver?.VisitOwnTypeFields((slot, leaf) => {
            if (slot == "sty") stamp = leaf;
            else if (slot == "ret") result = leaf;
            else if (slot == "type") own = leaf;
        });
        return stamp ?? result ?? own;
    }
}
