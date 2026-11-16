using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// THE ERASURE INVARIANT (#86): CARRIER-ARGUMENT ERASURE. For any declaration slot `s`,
// `physical(s) = Erase(declaredKotlinType(s))`, and `Erase` is POSITIONAL — one rule, read off where the type sits:
//
//   * A DIRECT slot keeps the CLR-native form. A concrete `V?` is `System.Nullable<V>` at a method return, a method
//     or constructor parameter, a field, a property, a body local and a `ref` referent alike. Only the open
//     `Nullable(Tv)` erases here, because `Nullable<T>` is not even expressible for an unconstrained `T` and a bare
//     `!T` slot collapses a null to `default(T)`.
//   * An ARGUMENT to a CLR-REIFIED CONSTRUCTION — a generic type's type argument, a generic method's type argument,
//     an array element, a delegate's return — is `System.Object` whenever the Kotlin type there is `X?` for a
//     possibly-value `X`: an open `Tv` (some instantiation is a struct) or a value `Fqn` (this one is).
//     So `List<Int?>` is `IReadOnlyList<object>`, `Box<Int?>` is `Box<object>`, `f<Int?>()` instantiates at `object`,
//     `Comparable<Int?>` is `Comparable<object>`, `Array<Int?>` is `object[]` and `(Int) -> Int?` is
//     `Func<int32, object>`. References keep their normal CLR representation: `List<String?>` is
//     `IReadOnlyList<string>` and `Array<String?>` is `string[]`, the `?` riding the NRT byte.
//
// A DELEGATE PARAMETER IS THE ONE ARGUMENT POSITION THAT KEEPS THE DECLARED FORM for a CONCRETE `V?`, so
// `(Int?) -> String` is `Func<Nullable<int32>, string>` while `(T?) -> String` is `Func<object, string>` at every
// instantiation. That is not the general rule and is recorded as such in `docs/dotkt-semantics.md` §9c-bis: a
// delegate's target may be a member the AUTHOR declared (`::handle`, `expr::member`), whose slots are its Kotlin
// surface and are not this pass's to move, and ECMA-335 II.14.6 admits no `Func<object, …>` over a target declaring
// `Nullable<int32>`. Closing it needs a synthesized FORWARDER at those two reference forms — a static one for
// `::fn`, a capture class for `expr::member` — after which the parameter joins the rule with the rest.
//
// WHY THE TWO POSITIONS DIFFER. The Kotlin type system's contract for an unconstrained `T?` is a runtime null for
// EVERY `T` — more than a reified CLR generic argument expresses — so a generic position has to box; `object` is the
// only uniform CLR storage that carries a real null for both a reference and a value instantiation, and the CLR's own `Nullable<V>` boxing
// collapse makes it the spec-defined boxed form (`box` of an empty `Nullable<V>` IS a null reference; `unbox.any
// Nullable<V>` accepts null). A reified construction is INVARIANT, so an open `G<T?>` erased to `G<object>` and a
// concrete `G<Int?>` left as `G<Nullable<int32>>` could never meet — the same unrelated pair array compatibility
// forbids for elements (ECMA-335 I.8.7.1). A SCALAR slot has no such meeting to arrange: nothing is reified over it,
// so it keeps the CLR-native `Nullable<V>`, which is what a C# caller expects to see and what interop is written in.
//
// EVERY type variable qualifies, whatever its bound. `fun <T : CharSequence> f(xs: List<T?>)` has no value
// instantiation and still erases, because the rule is uniform rather than bound-consulting: one physical form per
// declaration, decided without resolving where each bound leads.
//
// A declaration BOUND ELSEWHERE is authoritative and is NOT re-erased: a `resolvedMemberParams`, and the `argTypes` a `clr*`
// node carries under another name, state what a .NET member really declares, so a genuine `List<int?>` parameter
// keeps its `Nullable<int32>` argument. Kotlin's canonical `G<object>` does not inhabit it, and that crossing is
// REFUSED by ForeignNullableGenericCrossing rather than silently adapted.
//
// The pre-erasure Kotlin TypeNode rides `[KotlinNullableGeneric]` so the Kotlin surface survives the trip; the
// user-visible consequences are recorded in `docs/dotkt-semantics.md` §9c-bis.
//
// USES of a slot are typed `Subst(Erase(declaredKotlinType(s)), typeArgs)` and NEVER `Erase(Subst(...))` — that is
// NullableTvErasureCallRealign's job, on both the read and the write axis. This pass owns only the declaration axis.
// Runs in every build so ref.dll, rt.dll, and the app's view of their signatures agree.
static partial class NullableGenericErasure
{
    // Opaque pass-local hand-off consumed by RoundtripMetadata. Method type-parameter constraints belong to their
    // MethodDef, so they cannot ride the type-level KotlinSupertypes carrier used for class parameters.
    internal const string MethodTypeParameterBoundsPre = "methodTypeParameterBoundsPre";

