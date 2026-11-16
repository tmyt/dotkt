using DotKt.Bir;

// CLR lexical-access projection. Kotlin declarations keep their source visibility; when ownership lowering places a
// frontend-valid caller in another CLR TypeDef or assembly and direct CLR access is no longer legal, put a private extern
// [UnsafeAccessor] method on that physical caller and route the edge through it. No target declaration is widened.
//
// kotc carries the accessed declaration's owner/method generic frames in BIR. Re-declare owner parameters on a
// synthesized generic holder and method parameters on the accessor itself, preserving the exact form/index/constraints
// required by .NET 9+'s strict UnsafeAccessor signature matcher. Invalid/old BIR is outside the toolchain contract.
static partial class UnsafeAccessorLowering
{
    const string AttributeFqn = "System.Runtime.CompilerServices.UnsafeAccessorAttribute";
    const string KindFqn = "System.Runtime.CompilerServices.UnsafeAccessorKind";
    static int _counter;

    static TypeNode SubstituteOwnerSlots(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv tv when tv.Scope == "type" && tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name,
            f.Args?.Select(arg => SubstituteOwnerSlots(arg, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteOwnerSlots(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteOwnerSlots(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstituteOwnerSlots(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteOwnerSlots(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstituteOwnerSlots(fn.Ret, args),
            fn.Params.Select(param => SubstituteOwnerSlots(param, args)).ToArray(),
            fn.Recv == null ? null : SubstituteOwnerSlots(fn.Recv, args), fn.Clr,
            fn.Ctx?.Select(param => SubstituteOwnerSlots(param, args)).ToArray()),
        _ => type,
    };

    static string AccessorName(string target)
    {
        var safe = new string((target ?? "member").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"dotkt$unsafe${System.Threading.Interlocked.Increment(ref _counter)}${safe}";
    }

    static string SuperForwarderName(string target)
    {
        var safe = new string((target ?? "member").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"dotkt$super${System.Threading.Interlocked.Increment(ref _counter)}${safe}";
    }

}
