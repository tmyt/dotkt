// Shared scalar suspend policy and the declaration-binding context-read leaf.
// Owning named and lambda execution is implemented by SourceFunGen and its
// compilation-scoped SuspendModuleContext; no DOM body engine lives here.
using DotKt.Bir;

static partial class SuspendColdLowering
{
    static readonly TypeNode AnyTn = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode IntTn = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode BoolTn = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode VoidTn = new TypeNode.Fqn("void");
    static readonly TypeNode UnitTn = new TypeNode.Fqn("kotlin.Unit");
    static readonly TypeNode ContAnyTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
    static readonly TypeNode NullableContAnyTn = new TypeNode.Nullable(new TypeNode.Fqn(
        "kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) }));
    static readonly TypeNode ContUnitTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Unit") });

    static bool IsUnitTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "void" or "kotlin.Unit" };
    static bool IsAnyTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "kotlin.Any" };
    static bool IsNothingTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "kotlin.Nothing" };

    const string ContinuationImplFqn = "kotlin.coroutines.clr.internal.ContinuationImpl";
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";
    // The CLR Task ABI is a physical lowering decision owned here. These identities are resolved and validated
    // against the exact compile-reference universe; no kotlin.clr alias declaration participates in the bridge.
    const string TaskBclFqn = "System.Threading.Tasks.Task";
    const string TaskCompletionSourceBclFqn = "System.Threading.Tasks.TaskCompletionSource";
    // One module-private sink is synthesized into each app assembly that needs a Task bridge or suspend-main drain.
    // It implements the Kotlin Continuation protocol but completes a CLR TaskCompletionSource<T>.
    const string RootContinuationFqn = "kotlin.coroutines.clr.internal.RootContinuation";
    // A @RestrictsSuspension-scope (e.g. SequenceScope) suspend lambda's SM base. Same 2-arg (arity, completion)
    // ctor + create() protocol as SuspendLambda; RestrictedContinuationImpl pins EmptyCoroutineContext.
    const string RestrictedSuspendLambdaFqn = "kotlin.coroutines.clr.internal.RestrictedSuspendLambda";
    const string BaseContinuationImplFqn = "kotlin.coroutines.clr.internal.BaseContinuationImpl";
    // BaseContinuationImpl.create returns Continuation<Unit> (ContinuationImpl.kt:82/87); a CLR virtual override needs
    // an EXACT return-type match (no covariance), so the SM's create() must return Continuation<Unit>, NOT <Any>.
    const string IntrinsicsKtFqn = "kotlin.coroutines.intrinsics.IntrinsicsKt";
    // Top-level `throwOnFailure(result)` helper (ContinuationImpl.kt, package kotlin.coroutines.clr.internal).
    const string ThrowOnFailureOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";

    static readonly Dictionary<string, string> SuspendCoroutineIntrinsicOwners = new(StringComparer.Ordinal)
    {
        ["suspendCoroutine"] = "kotlin.coroutines.ContinuationKt",
        ["suspendCoroutineUninterceptedOrReturn"] = "kotlin.coroutines.intrinsics.IntrinsicsKt",
    };

    const string StartSuspendOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";

    // Null-check blocks and smart-cast locals may retain the declared nullable surface. Nullability does not
    // change the function-value ABI; receiver evaluation still performs the source null check before invocation.
    static bool IsSuspendFunctionType(TypeNode type) => type switch
    {
        TypeNode.Fn { Suspend: true } => true,
        TypeNode.Nullable nullable => IsSuspendFunctionType(nullable.Of),
        TypeNode.Oblivious oblivious => IsSuspendFunctionType(oblivious.Of),
        _ => false,
    };
    internal static bool IsCoroutineContextRead(string kind, string preservedName, string preservedKind,
        string method, string propertyKind, int? argumentCount) => kind == "callStatic"
        && (preservedName == "coroutineContext" && preservedKind == "get"
            || method == "coroutineContext" && propertyKind == "get")
        && (argumentCount == null || argumentCount == 0);

    const string CoroutineContextFqn = "kotlin.coroutines.CoroutineContext";
    const string ContinuationFqn = "kotlin.coroutines.Continuation";

}