    // WHERE a type sits, which is the whole of the erasure rule.
    internal enum Pos
    {
        // A direct CLR slot: a concrete `V?` stays `System.Nullable<V>`.
        Slot,
        // An argument to a reified construction — a type argument, an array element, a delegate component. A
        // possibly-value `X?` is `System.Object` here.
        Argument,
        // A declaration BOUND ELSEWHERE, whose slots this pass does not get to move: a resolved .NET member's
        // `resolvedMemberParams`, and the `funcType` of a delegate over a member that is already declared. Only the
        // inexpressible open `Nullable(Tv)` is rewritten; a concrete `V?` the target really declares is never
        // restated, because moving the delegate without moving the target is what leaves the two incompatible.
        Bound,
    }

    // Frozen pre-erasure suspend function shapes, consumed by BirTypeLowering.
    internal const string SuspendFnPre = "suspendFnTypePre";
    internal const string RetSuspendFnPre = "retSuspendFnTypePre";

    static bool HasNullableTv(TypeNode t) => t switch
    {
        // ClrPointer's argument is an opaque ECMA pointee signature, not a reified Kotlin generic argument.
        // Its exact tree crosses unchanged until BirTypeLowering materializes `ptr`.
        TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn or FunctionPointerVocabulary.Pointer } => false,
        TypeNode.Nullable { Of: TypeNode.Tv } => true,
        TypeNode.Nullable n => HasNullableTv(n.Of),
        TypeNode.Oblivious o => HasNullableTv(o.Of),
        TypeNode.Projection p => HasNullableTv(p.Of),
        TypeNode.Fqn { Args: { } args } => args.Any(HasNullableTv),
        TypeNode.Array a => HasNullableTv(a.Elem),
        TypeNode.ByRef b => HasNullableTv(b.Of),
        TypeNode.Fn fn => HasNullableTv(fn.Ret) || fn.Params.Any(HasNullableTv)
                          || (fn.Recv != null && HasNullableTv(fn.Recv)),
        _ => false,
    };

    // True iff `t` carries a position the erasure rewrites and dll2klib cannot infer back. Two of them:
    //   * a `Nullable(Tv)` anywhere reachable through a CLR-representable declaration shape; and
    //   * a REIFIED ARGUMENT that is a nullable POSSIBLY-VALUE type — `List<Int?>` physically `IReadOnlyList<object>`,
    //     `Box<Int?>` physically `Box<object>`, `Array<Int?>` physically `object[]`. Without the carrier a
    //     re-consuming reader sees only the `object` argument and restores `List<Any?>`, which is a DIFFERENT Kotlin
    //     type: a consumer passing its own `List<Int?>` would then fail to type-check.
    // The HEAD is deliberately not one of them: a direct `Int?` slot keeps its `Nullable<int32>` and needs no carrier
    // to be read back.
    // A non-suspend `Fn` is a real delegate in CIR, so dll2klib can walk its Invoke signature in parallel with the
    // recorded Kotlin fn node. A suspend fn is excluded: BirTypeLowering erases the whole value to object and its
    // distinct suspend-fn carrier owns restoration, so there is no physical delegate shape for this carrier to align with.
    static bool HasRestorableNullableTv(TypeNode t, ValueTypeOracle isValue) => t switch
    {
        TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn or FunctionPointerVocabulary.Pointer } => false,
        TypeNode.Nullable { Of: TypeNode.Tv } => true,
        TypeNode.Nullable n => HasRestorableNullableTv(n.Of, isValue),
        TypeNode.Fqn { Args: { } args } => args.Any(a => ErasedArgument(a, isValue)),
        TypeNode.Array a => ErasedArgument(a.Elem, isValue),
        TypeNode.ByRef b => HasRestorableNullableTv(b.Of, isValue),
        TypeNode.Oblivious o => HasRestorableNullableTv(o.Of, isValue),
        TypeNode.Projection p => HasRestorableNullableTv(p.Of, isValue),
        // A delegate's RETURN is an argument position; its PARAMETERS keep the declared form, so they need a carrier
        // only where a slot would — for the open `Nullable(Tv)` no CLR slot expresses.
        TypeNode.Fn { Suspend: false } fn =>
            ErasedArgument(fn.Ret, isValue)
            || fn.Params.Any(p => HasRestorableNullableTv(p, isValue))
            || (fn.Recv != null && HasRestorableNullableTv(fn.Recv, isValue)),
        _ => false,   // suspend Fn / bare Fqn / Tv: nothing the erasure rewrites
    };

    // One reified ARGUMENT the erasure rewrites: either it is itself the possibly-value `X?` that becomes `object`,
    // or it contains one deeper down (`List<List<Int?>>`).
    static bool ErasedArgument(TypeNode t, ValueTypeOracle isValue)
        => IsNullableMaybeValue(t, isValue) || HasRestorableNullableTv(t, isValue);

    // The Tv of a Nullable(Tv) somewhere in a type (a nullable-generic collection element `…<T?>`), else null.
    static TypeNode.Tv ExtractNullableTv(TypeNode t) => t switch
    {
        TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn or FunctionPointerVocabulary.Pointer } => null,
        TypeNode.Nullable { Of: TypeNode.Tv tv } => tv,
        TypeNode.Nullable n => ExtractNullableTv(n.Of),
        TypeNode.Fqn { Args: { } args } => args.Select(ExtractNullableTv).FirstOrDefault(x => x != null),
        TypeNode.Array a => ExtractNullableTv(a.Elem),
        TypeNode.ByRef b => ExtractNullableTv(b.Of),
        _ => null,
    };

    // A CLR argTypes vector describes an external declaration; a Kotlin construction's
    // vector is its own substituted view and follows the ordinary slot rule.
    static Pos PositionForField(string field, string kind, Pos inherited) => field switch {
        "elem" => IsArgumentElementKind(kind) ? Pos.Argument : Pos.Slot,
        "typeArgs" => Pos.Argument,
        "resolvedMemberParams" => Pos.Bound,
        "argTypes" when ClrBoundNode.IsAny(kind) => Pos.Bound,
        _ => inherited,
    };

    // The node kinds whose `elem` is a REIFIED ARGUMENT — an array allocation's `newarr` token, a `ldelem`/`stelem`
    // token, the element a `for (x in arr)` binds, the element an inline iteration over a collection yields, and a
    // vararg pack's element. All of them must name the same type as the array or collection they operate on, and that
    // container's own argument is `object` for a possibly-value `X?`.
    //
    // A vararg pack (`newArray`) is one of them BECAUSE the call it is built for now instantiates at `object` too:
    // `f<Int?>(1, null)` canonicalizes its `typeArgs` above, so the callee's `!!0[]` parameter IS an `object[]` and a
    // pack that kept `Nullable<int32>` elements would be the one array the call cannot accept.
    //
    // A list construction's element (`newList`) is a reified
    // argument too and is absent for one reason: MemberCallSubstitution BUILDS those nodes long after this sweep,
    // from the call's own `typeArgs` — which this sweep has already canonicalized — so they arrive at `object`
    // rather than being erased into it. Adding them here would be listing a kind this pass never sees.
    internal static bool IsArgumentElementKind(string kind) => ArgumentElemKinds.Contains(kind);

    static readonly HashSet<string> ArgumentElemKinds = new(StringComparer.Ordinal)
    {
        "newArray", "newArraySized", "newArrayInit", "arrayGet", "arraySet", "forArray",
        "forEachInline", "forIn", "spreadConcat",
    };

    // `Erase` at a DIRECT SLOT — a method return, a parameter, a field, a property, a local, a `ref` referent. The
    // concrete `V?` keeps its CLR-native `System.Nullable<V>`; only the open `Nullable(Tv)`, which is inexpressible,
    // becomes `object`. Whatever the slot CONTAINS is erased at its own position, so a `List<Int?>` parameter still
    // becomes an `IReadOnlyList<object>`.
    internal static TypeNode EraseNullableTv(TypeNode t, ValueTypeOracle isValue) => Erase(t, Pos.Slot, isValue);

    // `Erase` at an ARGUMENT to a reified construction — a generic type argument, a generic method type argument, an
    // array element, a delegate component. A nullable POSSIBLY-VALUE type is `object` here, so `List<T?>`,
    // `List<Int?>` and `List<Boolean?>` all become `IReadOnlyList<object>` and meet each other, and `Array<T?>`,
    // `Array<Int?>` and `Array<Boolean?>` are all `object[]`. Anything else erases normally: `List<String?>` stays
    // `IReadOnlyList<string>` and `Array<String?>` stays `string[]` (the `?` rides the NRT byte).
    internal static TypeNode EraseArgument(TypeNode t, ValueTypeOracle isValue) => Erase(t, Pos.Argument, isValue);

    // A declaration bound elsewhere. Only the open `Nullable(Tv)` — which no CLR slot can hold for an unconstrained
    // type variable — is rewritten; a concrete `Nullable<V>` the target really declares stands.
    internal static TypeNode EraseBound(TypeNode t, ValueTypeOracle isValue) => Erase(t, Pos.Bound, isValue);

    // THE ONE RULE. `Nullable(Tv)` is `object` wherever it sits, because no CLR slot expresses it; a concrete
    // possibly-value `V?` is `object` in an ARGUMENT position and `Nullable<V>` in a slot. Everything else recurses,
    // with each child visited at ITS position: an Fqn's arguments, an array's element and a delegate's
    // parameters/return/receiver are arguments; a `ref` referent and a nullable's inner are slots.
    internal static TypeNode Erase(TypeNode t, Pos pos, ValueTypeOracle isValue)
    {
        // `ClrPointer<T>` is a signature carrier, not a CLR generic instantiation. In particular,
        // `ClrPointer<Int?>` means `Nullable<int>*`; treating its argument like `List<Int?>` would rewrite it to
        // `object*` before the representation boundary. Keep the complete pointee signature opaque here.
        if (t is TypeNode.Fqn { Name: BirTypeLowering.PointerIntrinsicFqn or FunctionPointerVocabulary.Pointer }) return t;
        if (t is TypeNode.Nullable { Of: TypeNode.Tv }) return new TypeNode.Fqn("object");
        if (pos == Pos.Argument && IsNullableMaybeValue(t, isValue)) return new TypeNode.Fqn("object");
        // A BOUND subtree stays bound all the way down: a `List<int?>` parameter's argument is what the target
        // declares, not a position this compiler gets to canonicalize.
        var inner = pos == Pos.Bound ? Pos.Bound : Pos.Argument;
        var slot = pos == Pos.Bound ? Pos.Bound : Pos.Slot;
        return t switch
        {
            TypeNode.Nullable n => new TypeNode.Nullable(Erase(n.Of, slot, isValue)),
            // An NRT-OBLIVIOUS `T!` is a pure nullability ANNOTATION, not a container — BirTypeLowering lowers
            // straight through it and propagates the position with it — so its inner keeps THIS node's position.
            TypeNode.Oblivious o => new TypeNode.Oblivious(Erase(o.Of, pos, isValue)),
            TypeNode.Projection p => new TypeNode.Projection(p.Variance, Erase(p.Of, pos, isValue)),
            TypeNode.Fqn { Args: null } f => f,
            TypeNode.Fqn f => f with { Args = f.Args.Select(a => Erase(a, inner, isValue)).ToArray() },
            TypeNode.Array a => new TypeNode.Array(Erase(a.Elem, inner, isValue), a.Rank, a.SzArray),
            TypeNode.ByRef b => new TypeNode.ByRef(Erase(b.Of, slot, isValue)),
            // A delegate's RETURN follows the argument rule; its PARAMETERS keep the declared form. See the header:
            // the two differ only because a callable reference to a DECLARED member has no forwarder yet. The
            // NOMINAL family and the context parameters ride through untouched — an erasure states positions, and a
            // rebuild that dropped them turned `Func<int, List<int?>>` into a family-less function type that no
            // longer compared equal to the declaration it is the image of.
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Erase(fn.Ret, inner, isValue),
                fn.Params.Select(p => Erase(p, slot, isValue)).ToArray(),
                fn.Recv == null ? null : Erase(fn.Recv, slot, isValue), fn.Clr,
                fn.Ctx?.Select(c => Erase(c, slot, isValue)).ToArray()),
            _ => t,
        };
    }

    // THE OTHER DIRECTION OF THE SAME POSITIONAL RULE: does this LOWERED declaration put a `System.Nullable<V>`
    // where `Erase` would have put an `object`? That is exactly the shape no Kotlin type inhabits, and
    // ForeignNullableGenericCrossing refuses a .NET member that declares one.
    //
    // It lives beside `Erase` and its arms are read against `Erase`'s, position for position, because the two ARE one
    // statement and stating them in two files is how they drift apart. They did: a delegate PARAMETER is `Pos.Slot`
    // here (a concrete `V?` keeps its `Nullable<V>`, see the header), and a copy of this walk that called it an
    // argument refused a `Func<int?, string>` parameter Kotlin inhabits exactly.
    //
    // The HEAD is not a crossing in either arm: a direct `Nullable<V>` parameter or return IS what a Kotlin scalar
    // `Int?` is, and it crosses without any adaptation at all. That includes a `Nullable<!!0>` on a `T : struct` .NET
    // generic, which a Kotlin `T?` inhabits at every instantiation.
    internal static bool ErasureWouldMove(TypeNode lowered) => AtSlot(lowered);

    // The same question asked of an ARGUMENT rather than a slot, for a reader that has descended INTO a declaration
    // and has to keep saying which position it is standing in. The rule is positional, so a walk that carries the
    // position needs both arms of it; asking `ErasureWouldMove` of an argument would answer about the wrong one — a
    // concrete `V?` moves here and not there.
    internal static bool ErasureWouldMoveArgument(TypeNode lowered) => AtArgument(lowered);

    // THE IMAGE THAT `ErasureWouldMove` ANSWERS ABOUT: what a Kotlin declaration filling this LOWERED foreign slot
    // physically states. `List<Nullable<int32>>` -> `List<object>`, and a slot the erasure does not move is returned
    // unchanged — so comparing a Kotlin declaration against this image asks "is THIS the member that fills THAT
    // slot?" exactly, rather than by name and parameter count, which cannot tell a crossing overload from its
    // untouched sibling.
    //
    // The oracle is a constant `true` and not the build's struct-ness predicate, for the same reason `AtArgument`
    // needs none: on a LOWERED foreign declaration every surviving `Nullable` is already a real `System.Nullable<V>`
    // over a value type, so asking whether its inner may be a value has one answer. Sharing `Erase` rather than
    // walking the positions again is what keeps the image and the question the same statement.
    internal static TypeNode ErasedLoweredSlot(TypeNode lowered) => Erase(lowered, Pos.Slot, _ => true);

    // `Erase` at Pos.Slot: the head is not moved; an Fqn's arguments, an array's element and a delegate's RETURN are
    // arguments; a byref referent, a nullable's inner and a delegate's PARAMETERS are slots.
    static bool AtSlot(TypeNode t) => t switch
    {
        // A resolved pointer's pointee is the foreign declaration's exact signature, not Kotlin generic storage.
        TypeNode.Ptr => false,
        TypeNode.Fqn { Args: { } args } => args.Any(AtArgument),
        TypeNode.Array a => AtArgument(a.Elem),
        TypeNode.ByRef b => AtSlot(b.Of),
        TypeNode.Nullable n => AtSlot(n.Of),
        TypeNode.Oblivious o => AtSlot(o.Of),
        TypeNode.Projection p => AtArgument(p.Of),
        TypeNode.Fn fn => AtArgument(fn.Ret) || fn.Params.Any(AtSlot)
                          || (fn.Recv != null && AtSlot(fn.Recv)),
        _ => false,
    };

    // `Erase` at Pos.Argument: a `Nullable` node HERE is the position that moves. By this point the tree is lowered,
    // so every surviving `Nullable` is a real `System.Nullable<V>` over a value type — BirTypeLowering strips each
    // reference `?` before then. An NRT-OBLIVIOUS wrapper is a pure annotation and is looked through, so a
    // `[MaybeNull] List<int?>` is the same crossing as a plain one.
    static bool AtArgument(TypeNode t) => t switch
    {
        TypeNode.Nullable => true,
        TypeNode.Oblivious o => AtArgument(o.Of),
        _ => AtSlot(t),
    };

    // `X?` where `X` may be a value type: an open type variable, or a concrete value FQN. A reference `X` is excluded —
    // its `?` is not a physical difference on the CLR.
    //
    // EVERY type variable qualifies, whatever its bound. `fun <T : CharSequence> f(xs: Array<T?>)` has no value
    // instantiation and still erases, because the rule is uniform rather than bound-consulting: one physical form per
    // declaration, decided without resolving where each bound leads. The cost is a `CharSequence`-bounded array that
    // boxes for nothing; the alternative is a slot whose representation depends on a bound the reader has to chase,
    // and two `Array<T?>` declarations that cannot meet when one is bounded and one is not.
    internal static bool IsNullableMaybeValue(TypeNode t, ValueTypeOracle isValue)
        => t is TypeNode.Nullable n && MayBeValue(n.Of, isValue);

    // A CONSTRUCTED name is classified like any other: `KeyValuePair<K,V>` and `ArraySegment<T>` are structs, and the
    // oracle uses their complete FQN, including arguments. Matching only the argument-less shape left every
    // constructed BCL struct
    // classified as a reference, so `Array<KeyValuePair<K,V>?>` stayed a `Nullable<KVP>[]` while the open `Array<T?>`
    // it has to meet was `object[]` — the unrelated pair this whole decision exists to delete, and it segfaulted the
    // process rather than failing loudly.
    static bool MayBeValue(TypeNode t, ValueTypeOracle isValue) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Fqn f => isValue?.Invoke(f) == true,
        TypeNode.Oblivious o => MayBeValue(o.Of, isValue),
        _ => false,
    };
}
