using DotKt.Bir;
using DotKt.Bir.Lowering;

// A read of the facts a resolver actually authored, not a second resolution
// policy. In particular, a member reference does not supply a missing stamp.
internal sealed record ForeignDeclarationFacts(string Kind, string Member, string Owner,
    BirMemberReference MemberRef, bool HasParameterStamp, TypeNode DeclaredReturn)
{
    internal static ForeignDeclarationFacts Read(BirGraphValue node) => node switch {
        BirCallStatic value => Operation(value, value.Method.OrDefault(), value.OwnerType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirCallInstance value => Operation(value, value.Method.OrDefault(), value.OwnerType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirFieldGet value => Operation(value, value.Name.OrDefault(), value.Type.OrDefault() ?? value.OwnerType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirFieldSetExpression value => Operation(value, value.Name.OrDefault(), value.Type.OrDefault() ?? value.OwnerType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirNewDelegate value => Operation(value, value.Method.OrDefault(), null,
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirNewBoundDelegate value => Operation(value, value.Method.OrDefault(), value.OwnerType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirNew value => Operation(value, null, value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirSetField value => Operation(value, value.Name.OrDefault(), value.Type.OrDefault() ?? value.OwnerType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirNewClr value => Operation(value, null, value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirConstrainedCall value => Operation(value, value.Method.OrDefault(), null,
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirGenericStatic value => Operation(value, value.Method.OrDefault(), value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirGenericInstance value => Operation(value, value.Method.OrDefault(), value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirEventAdd value => Operation(value, null, value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirEventRemove value => Operation(value, null, value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirNativeInstance value => Operation(value, value.Method.OrDefault(), value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirNativeStatic value => Operation(value, value.Method.OrDefault(), value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirPropertyGet value => Operation(value, value.Name.OrDefault(), value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirPropertySet value => Operation(value, value.Name.OrDefault(), value.Type.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirBoundClrDelegate value => Operation(value, value.Method.OrDefault(), value.Type.OrDefault() ?? value.ClrType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        BirClrStaticDelegate value => Operation(value, value.Method.OrDefault(), value.Type.OrDefault() ?? value.ClrType.OrDefault(),
            value.MemberRef.OrDefault(), value.ResolvedMemberParams.HasValue, value.ResolvedMemberReturn.OrDefault()),
        // These are parameter-check inputs, not executable stamp sites. Their
        // old field-selection vocabulary has neither a diagnostic owner nor name.
        BirAttribute value => Operation(value, null, null, value.MemberRef.OrDefault(), false, null),
        BirMethodImpl value => Operation(value, null, null, value.MemberRef.OrDefault(), false, null),
        _ => null,
    };

    static ForeignDeclarationFacts Operation(BirGraphValue node, string member, TypeNode owner,
        BirMemberReference reference, bool hasParameters, TypeNode declaredReturn) => new(
        (node as BirGraphNode)?.Tag, member ?? ".ctor", (owner as TypeNode.Fqn)?.Name ?? "<unknown>",
        reference, hasParameters, declaredReturn);

    internal static void ClearDeclaredReturn(BirGraphValue node)
    {
        switch (node)
        {
            case BirCallStatic value: value.ResolvedMemberReturn = default; break;
            case BirCallInstance value: value.ResolvedMemberReturn = default; break;
            case BirFieldGet value: value.ResolvedMemberReturn = default; break;
            case BirFieldSetExpression value: value.ResolvedMemberReturn = default; break;
            case BirNewDelegate value: value.ResolvedMemberReturn = default; break;
            case BirNewBoundDelegate value: value.ResolvedMemberReturn = default; break;
            case BirNew value: value.ResolvedMemberReturn = default; break;
            case BirSetField value: value.ResolvedMemberReturn = default; break;
            case BirNewClr value: value.ResolvedMemberReturn = default; break;
            case BirConstrainedCall value: value.ResolvedMemberReturn = default; break;
            case BirGenericStatic value: value.ResolvedMemberReturn = default; break;
            case BirGenericInstance value: value.ResolvedMemberReturn = default; break;
            case BirEventAdd value: value.ResolvedMemberReturn = default; break;
            case BirEventRemove value: value.ResolvedMemberReturn = default; break;
            case BirNativeInstance value: value.ResolvedMemberReturn = default; break;
            case BirNativeStatic value: value.ResolvedMemberReturn = default; break;
            case BirPropertyGet value: value.ResolvedMemberReturn = default; break;
            case BirPropertySet value: value.ResolvedMemberReturn = default; break;
            case BirBoundClrDelegate value: value.ResolvedMemberReturn = default; break;
            case BirClrStaticDelegate value: value.ResolvedMemberReturn = default; break;
        }
    }
}
