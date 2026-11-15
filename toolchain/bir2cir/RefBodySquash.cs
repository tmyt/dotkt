using System;
using DotKt.Bir;
using DotKt.Cir;

// REFERENCE-build body squashing. The pure-Kotlin reference stdlib (DotKt.Private.Stdlib.dll) is a METADATA-ONLY
// surface: every declaration keeps its full signature/type/supertype/generic/attribute metadata, but its BODY is
// replaced with a single `throw NotImplementedException()` statement. The ref dll is never executed (it is loaded
// compile-time only and substituted away at app-emit), so a thrown stub is the correct, minimal body.
//
// WHY this is a prerequisite for kotc emitting bare `kotlin.Int`: in the reference build bir2cir keeps `kotlin.*`
// primitive tokens VERBATIM (they are not lowered to the CLR primitive). If a real method body were emitted, IL
// operating on such a bare-value `kotlin.Int` (arithmetic / box / conv) would have no valid CLR primitive to act
// on. Squashing every body to a throw guarantees no such IL is ever produced — the signature carries `kotlin.Int`
// purely as metadata.
//
// Mutates the declaration tree after semantic metadata capture and before physical call binding. Only the hierarchy that ilemit emits as IL
// bodies is touched: file-level methods and the current flat type declarations' methods + constructors.
// Property accessors are already represented as methods, so they are covered by the method pass.
static partial class RefBodySquash
{
    // Metadata-only primitive facades are reference classes, not native literal stack types. Historically
    // ilemit's constant switch implicitly emitted null for these slots. Scalar projection preserves the literal
    // value first; this reference-only pass then replaces executable facade values with physical null placeholders.
    // Source semantics remain in the previously captured Kotlin metadata; this image is never executed.
    public static CirFileDeclaration LowerLiteralPlaceholders(CirFileDeclaration root,
        BirTypeLowering.PhysicalTypeContext context)
    {
        return CirBodyRewrite.RewriteCirFileDeclaration(root, node =>
        {
            if (node is CirConstant { Type: TypeNode.Fqn facade } constant)
            {
                var native = BirTypeLowering.LowerSlot(facade, context, BirTypeLowering.PhysicalSlotRole.Value, force: true);
                if (native is TypeNode.Fqn physical && facade.Name != physical.Name)
                    return constant with { Type = new TypeNode.Fqn("System.Object"), Value = new AttributeArgument.NullValue() };
            }
            return node;
        });
    }

}
