using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// TOP-LEVEL REIFIED ENUM INTRINSICS (#73). kotc emits the FAITHFUL top-level call for `enumValues<T>()` /
// `enumValueOf<T>(name)` — a plain
// `callStatic owner:null method:"enumValues" typeArgs:[T] args:[…]` (the Kotlin fact; it names no CLR shape and
// carries no `kotlin.*` FQN of the intrinsic). On the CLR every type arg is REIFIED, so these lower at the call
// site exactly like `T.values()` / `T.valueOf(name)` — a Kotlin<->CLR equivalence, so it lives HERE:
//
//   enumValues<T>()                    -> rich enum: callStatic <T>.values(); otherwise an enumValues semantic node
//   enumValueOf<T>(name)                -> rich enum: callStatic <T>.valueOf(name)      ; basic/gp: {k:enumParse,type:T,arg}
//
// RICH vs BASIC comes from an explicit producer fact: local BIR carries `enumRich:true`, while a referenced producer
// carries trusted [KotlinRichEnum]. EnumValueLowering resolves the semantic values node to declaration-ordered
// physical tables or the ordered runtime helper for a generic parameter. No values()/valueOf() name/signature
// convention is used to classify a referenced owner.
//
// Runs EARLY (grouped with the range/array/char faithful recognitions, before MemberCallSubstitution / BirTypeLowering)
// so the produced enumValues/enumParse/values nodes enter the ordinary CIR pipeline with their semantic enum identity
// still available.
static partial class EnumIntrinsicLowering
{
    static readonly HashSet<string> Names = new(System.StringComparer.Ordinal)
    { "enumValues", "enumValueOf" };

    // Collect the local RICH-enum type names (a `kind:"class"` decl carrying the faithful `enumRich:true` marker),
    // across ALL input files (a cross-file `enumValues<E>()` may name an enum declared in another input).
    public static HashSet<string> CollectRichEnums(IEnumerable<BirFileDeclaration> roots)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty().OfType<BirClassDefinition>())
                if (type.Kind.OrDefault() == "class" && type.EnumRich.OrDefault()
                    && type.Name.OrDefault() is string n)
                    set.Add(n);
        return set;
    }
}
