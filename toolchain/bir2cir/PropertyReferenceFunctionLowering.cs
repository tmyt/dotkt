using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// A Kotlin property reference is simultaneously a KProperty value and a FunctionN value. BIR keeps that meaning as
// an ordinary generated KProperty construction plus the transient `propertyFunction.iface` fact. A CLR delegate is
// not an interface and the generated KProperty class cannot physically implement it, so a construction or typed
// KProperty value that fills a function-typed SLOT needs a forwarding closure. That representation decision belongs
// here, never in kotc.
//
// The rule is structural over slots, not syntax sites. A slot is a field/local initializer or write, a return, a
// call/constructor/delegate argument, an array initializer/write (including vararg packs), or either arm of a value
// join. Transparent valueBlock/cond/cast/array containers propagate the same target to their values. The forwarding
// class captures the KProperty through its declared interface and invokes that interface, so no generated-name,
// physical-layout, or method-body inference is involved.
static partial class PropertyReferenceFunctionLowering
{
    sealed class Context
    {
        public TypeNode Ret;
        public readonly Dictionary<string, TypeNode> Locals = new(StringComparer.Ordinal);
    }


    static TypeNode Substitute(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArgs.Length => methodArgs[tv.I],
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(t => Substitute(t, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Substitute(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Substitute(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(Substitute(a.Elem, ownerArgs, methodArgs), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(Substitute(b.Of, ownerArgs, methodArgs)),
        TypeNode.Ptr p => new TypeNode.Ptr(Substitute(p.Of, ownerArgs, methodArgs)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, Substitute(m.M, ownerArgs, methodArgs),
            Substitute(m.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Substitute(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(t => Substitute(t, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : Substitute(fn.Recv, ownerArgs, methodArgs), fn.Clr,
            fn.Ctx?.Select(t => Substitute(t, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };


    static TypeNode.Fn FunctionTarget(TypeNode type)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return type as TypeNode.Fn;
    }

    static TypeNode ArrayTarget(TypeNode type)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return (type as TypeNode.Array)?.Elem;
    }


    static bool MentionsTypeVariable(TypeNode type) => type switch
    {
        TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } args } => args.Any(MentionsTypeVariable),
        TypeNode.Projection p => MentionsTypeVariable(p.Of),
        TypeNode.Nullable n => MentionsTypeVariable(n.Of),
        TypeNode.Oblivious o => MentionsTypeVariable(o.Of),
        TypeNode.Array a => MentionsTypeVariable(a.Elem),
        TypeNode.ByRef b => MentionsTypeVariable(b.Of),
        TypeNode.Ptr p => MentionsTypeVariable(p.Of),
        TypeNode.Mod m => MentionsTypeVariable(m.M) || MentionsTypeVariable(m.Of),
        TypeNode.Fn fn => MentionsTypeVariable(fn.Ret) || fn.DelegateParams.Any(MentionsTypeVariable),
        _ => false,
    };

}
