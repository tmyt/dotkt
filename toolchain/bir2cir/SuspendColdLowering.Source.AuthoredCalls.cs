using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        BirCallStatic BindAuthoredCall(BirCallStatic call, int sourceArity, TypeNode[] sourceParameters)
        {
            var selected = SelectAuthoredCall((call.Owner.OrDefault() as TypeNode.Fqn)?.Name,
                call.Method.OrDefault(), true, call.TypeArgs, sourceArity, sourceParameters);
            call.DeclarationId = selected.Identity; call.TypeArgs = selected.Arguments; call.Sig = selected.Parameters;
            return call;
        }

        BirCallInstance BindAuthoredCall(BirCallInstance call, int sourceArity, TypeNode[] sourceParameters)
        {
            var selected = SelectAuthoredCall((call.OwnerType.OrDefault() as TypeNode.Fqn)?.Name,
                call.Method.OrDefault(), false, call.TypeArgs, sourceArity, sourceParameters);
            call.DeclarationId = selected.Identity; call.TypeArgs = selected.Arguments; call.Sig = selected.Parameters;
            return call;
        }

        (string Identity, TypeNode[] Arguments, TypeNode[] Parameters) SelectAuthoredCall(string owner,
            string name, bool isStatic, OptionalField<TypeNode[]> supplied, int sourceArity, TypeNode[] sourceParameters)
        {
            NullableRepresentationFrame frame;
            TypeNode[] parameters;
            string identity;
            if (_localTypeFqns.Contains(owner))
            {
                var matches = _module.HelperDeclarations.Where(entry => entry.Owner == owner
                    && entry.Method.Static.OrDefault() == isStatic
                    && (entry.Method.DeclarationSourceName.OrDefault() ?? entry.Method.Name.OrDefault()) == name
                    && (entry.Method.NullableRepresentationFrame.OrDefault()?.SourceArity
                        ?? entry.Method.TypeParams.OrEmpty().Length) == sourceArity
                    && entry.Signature.Parameters is { } declared && declared.Count == sourceParameters.Length
                    && declared.Select((parameter, index) => ReferenceMetadataIndex.SourceDeclarationDescribesCall(
                        parameter, sourceParameters[index])).All(match => match)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException($"Compiler-authored helper '{owner}.{name}' has {matches.Length} exact local source declarations");
                var method = matches[0].Method;
                identity = method.DeclarationId.OrDefault();
                frame = method.NullableRepresentationFrame.OrDefault();
                parameters = method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray();
            }
            else
            {
                var helper = _refs.AuthoredKotlinHelper(owner, name, sourceArity, sourceParameters, isStatic);
                identity = helper.DeclarationId; frame = helper.NullableFrame; parameters = helper.ParamTypeNodes;
            }
            var sourceArguments = supplied.HasValue ? supplied.Value : Enumerable.Repeat(AnyTn, sourceArity).ToArray();
            var arguments = frame == null ? sourceArguments : frame.Close(sourceArguments, type => type,
                type => NullableGenericErasure.EraseArgument(new TypeNode.Nullable(type), _isValueFqn),
                type => type, type => NullableGenericErasure.EraseArgument(new TypeNode.Nullable(type), _isValueFqn));
            return (identity, arguments.Select(BirGraphValue.CopyType).ToArray(),
                parameters.Select(BirTypeLowering.CanonicalPhysicalSlotType).Select(BirGraphValue.CopyType).ToArray());
        }

        IBirExpression CompanionFactoryCall(string semanticOwner, string method, TypeNode[] typeArgs,
            TypeNode[] signature, IBirExpression[] arguments, TypeNode result)
        {
            if (_refs.TrySingletonCompanionCarrier(semanticOwner, out var carrier))
            {
                var type = new TypeNode.Fqn(carrier);
                return BindAuthoredCall(new BirCallInstance { OwnerType = type, Method = method,
                    TypeArgs = typeArgs, Sig = signature, Args = arguments, Ret = result, Virtual = false,
                    Recv = new BirStaticFieldGet { OwnerType = type, Name = "$INSTANCE", Sty = type } },
                    typeArgs.Length, signature);
            }
            return BindAuthoredCall(new BirCallStatic { Owner = new TypeNode.Fqn(semanticOwner),
                Method = method, TypeArgs = typeArgs, Sig = signature, Args = arguments, Ret = result },
                typeArgs.Length, signature);
        }
    }
}
