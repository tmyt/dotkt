using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    // An inner value's carrier cannot inherit a CLR class. Cross to a proven
    // base while the source frame is available, without recovering an enclosing
    // generic argument or replacing registered declarations inside the operand.
    internal static void AlignCapturedInnerBaseValuesSource(BirFileDeclaration root, CarrierDemandPlan demand,
        ReferenceMetadataIndex refs)
    {
        var context = CarrierTypeContext.ForSource(demand.Preparation, refs);
        Walk(root, new Dictionary<string, TypeNode>(StringComparer.Ordinal), null);

        void Walk(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> variables, TypeNode returnType)
        {
            if (node is IBirExecutablePayload) return;
            if (AlignmentCallable(node))
            {
                var parameters = new Dictionary<string, TypeNode>(variables, StringComparer.Ordinal);
                node.VisitSourceParameters((name, type) => parameters[name] = type);
                variables = parameters;
                // A callable frame reads ret only. An absent ret on a
                // constructor, inline or suspend donor resets the return frame.
                returnType = null;
                node.VisitOwnTypeFields((field, type) => { if (field == "ret") returnType = type; });
            }

            IBirExpression Align(IBirExpression value, TypeNode targetType)
            {
                if (value == null || StripSourceNullability(targetType) is not TypeNode.Fqn target
                    || (target.Args?.Length ?? 0) != 0
                    || StripSourceNullability(BirGraphStaticType.Surface(value, variables)) is not TypeNode.Fqn source
                    || !IsCapturedInner(source, demand.Owners, refs)
                    || ProjectConstructedArguments(source, target.Name, context) is not { Count: 0 }) return value;
                return new BirCast { Type = BirGraphValue.CopyType(targetType), Sty = BirGraphValue.CopyType(targetType),
                    E = OptionalField<IBirExpression>.Of(value), _exactBridgeCast = true };
            }

            var (arguments, signature) = node switch {
                BirCallStatic value => (value.Args.OrDefault(), value.Sig.OrDefault() ?? value.ArgTypes.OrDefault() ?? value.MemberSignature.OrDefault()),
                BirCallInstance value => (value.Args.OrDefault(), value.Sig.OrDefault() ?? value.ArgTypes.OrDefault() ?? value.MemberSignature.OrDefault()),
                BirNew value => (value.Args.OrDefault(), value.ArgTypes.OrDefault() ?? value.MemberSignature.OrDefault()),
                BirNewClr value => (value.Args.OrDefault(), value.ArgTypes.OrDefault() ?? value.MemberSignature.OrDefault()),
                _ => ((IBirExpression[])null, (TypeNode[])null),
            };
            if (arguments != null && signature != null && arguments.Length == signature.Length)
                for (var index = 0; index < arguments.Length; index++) arguments[index] = Align(arguments[index], signature[index]);
            switch (node)
            {
                case BirCallInstance { Recv.HasValue: true } call:
                    call.Recv = OptionalField<IBirExpression>.Of(Align(call.Recv.Value, call.OwnerType.OrDefault()));
                    break;
                case BirReturn { Value.HasValue: true } returned:
                    returned.Value = OptionalField<IBirExpression>.Of(Align(returned.Value.Value, returnType));
                    break;
                case BirReturnExpression { Value.HasValue: true } returned:
                    returned.Value = OptionalField<IBirExpression>.Of(Align(returned.Value.Value, returnType));
                    break;
                case BirVariable { Init.HasValue: true } variable:
                    variable.Init = OptionalField<IBirExpression>.Of(Align(variable.Init.Value, variable.Type.OrDefault()));
                    break;
                case BirSetLocal { Value.HasValue: true, Name.HasValue: true } assignment
                    when variables.TryGetValue(assignment.Name.Value, out var localType):
                    assignment.Value = OptionalField<IBirExpression>.Of(Align(assignment.Value.Value, localType));
                    break;
            }
            node.VisitSourceChildren(child => Walk(child.Value, variables, returnType), region => {
                var inner = new Dictionary<string, TypeNode>(variables, StringComparer.Ordinal);
                foreach (var statement in region.Statements)
                {
                    Walk((BirGraphValue)statement, inner, returnType);
                    if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                        inner[variable.Name.Value] = variable.Type.Value;
                }
                // Each statement region has its own child scope. In particular a
                // valueBlock's statements do not export locals to this sibling.
                if (region.Result is { } result) Walk(result.Value, variables, returnType);
            });
        }
    }

    static bool AlignmentCallable(BirGraphValue node) => node switch {
        BirFunctionDeclaration value => value.Params.HasValue && value.Body.HasValue,
        BirConstructor value => value.Params.HasValue && value.Body.HasValue,
        BirClosureDefinition value => value.Params.HasValue && value.Body.HasValue,
        BirNewSuspendLambda value => value.Params.HasValue && value.Body.HasValue,
        BirInlineLambda value => value.Params.HasValue && value.Body.HasValue, _ => false,
    };
}
