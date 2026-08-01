// WHICH NODES CARRY A .NET DECLARATION — stated ONCE, because three passes ask it and a fourth will.
//
// Once NetInteropBinding (or MemberCallSubstitution, for a constructor) has bound a node to a .NET member, what the
// node carries in `memberSig`/`argTypes`/`ret` is that MEMBER's declaration, not the caller's Kotlin view of it. Every
// pass in the erasure family turns on that distinction: the declaration axis must not restate a foreign declaration
// in Kotlin's vocabulary, the use axis trusts a foreign descriptor further than a Kotlin one, and the crossing
// refusal reads a foreign declaration to decide whether Kotlin can inhabit it at all.
//
// The three used to spell it themselves, with comments claiming they agreed. They did not: two included the property
// accessors and one did not, and nothing said which was right. The split below keeps that difference EXPLICIT — a
// CALL has an argument vector, a property/field ACCESS has only a type — so a caller picks the set its question is
// about rather than the set it happened to copy.
static class ClrBoundNode
{
    // A CALL bound to a .NET member: it carries an argument descriptor (`memberSig`, or `argTypes` before
    // ClrMemberResolution stamps one) as well as a result.
    public static bool IsCall(string k) =>
        k is "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance" or "newClr";

    // A .NET property or field ACCESS. It names a member and a type, and has no argument vector at all — which is
    // why the argument axis does not list these and the crossing refusal does.
    public static bool IsMemberAccess(string k) => k is "clrPropGet" or "clrPropSet";

    // Any node whose types are a .NET declaration.
    public static bool IsAny(string k) => IsCall(k) || IsMemberAccess(k);
}
