using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExternalGenericConstraintValidation
{
    public sealed partial class Prepared
    {
        // Continue the exact Prepared authority used before physical lowering.
        // Local declaration facts and referenced metadata caches are not rebuilt.
        public void ApplyResolvedMembers(BirGraphValue root) => WalkResolvedSource(root, null,
            Array.Empty<ParameterFacts>(), Array.Empty<ParameterFacts>());

        void WalkResolvedSource(BirGraphValue node, string incomingField, ParameterFacts[] typeParameters,
            ParameterFacts[] methodParameters)
        {
            if (node is BirMemberReference or IBirExecutablePayload) return;
            var nextTypes = typeParameters;
            var nextMethods = methodParameters;
            if (node is IBirTypeDeclaration owner && IsTypeKind(owner.Kind.OrDefault()))
                nextTypes = ReadSourceParameterFacts(owner.CapturedTypeParams.OrEmpty().Concat(owner.TypeParams.OrEmpty()));
            if (incomingField == "methods")
                nextMethods = ReadSourceParameterFacts(node is BirFunctionDeclaration method ? method.TypeParams.OrEmpty() : []);
            else if (incomingField == "ctors") nextMethods = Array.Empty<ParameterFacts>();

            if (ResolvedUseOf(node) is { } use)
            {
                if (!use.Arguments.HasValue || use.Arguments.Value.Length != use.Declarations.Length)
                    throw new InvalidDataException(
                        "bir2cir: resolved generic member constraint carrier does not match its type arguments");
                var declaringType = use.Member?.DeclaringType.OrDefault();
                var member = use.Member?.Name.OrDefault() ?? use.Method ?? "<unknown>";
                var name = declaringType == null ? member : Display(declaringType) + "." + member;
                for (var index = 0; index < use.Arguments.Value.Length; index++)
                {
                    var argument = use.Arguments.Value[index];
                    ValidateType(argument, nextTypes, nextMethods, _refs, _isValueFqn, _localEnums,
                        _localTypes, _declarationCache);
                    ValidateArgument(name, index, argument, SourceConstraintDeclaration(use.Declarations[index]),
                        nextTypes, nextMethods, _refs, _isValueFqn, _localEnums, _localTypes);
                }
                use.Clear();
            }
            node.VisitOwnChildSlots((field, _, slot) => {
                if (!ForeignDeclarationTypeKeys.Contains(field)
                    && (field != "argTypes" || node is BirNew))
                    WalkResolvedSource(slot.Value, field, nextTypes, nextMethods);
            });
        }

        ParameterFacts[] ReadSourceParameterFacts(IEnumerable<BirTypeParameter> parameters) =>
            ReadParameterFacts(parameters.Select(SourceConstraintDeclaration).ToArray(),
                _refs, _isValueFqn, _localEnums, _localTypes);
    }

    static ConstraintFacts SourceConstraintDeclaration(BirTypeParameter declaration) =>
        new(declaration.SpecialConstraints.OrEmpty(), declaration.Constraints.OrEmpty());

    sealed record ResolvedUse(BirTypeParameter[] Declarations, OptionalField<TypeNode[]> Arguments,
        BirMemberReference Member, string Method, Action Clear);

    // The selected MethodDef owns the carrier. None is reconstructed from the
    // receiver, a matching name, a method body or the physical member signature.
    static ResolvedUse ResolvedUseOf(BirGraphValue node) => node switch {
        BirCallStatic value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirCallInstance value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirNewDelegate value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirNewBoundDelegate value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirBoundClrDelegate value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirClrStaticDelegate value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirConstrainedCall value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirGenericStatic value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirGenericInstance value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirNativeStatic value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        BirNativeInstance value when value._resolvedMethodTypeParams.HasValue => new(value._resolvedMethodTypeParams.Value,
            value.TypeArgs, value.MemberRef.OrDefault(), value.Method.OrDefault(), () => value._resolvedMethodTypeParams = default),
        _ => null,
    };
}
