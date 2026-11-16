using System;
using System.Linq;
using DotKt.Bir;

// COMPREHENSIVE reference-nullable strip (#37/#48). ilemit's MapType asserts that a `{t:nullable}` node it maps has a
// VALUE inner (a reference `Nullable<referenceType>` is not a real CLR type — a reference is nullable in IL regardless).
// So NO `{t:nullable,of:<reference>}` may reach ilemit in ANY position: not just decl `type`/`ret`/param slots (which
// physical slot lowering visits), but also owner generic type-args (ilemit reaches these via ParseOwnerT), call
// `argTypes`/`typeArgs`, expression `cast`/`type` fields, and any node child. Physical slot roles choose where to
// lower value/return representations; this independent strip also reaches nested type arguments — the distinction
// originally surfaced in the ref-stdlib emit on a `Continuation<Any?>`-shaped owner.
//
// This sweep is position-INDEPENDENT: it walks the generated structured type fields, including nested signatures,
// and recursively strips their reference nullables, leaving encoded source leaves opaque. A VALUE
// `{t:nullable,of:<value/struct/enum>}` STAYS `{t:nullable}` (ilemit builds `System.Nullable<T>`); a `Tv` inner is
// non-value -> bare `Tv` (the object-erasure lifeline passes already converted the dataflow-critical unconstrained
// `T?` to `object` upstream, so a surviving `Nullable(Tv)` is a non-erased usage that lowers to the bare tv).
// Nullable Unit is retained until BirTypeLowering chooses its value-returning ABI rather than Unit's void ABI;
// that lowering removes its reference wrapper before CIR reaches ilemit.
//
// Runs on the SEMANTIC tree (kotlin.* names) AFTER DeclNullableFlags (so the NRT byte walk still saw the nullability)
// and BEFORE BirTypeLowering — the oracle is unambiguous on the semantic names, and USAGE positions get ONLY the bare
// strip, never an NRT byte (nullability at a type usage is compile-time-only, not NRT-annotated).
static partial class ReferenceNullableStrip
{
    internal static void FunctionSelfTest() => FunctionRewriteTests.Check("reference nullable strip",
        type => Strip(type, _ => false));

    // Recursively strip reference nullables within a Type: a `Nullable` with a value inner keeps its wrapper; a
    // `Nullable` with a reference/tv/generic/array/fn inner collapses to the (recursively-stripped) bare inner,
    // except Unit's return-contract distinction, which BirTypeLowering consumes.
    static TypeNode Strip(TypeNode t, ValueTypeOracle isValue) => t switch
    {
        // Unit? is a value-returning reference contract, unlike Unit's void return convention.
        // Keep that distinction until BirTypeLowering selects the physical return/delegate shape.
        TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Unit", Args: null } } n => n,
        TypeNode.Nullable n => IsValueInner(n.Of, isValue)
            ? new TypeNode.Nullable(Strip(n.Of, isValue))
            : Strip(n.Of, isValue),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Strip(o.Of, isValue)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(x => Strip(x, isValue)).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Strip(a.Elem, isValue)),
        TypeNode.ByRef b => new TypeNode.ByRef(Strip(b.Of, isValue)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Strip(fn.Ret, isValue),
            fn.Params.Select(x => Strip(x, isValue)).ToArray(),
            fn.Recv == null ? null : Strip(fn.Recv, isValue), fn.Clr,
            fn.Ctx?.Select(context => Strip(context, isValue)).ToArray()),
        _ => t,
    };

    // A CONSTRUCTED name is a struct if the oracle says so: `ArraySegment<String>?` is a `Nullable<ArraySegment<String>>`
    // and keeps its wrapper exactly as `Int?` does. Matching only the argument-LESS shape stripped it to the bare
    // struct, so a `null` element read out of an `Array<ArraySegment<String>?>` was unboxed as a NON-nullable struct —
    // a NullReferenceException, or silently no null at all. The oracle answers false for a constructed REFERENCE
    // generic (`List<String>?`), which is what keeps that side stripping as before.
    static bool IsValueInner(TypeNode of, ValueTypeOracle isValue) =>
        of is TypeNode.Fqn f && isValue(f);
}
