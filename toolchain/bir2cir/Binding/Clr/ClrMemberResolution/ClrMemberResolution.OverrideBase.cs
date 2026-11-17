using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotKt.Bir;

// W1-S4 (#46/#183) — RESOLVED-CLR-IR carry for the DECLARATION-SIDE override base slot. A Kotlin class member that
// overrides a .NET base-CLASS virtual accessor (`override val message` on a `kotlin.Exception`->System.Exception base:
// prop_get<message> plus `pendingOverrideOwner`="System.Exception" and `pendingOverrideMember`="get_Message" from
// DeclarationRename) needs ilemit to
// `DefineMethodOverride` against the EXACT base virtual so the emitted method reuses the base vtable slot (else a fresh
// newslot is minted and `callvirt System.Exception::get_Message` binds the base value, not the override).
//
// Until now ilemit resolved that base virtual with `baseT.GetMethod(name, ps) ?? baseT.GetMethod(name)` — a name+params
// match with a NAME-ONLY first-pick fallback (exactly the fallback class #46 removes at call sites). This pass moves the
// resolution HERE: it resolves the base virtual off the ref.dll MLC and stamps the complete scalar `clrOverrideRef`,
// so ilemit LINKS the unique base slot (0 = hard ABI error, >1 = malformed) and never first-picks. Runs inside ClrMemberResolution's
// owning physical walk on every method declaration carrying PendingOverrideOwner.
//
// SCOPE: `pendingOverrideOwner` identifies property-accessor and projected-indexer overrides of a .NET base class.
// Indexers also need an explicit base slot when their implementation narrows the return type. An erasure bridge
// that takes ownership of the same slot removes the typed implementation's pending binding to avoid two MethodImpls.
static partial class ClrMemberResolution
{
    static MemberRefNode SelectNativeOverrideBase(TypeNode.Fqn owner, Type open, string name,
        List<TypeNode> arguments, TypeNode result)
    {
        var candidates = new List<MethodInfo>();
        try { candidates.AddRange(open.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => method.Name == name && method.IsVirtual && method.GetParameters().Length == arguments.Count)); } catch { }
        var selected = PickOverrideBase(candidates, arguments, result, open, owner.Args,
            $"override base={owner.Name}.{name}({DescArgs(arguments)}):{result}");
        return ClrMemberReferences.MemberRefOf(_refs, _localTypes, selected, MemberRefNode.Kinds.Method, open, owner.Args);
    }
    // Pick the UNIQUE base virtual to override. An override's DECLARED params ARE the base slot's params (that is what
    // "override" means), so this is a STRUCTURAL identity match — NOT the call side's arg-applicability (which would let
    // a scalar `Any` arg "match" an `Any[]` param via the object-downcast rule and make BaseContinuationImpl's
    // `create(Any,Cont)` / `create(Any[],Cont)` ambiguous). Require exactly one after §12.8.10.2 most-derived shadowing;
    // 0 = hard ABI error, >1 = malformed. NEVER a first-pick.
    static MethodInfo PickOverrideBase(List<MethodInfo> cands, List<TypeNode> argNodes, TypeNode returnNode,
        Type open, TypeNode[] ownerArgs,
        string desc)
    {
        var hits = ClrDeclarationMatching.MostDerived(cands.Where(c => _refs.NativeMethodSignature(c) != null
            ? ExternalMethodImplSlotMatches(c, open, ownerArgs, argNodes, returnNode)
            : OverrideMatch(c.GetParameters(), argNodes) && OverrideParamMatch(returnNode, c.ReturnType)).ToList());
        if (hits.Count == 0) throw ClrDeclarationMatching.NoMatch(desc, cands);
        if (hits.Count == 1) return hits[0];
        throw ClrDeclarationMatching.Malformed(desc, hits);
    }

    static bool OverrideMatch(ParameterInfo[] ps, List<TypeNode> argNodes)
    {
        if (ps.Length != argNodes.Count) return false;
        for (int i = 0; i < ps.Length; i++) if (!OverrideParamMatch(argNodes[i], ps[i].ParameterType)) return false;
        return true;
    }

    // STRUCTURAL identity of an override's declared param TypeNode `a` (in the CLR-lowered CIR vocabulary — a scalar
    // `System.Object`, `Continuation<System.Object>`) against the base virtual's ref.dll param Type `p` (UNLOWERED —
    // `kotlin.Any`, `Continuation<kotlin.Any>`). Preserves array/scalar/generic/byref STRUCTURE on both sides; the only
    // leaf discrepancy is Kotlin `Any` <-> `System.Object` (normalized as the shared top type). A BARE positional-tv
    // base param is a wildcard (its concrete form is the override's substituted arg at the instantiation); a tv-BEARING
    // structure (`T[]`, `Continuation<T>`) recurses STRUCTURALLY (mirroring ilemit's GenericParamMatches) and bottoms
    // out at the bare-tv leaves — so a generic base's `foo(T[])` vs `foo(T)` stay distinguishable, not both wildcarded.
    static bool OverrideParamMatch(TypeNode a, Type p)
    {
        if (a is TypeNode.Oblivious ob) return OverrideParamMatch(ob.Of, p);
        p = ClrDeclarationMatching.AliasResolve(_refs, p);
        if (p.IsGenericParameter) return true;   // substituted at the instantiation — wildcard
        if (p.IsByRef) return a is TypeNode.ByRef b && OverrideParamMatch(b.Of, p.GetElementType());
        if (p.IsPointer)
        {
            if (a is not TypeNode.Ptr pointer) return false;
            var pointee = p.GetElementType();
            if (pointee?.FullName == "System.Void"
                && pointer.Of is TypeNode.Fqn { Name: "void" or "System.Void", Args: null })
                return true;
            return pointee != null && OverrideParamMatch(pointer.Of, pointee);
        }
        if (p.IsArray) return a is TypeNode.Array ar && OverrideParamMatch(ar.Elem, p.GetElementType());
        if (p.IsGenericType && ClrDeclarationMatching.SafeDef(p) == ClrDeclarationMatching.NullableDef(_refs))
            return a is TypeNode.Nullable nv && OverrideParamMatch(nv.Of, p.GetGenericArguments()[0]);
        if (a is TypeNode.Nullable nn) return OverrideParamMatch(nn.Of, p);   // reference-nullable arg = same .NET type
        if (p.IsGenericType)   // constructed generic reference/value type -> recurse args structurally
        {
            if (a is not TypeNode.Fqn f || f.Args == null) return false;
            var adef = ClrDeclarationMatching.RefDef(_refs, f.Name, f.Args.Length);
            if (adef == null || ClrDeclarationMatching.SafeDef(adef) != ClrDeclarationMatching.SafeDef(p)) return false;
            var pa = p.GetGenericArguments();
            if (pa.Length != f.Args.Length) return false;
            for (int i = 0; i < pa.Length; i++) if (!OverrideParamMatch(f.Args[i], pa[i])) return false;
            return true;
        }
        // Scalar leaf: resolve `a` and require identity, with Kotlin `Any` == `System.Object` as the shared top type.
        var aT = ClrDeclarationMatching.MapMlc(_refs, a);
        if (aT == null) return false;
        if (aT == p) return true;
        return IsTopType(aT) && IsTopType(p);
    }

    static bool IsTopType(Type t) { try { return t.FullName is "System.Object" or "kotlin.Any"; } catch { return false; } }
}
