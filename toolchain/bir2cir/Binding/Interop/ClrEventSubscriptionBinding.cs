using DotKt.Bir;

// Source forwarding and live handle construction use the same declaration-space
// substitution. An absent application leaves its authored physical TV in place.
static partial class ClrEventSubscriptionBinding
{
    static TypeNode SubstituteTypeVariables(TypeNode type, TypeNode[] ownerArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when ownerArgs != null && tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Fqn { Args: { Length: > 0 } args } f =>
            new TypeNode.Fqn(f.Name, args.Select(argument => SubstituteTypeVariables(argument, ownerArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteTypeVariables(n.Of, ownerArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteTypeVariables(o.Of, ownerArgs)),
        TypeNode.Array a => new TypeNode.Array(SubstituteTypeVariables(a.Elem, ownerArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteTypeVariables(b.Of, ownerArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstituteTypeVariables(fn.Ret, ownerArgs),
            fn.Params.Select(parameter => SubstituteTypeVariables(parameter, ownerArgs)).ToArray(),
            fn.Recv == null ? null : SubstituteTypeVariables(fn.Recv, ownerArgs), fn.Clr,
            fn.Ctx?.Select(context => SubstituteTypeVariables(context, ownerArgs)).ToArray()),
        _ => type,
    };
}
