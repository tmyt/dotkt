using System;
using System.Linq;
using DotKt.Bir;

// DECL-position NRT-byte collection (#37/#48 nullability fold). Runs on the SEMANTIC BIR (kotlin.* type tokens, the
// `{t:nullable}` reference wrappers still present) — AFTER the object-erasure passes but BEFORE BirTypeLowering strips
// the reference `?` wrappers to bare types. For every declaration slot whose Type node carries a nullable REFERENCE
// position, it emits the flattened `NullableAttribute` byte array in place of the retired scalar `"nullable"` /
// `"retNullable"` decl flags:
//   * a method            -> `retNullableFlags` (its `ret` node)
//   * a method param      -> `nullableFlags`    (its `type` node)
//   * a CONSTRUCTOR param -> `nullableFlags`    (its `type` node)
//   * a field / property  -> `nullableFlags`    (its `type` node)
// The physical-head query determines which generic arguments still occupy signature positions, without
// discarding the semantic annotation wrappers on surviving arguments. It uses the current build's representation.
//
// The CONSUMER is RoundtripMetadata.CompleteMetadata, which turns each flags key into a real `[Nullable]` entry in the decl's
// `attrs`/`retAttrs` array; ilemit then stamps those entries through its generic BuildCab path and never reads the
// flags keys itself. Producer and consumer traversals must therefore agree on the decl kinds they visit — a slot this
// pass skips silently loses its `[Nullable]` at stamp time (the #251 ctor bug). The inventory is the current methods,
// fields, properties and constructors, including those on current generated types; executable descendants are not
// another declaration inventory.
//
// A VALUE `T?` (`Nullable<Int>`) contributes NO byte (it is the structural Nullable<T>, kept by BirTypeLowering); a
// non-null reference emits nothing (the type's [NullableContext(1)] default covers it) — only a nullable reference
// position yields an override array. NEVER overwrites a flags key already set (SuspendColdLowering's synthesized
// Task-bridge sets its own `retNullableFlags` up-front and must win).
static partial class DeclNullableFlags
{
    // Unit has no NRT byte, and function types carry only a head byte in the Kotlin NRT convention.
    // Preserve the remaining annotation-bearing subtree before reference-nullability stripping and final type
    // lowering. CLR type aliases also discard their Kotlin classifier, potentially mapping several Kotlin types
    // to one CLR type. Preserve that identity explicitly rather than expecting an importer to invert the alias.
    // Earlier representation passes preserve any changed source surface; never replace their carrier.
    static bool RequiresExactSurface(TypeNode type, Func<TypeNode.Fqn, bool> hasAlias) => type switch
    {
        TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Unit", Args: null } } => true,
        TypeNode.Nullable n => RequiresExactSurface(n.Of, hasAlias),
        TypeNode.Oblivious o => RequiresExactSurface(o.Of, hasAlias),
        TypeNode.Projection p => RequiresExactSurface(p.Of, hasAlias),
        TypeNode.Fqn f => hasAlias(f) || f.Args?.Any(arg => RequiresExactSurface(arg, hasAlias)) == true,
        TypeNode.Array a => RequiresExactSurface(a.Elem, hasAlias),
        TypeNode.ByRef b => RequiresExactSurface(b.Of, hasAlias),
        TypeNode.Fn => true,
        _ => false,
    };
}
