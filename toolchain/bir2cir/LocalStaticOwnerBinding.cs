using System.Collections.Generic;

// Put a Kotlin static member call onto the CLR owner axis.
//
// kotc reports a static member call in Kotlin vocabulary: `callStatic` naming the DECLARING TYPE in `ownerType`, with
// no `owner` at all. That is the whole frontend fact — `class C { companion { fun f() } }` resolves `C.f()` to a member
// of `C` that takes no receiver. Which CLR type physically hosts it is this layer's decision, and for a type declared
// in THIS compilation the answer is "that same type": the emitted TypeDef is the owner, so the declaring identity moves
// verbatim onto `owner`, the axis ilemit dispatches from.
//
// The discriminator is structural and local: this compilation declares a type of that name, and that declaration
// carries a static member of that name. Nothing else qualifies — a reference-KLIB or .NET owner is bound by
// NetInteropBinding / MemberCallSubstitution off the reference universe, and both of those key on the ownerless axis.
// Moving the token (rather than copying it) keeps ONE owner axis on the node, so the later generic-owner closure has a
// single key to rewrite and cannot leave the two spellings disagreeing.
//
// Runs per file, right after NetInteropBinding and before MemberCallSubstitution: the external binders have had first
// refusal, and the ownerless recognizers that follow must not see a call whose declaring type is already resolved.
static partial class LocalStaticOwnerBinding
{
    internal sealed class LocalStatics
    {
        public string PhysicalOwner;
        public readonly HashSet<string> Methods = new(System.StringComparer.Ordinal);
        public readonly Dictionary<string, string> PropertyAccessors = new(System.StringComparer.Ordinal);
    }


    static void AddStatic(Dictionary<string, LocalStatics> index, string semanticOwner, string physicalOwner,
        string method, string property, string accessor)
    {
        if (!index.TryGetValue(semanticOwner, out var statics))
            index[semanticOwner] = statics = new LocalStatics { PhysicalOwner = physicalOwner };
        else if (statics.PhysicalOwner != physicalOwner)
            throw new System.InvalidOperationException($"multiple local static owners claim semantic type '{semanticOwner}'");
        statics.Methods.Add(method);
        if (property != null) statics.PropertyAccessors[property + "\u001f" + accessor] = method;
    }

}
