using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
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
// Walk (last pass, fully-lowered tree) on every method DECLARATION node carrying `pendingOverrideOwner`.
//
// SCOPE: `pendingOverrideOwner` is stamped ONLY on PROPERTY-ACCESSOR overrides of a .NET base CLASS virtual (the external
// Property/MethodSemantics slot on a non-generic BCL class such as System.Exception — a plain-method override binds
// its base slot implicitly by CLR
// name+sig matching, no DefineMethodOverride). The matcher below also handles a generic base def (positional-tv params
// treated as substitution wildcards) for completeness, but the corpus exercises only the non-generic accessor case.
static partial class ClrMemberResolution
{
    static void ResolveOverrideBase(JsonObject node)
    {
        var ownerSpec = TypeJson.Read(node["pendingOverrideOwner"]) as TypeNode.Fqn;
        var owner = ownerSpec?.Name;
        var implementationName = (node["name"] as JsonValue)?.GetValue<string>();
        var slotName = (node["pendingOverrideMember"] as JsonValue)?.GetValue<string>() ?? implementationName;
        var rawReturnNode = TypeJson.Read(node["pendingOverrideReturn"]);
        var returnNode = rawReturnNode == null ? null : BirTypeLowering.CanonicalPhysicalSlotType(rawReturnNode);
        if (owner == null || implementationName == null || slotName == null || node["params"] is not JsonArray) return;
        // Freeze the exact external slot before final MethodDef allocation can rename the implementing declaration.
        // ilemit consumes this descriptor one-to-one and must never fall back to the implementation's own name.
        node["pendingOverrideMember"] = slotName;
        if (returnNode == null)
            throw new InvalidOperationException($"bir2cir: override '{owner}.{slotName}' is missing the exact base return descriptor");
        var open = ResolveOwnerType(ownerSpec);
        if (open == null)
            throw new InvalidOperationException($"bir2cir: override base owner '{owner}' does not resolve to a .NET type (#46/#183 pendingOverrideOwner carry)");
        // Read EVERY param type — a null-drop would shrink the arity and could bind a wrong-arity base overload
        // (BaseContinuationImpl's create(completion)/create(value,completion)/create(args[],completion) family), so an
        // unreadable node is a hard error, not silently skipped.
        var argNodes = (node["params"] as JsonArray).Select((p, i) => TypeJson.Read((p as JsonObject)?["type"])
            ?? throw new InvalidOperationException($"bir2cir: override '{owner}.{slotName}' param #{i} has an unreadable type node (#46/#183 pendingOverrideOwner carry)")).ToList();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var cands = new List<MethodInfo>();
        try { cands.AddRange(open.GetMethods(flags).Where(m => m.Name == slotName && m.IsVirtual && m.GetParameters().Length == argNodes.Count)); } catch { }
        var win = PickOverrideBase(cands, argNodes, returnNode,
            $"override base={owner}.{slotName}({DescArgs(argNodes)}):{returnNode}");
        // The incoming return describes the implementation's resolved Kotlin/constructed-owner view and is used
        // above to select the slot.  ilemit links against the declaration in the reference assembly, so carry the
        // winner's declared CLR return in the memberRef vocabulary (including positional type vars).
        // The same slot as one scalar identity. The three descriptors above state the base member in pieces —
        // name here, parameters there, owner and return elsewhere — and a MethodImpl target is exactly the
        // place where assembling those pieces back into a member is selection.
        // Keep the semantic instruction independently of the identity it requires. The instruction is the
        // durable trigger ilemit consumes; the reference is its already-selected MethodImpl operand. Keeping
        // those roles distinct lets the CIR gate detect either half being dropped without restoring a second
        // owner/name/signature spelling of the member.
        node["requiresClrOverride"] = true;
        node["clrOverrideRef"] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, ownerSpec.Args);
        // …and the pieces go. They were this resolution's INPUT — they named the slot to look for — and leaving
        // them makes the reference travel beside the thing it replaced, which is how a consumer keeps triggering
        // on the old key and never notices the new one exists.
        node.Remove("pendingOverrideOwner");
        node.Remove("pendingOverrideMember");
        node.Remove("pendingOverrideReturn");
    }

    // Pick the UNIQUE base virtual to override. An override's DECLARED params ARE the base slot's params (that is what
    // "override" means), so this is a STRUCTURAL identity match — NOT the call side's arg-applicability (which would let
    // a scalar `Any` arg "match" an `Any[]` param via the object-downcast rule and make BaseContinuationImpl's
    // `create(Any,Cont)` / `create(Any[],Cont)` ambiguous). Require exactly one after §12.8.10.2 most-derived shadowing;
    // 0 = hard ABI error, >1 = malformed. NEVER a first-pick.
    static MethodInfo PickOverrideBase(List<MethodInfo> cands, List<TypeNode> argNodes, TypeNode returnNode,
        string desc)
    {
        var hits = MostDerived(cands.Where(c => OverrideMatch(c.GetParameters(), argNodes)
            && OverrideParamMatch(returnNode, c.ReturnType)).ToList());
        if (hits.Count == 0) throw NoMatch(desc, cands);
        if (hits.Count == 1) return hits[0];
        throw Malformed(desc, hits);
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
        p = AliasResolve(p);
        if (p.IsGenericParameter) return true;   // substituted at the instantiation — wildcard
        if (p.IsByRef) return a is TypeNode.ByRef b && OverrideParamMatch(b.Of, p.GetElementType());
        if (p.IsArray) return a is TypeNode.Array ar && OverrideParamMatch(ar.Elem, p.GetElementType());
        if (p.IsGenericType && SafeDef(p) == NullableDef())
            return a is TypeNode.Nullable nv && OverrideParamMatch(nv.Of, p.GetGenericArguments()[0]);
        if (a is TypeNode.Nullable nn) return OverrideParamMatch(nn.Of, p);   // reference-nullable arg = same .NET type
        if (p.IsGenericType)   // constructed generic reference/value type -> recurse args structurally
        {
            if (a is not TypeNode.Fqn f || f.Args == null) return false;
            var adef = RefDef(f.Name, f.Args.Length);
            if (adef == null || SafeDef(adef) != SafeDef(p)) return false;
            var pa = p.GetGenericArguments();
            if (pa.Length != f.Args.Length) return false;
            for (int i = 0; i < pa.Length; i++) if (!OverrideParamMatch(f.Args[i], pa[i])) return false;
            return true;
        }
        // Scalar leaf: resolve `a` and require identity, with Kotlin `Any` == `System.Object` as the shared top type.
        var aT = MapMlc(a);
        if (aT == null) return false;
        if (aT == p) return true;
        return IsTopType(aT) && IsTopType(p);
    }

    static bool IsTopType(Type t) { try { return t.FullName is "System.Object" or "kotlin.Any"; } catch { return false; } }
}
