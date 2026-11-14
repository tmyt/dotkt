using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    readonly record struct ReferencedUse(bool Static, string Identity, string Accessor, string Method,
        TypeNode Owner, TypeNode Expected, TypeNode Return, int ArgumentCount, TypeNode[] TypeArguments);

    // This consumer needs no local declaration catalog. Its authority is the
    // producing assembly's Object ABI and trusted Kotlin return declaration.
    static TypeNode SelectReferencedReturn(ReferencedUse use, ReferenceMetadataIndex references)
    {
        // Unit expression statements ignore the Object result. Stating void
        // here would lose their pop even though the physical call pushes it.
        if (use.Expected == null || IsObject(use.Expected) || IsUnit(use.Expected)
            || use.Owner is not TypeNode.Fqn owner) return null;
        if (use.Accessor == "get" && references.IsUncheckedGenericObjectGetter(use.Identity, owner.Name,
            use.Static, use.ArgumentCount, use.TypeArguments.Length))
            // A selected getter's ret can still be declaration-relative. The
            // source use is already instantiated in the caller's frame.
            return use.Expected;
        if (use.Return != null || use.Method == null
            || !references.TryUncheckedGenericCastReturn(owner.Name, use.Method, use.Static,
                use.ArgumentCount, use.TypeArguments.Length, out var declared)) return null;
        var actual = Substitute(declared, owner.Args ?? Array.Empty<TypeNode>(), use.TypeArguments);
        return actual != null && actual.Equals(use.Expected) ? use.Expected : null;
    }

    internal static void ApplyReferenced(BirFileDeclaration root, ReferenceMetadataIndex references)
    {
        void Walk(BirGraphValue node)
        {
            // Preserve the existing pre-order rewrite: a call's physical
            // result is stated before visiting its independently owned operands.
            switch (node)
            {
                case BirCallStatic call:
                {
                    KotlinPropertyAccessors.TryCallIdentity(call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(),
                        call.Method.OrDefault(), call.Prop.OrDefault(), out _, out var role);
                    var result = SelectReferencedReturn(new(true, call.DeclarationId.OrDefault(), role, call.Method.OrDefault(),
                        call.OwnerType.OrDefault() ?? call.Owner.OrDefault() ?? call.CalleeOwner.OrDefault(),
                        call.Sty.OrDefault(), call.Ret.OrDefault(), call.Args.OrEmpty().Length, call.TypeArgs.OrEmpty()), references);
                    if (result != null) call.Ret = BirGraphValue.CopyType(result);
                    break;
                }
                case BirCallInstance call:
                {
                    KotlinPropertyAccessors.TryCallIdentity(call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(),
                        call.Method.OrDefault(), call.Prop.OrDefault(), out _, out var role);
                    var result = SelectReferencedReturn(new(false, call.DeclarationId.OrDefault(), role, call.Method.OrDefault(),
                        call.OwnerType.OrDefault(), call.Sty.OrDefault(), call.Ret.OrDefault(),
                        call.Args.OrEmpty().Length, call.TypeArgs.OrEmpty()), references);
                    if (result != null) call.Ret = BirGraphValue.CopyType(result);
                    break;
                }
            }
            VisitSourceUseChildren(node, Walk);
        }
        Walk(root);
    }
}
