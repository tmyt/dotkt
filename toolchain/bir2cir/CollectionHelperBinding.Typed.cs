using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionHelperBinding
{
    internal static void Apply(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, string> aliases, SourceCollectionInputs captured)
    {
        bool IsInterface(string owner) => captured.Kinds.TryGetValue(owner, out var kind) ? kind == "interface"
            : references != null && references.TryResolveClrOwner(owner, out _, out var referenceKind) && referenceKind == "interface";
        BirGraphValue Walk(BirGraphValue node, CollectionSourceContext context)
        {
            if (node == null || node is IBirExecutablePayload or BirAttribute or BirOverride) return node;
            context = context.Extend(node);
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value, context)));
            if (node is not BirCallInstance operation || operation.ClrOwnerResolved.OrDefault()
                || operation.OwnerType.OrDefault() is not TypeNode.Fqn owner || !operation.Args.HasValue) return node;
            if (!operation.Super.OrDefault() && (!aliases.ContainsKey(owner.Name) || !IsInterface(owner.Name))
                && (KotlinCollectionSlotSynthesis.ErasedQueryOverride(operation)
                    ?? operation.Overrides.OrEmpty().FirstOrDefault(marker =>
                        !KotlinCollectionSlotSynthesis.IsMapContractOwner((marker.Owner.OrDefault() as TypeNode.Fqn)?.Name)
                        && KotlinCollectionSlotSynthesis.OwnsSupplementalOverride(marker))) is { } selected)
            {
                owner = (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(selected.Owner.Value,
                    captured.Applications.DeclarationArguments(owner) ?? Array.Empty<TypeNode>());
                // Keep the original graph untouched if an owner adjustment is
                // selected without a helper; the returned call owns this copy.
                operation = operation with { OwnerType = owner, Method = selected.Member, DeclarationId = selected.DeclarationId };
            }
            var arguments = operation.Args.Value;
            BirCallStatic call;
            if (!aliases.ContainsKey(owner.Name))
            {
                if (operation.Method.OrDefault() != "iterator" || arguments.Length != 0) return operation;
                var declaresIterator = MemberCallSubstitution.HasDeclaredIterator(owner, captured.DeclaredIterators,
                    captured.Definitions, references, aliases, sourceFrames: true);
                var plan = MemberCallSubstitution.SelectMissingCollectionIterator(operation.Method.OrDefault(), arguments.Length,
                    declaresIterator, KotlinPropertyAccessors.HasIdentity(operation.PropertyName.OrDefault(), operation.PropertyAccessor.OrDefault())
                        || operation.Prop.OrDefault() is "get" or "set", operation.TypeArgs.OrEmpty().Length,
                    operation.Sty.OrDefault() ?? operation.Ret.OrDefault());
                if (plan == null) return operation;
                call = Render(plan, operation, references);
            }
            else if (owner.Name is "kotlin.collections.Map" or "kotlin.collections.MutableMap")
            {
                var helper = MemberCallSubstitution.MapDefaultHelper(operation.Method.OrDefault(),
                    operation.PropertyAccessor.OrDefault() ?? operation.Prop.OrDefault(), arguments.Length,
                    owner.Name == "kotlin.collections.MutableMap");
                if (helper == null || owner.Args is not { Length: 2 }) return operation;
                var (key, value) = MemberCallSubstitution.MapTypeArguments(owner, context.Receiver(operation.Recv.OrDefault()),
                    context.Constraints, references);
                var operands = new List<IBirExpression> { operation.Recv.OrDefault()
                    ?? throw new InvalidOperationException("Map member has no receiver") };
                operands.AddRange(arguments);
                operands.Add(MemberCallSubstitution.StaticMapPreferenceValue(new TypeNode.Fqn(owner.Name, new[] { key, value }), references,
                    MemberCallSubstitution.CollectionOperationPhase.Source));
                if (helper == "clrMapPutAll")
                {
                    var source = MemberCallSubstitution.CollectionMapBound(context.Receiver(arguments[0]), context.Constraints);
                    operands.Add(source == null ? UnknownPreference()
                        : MemberCallSubstitution.StaticMapPreferenceValue(source, references, MemberCallSubstitution.CollectionOperationPhase.Source));
                }
                call = new BirCallStatic {
                    Owner = new TypeNode.Fqn(HelperOwner), Method = helper, Sig = MemberCallSubstitution.MapHelperTypes(helper),
                    TypeArgs = new[] { key, value }, Args = operands.ToArray(),
                };
            }
            else
            {
                if (!IsInterface(owner.Name) || !(owner.Name.StartsWith("kotlin.collections.", StringComparison.Ordinal)
                    || owner.Name == "kotlin.sequences.Sequence")) return operation;
                var receiver = context.Receiver(operation.Recv.OrDefault());
                var element = MemberCallSubstitution.CollectionElement(owner, receiver, context.Constraints, references);
                var projected = MemberCallSubstitution.ProjectedCollectionReceiver(owner, receiver, false, context.Constraints, references);
                var preferred = operation.Recv.OrDefault() is BirLocal { Name.HasValue: true } local
                    && captured.ClosedViews.TryGetValue(local.Name.Value, out var exact) && exact.Name == owner.Name ? exact : null;
                var plan = MemberCallSubstitution.SelectCollectionMutation(operation.Method.OrDefault(), arguments.Length, owner,
                    element, projected, preferred, operation.DynRet.OrDefault() ?? operation.Ret.OrDefault())
                    ?? MemberCallSubstitution.SelectCollectionDefault(operation.Method.OrDefault(), operation.Prop.OrDefault(), arguments.Length,
                        operation.Recv.HasValue, owner, element, projected, preferred);
                if (plan == null) return operation;
                call = Render(plan, operation, references);
            }
            call.TypeArgs = call.TypeArgs.Value.Select(SourceHelperBinder.SourceArgument).ToArray();
            call.DeclarationId = captured.Helpers.Select(((TypeNode.Fqn)call.Owner.Value).Name,
                call.Method.Value, call.TypeArgs.Value.Length, call.Sig.Value);
            // This binding preserves the source result; representation demand
            // and physical frame materialization have not run yet.
            var result = OptionalField<TypeNode>.Of(operation.Sty.OrDefault() ?? operation.Ret.OrDefault());
            call.Ret = result;
            call.Sty = result;
            return call;
        }
        foreach (var root in roots) Walk(root, new CollectionSourceContext());
    }

    static BirCallStatic Render(MemberCallSubstitution.CollectionOperationPlan plan, BirCallInstance operation,
        ReferenceMetadataIndex references)
    {
        var operands = new List<IBirExpression>();
        if (operation.Recv.HasValue) operands.Add(operation.Recv.Value);
        if (plan.NativePreference != null)
            operands.Add(MemberCallSubstitution.CollectionPreferenceType(plan.PreferenceView, references, plan.NativePreference,
                MemberCallSubstitution.CollectionOperationPhase.Source) is { } preference
                ? new BirClassReference { Type = preference } : UnknownPreference());
        if (plan.Family is int family)
            operands.Add(new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(family) });
        operands.AddRange(operation.Args.Value);
        if (plan.DefaultIndex)
            operands.Add(new BirConstant { Type = new TypeNode.Fqn("int"), Value = new BirLiteral.IntegerLiteral(0) });
        return new BirCallStatic {
            Owner = new TypeNode.Fqn(plan.Owner), Method = plan.Method, Sig = MemberCallSubstitution.CollectionOperationTypes(plan),
            TypeArgs = plan.Element == null ? Array.Empty<TypeNode>() : new[] { plan.Element }, Args = operands.ToArray(),
            Ret = plan.Result == null ? default : OptionalField<TypeNode>.Of(plan.Result),
        };
    }

    static BirConstant UnknownPreference() => new() {
        Type = new TypeNode.Nullable(new TypeNode.Fqn("object")), Value = new BirLiteral.NullLiteral(),
    };
}
