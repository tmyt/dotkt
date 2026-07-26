using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// W1-S2 (#46) — RESOLVED-CLR-IR carry for PLAIN calls (clrStatic/clrInstance) + CONSTRUCTORS (newClr). Runs AFTER
// BirTypeLowering.Lower (Program.cs), so every clr*/newClr node's `type` (owner) + `argTypes` are already in the
// CLR-lowered vocabulary. bir2cir HOLDS the winning member (its MetadataLoadContext) but until now serialized only the
// lossy `argTypes` (declared param types WITHOUT member identity), forcing ilemit to re-run overload resolution
// (arity probes, name+arity first-picks, assignability scoring, ctor-by-arg-count, the interface-owner dynamic-dispatch
// downgrade). This pass makes CIR the RESOLVED IR: it structurally matches the callee's DECLARED param types (kotc's
// FIR-resolved `sig`, carried as `argTypes`) against the owner's same-name members in the MLC, requires a UNIQUE winner,
// and stamps the winner's canonical DECLARED params as `memberSig` (+ `dispatch` on clrInstance), deleting `argTypes`.
// ilemit then LINKS exactly one handle (0 = hard ABI error / >1 = malformed), never picking.
//
// Mirrors the S1 generic-CALL matcher (Emitter.Resolve.cs ResolveGenericMethod) — its DUAL for the CONSTRUCTION case:
// the owner is resolved on its OPEN definition, and `memberSig` keeps a class type-var as a positional `tv(type,i)`.
// So `List<E>` (generic stdlib) / `HashSet<EmittedType>` need NO MakeGenericType over an open/local type-arg; the
// tv-vs-owner-instantiation bridge (the `ownerArgs` TypeNodes) resolves the concrete/open owner args positionally,
// exactly as ilemit's GenericParamMatches `ownerArgs` branch does with reflected Types.
static partial class ClrMemberResolution
{
    static readonly Dictionary<JsonObject, JsonArray> KotlinSigSnapshots =
        new(ReferenceEqualityComparer.Instance);
    static ReferenceMetadataIndex _refs;
    static IReadOnlySet<string> _localEnums = new HashSet<string>();

    // `localEnums` = every LOCAL `kind:"enum"` FQN in this compilation (the self-build's own enums — in an APP build a
    // stdlib enum like RegexOption is in the ref.dll and resolves concretely, never via the enum-reinterpret fallback).
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, IReadOnlySet<string> localEnums)
    {
        _refs = refs;
        _localEnums = localEnums ?? new HashSet<string>();
        ResolveExternalClassOverrides(root);
        Walk(root);
    }

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);
            Resolve(obj);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList()) if (item != null) Walk(item);
        }
    }

    static void Resolve(JsonObject node)
    {
        // W1-S4 (#46/#183): a method DECLARATION overriding a .NET base-CLASS virtual (accessor) carries `clrOverride`
        // (the base owner FQN, from DeclarationRename) but no `k` — resolve its base virtual off the ref.dll and carry
        // `clrOverrideSig` so ilemit links the exact base slot (no name-only first-pick).
        if (node["clrOverride"] != null) ResolveOverrideBase(node);
        switch ((node["k"] as JsonValue)?.GetValue<string>())
        {
            case "newClr": ResolveCtor(node); break;
            case "clrStatic": ResolveCall(node, instance: false); break;
            case "clrInstance": ResolveCall(node, instance: true); break;
            case "newBoundClrDelegate": ResolveBoundClrDelegate(node); break;
            case "clrPropGet": ResolveProp(node, write: false); break;
            case "clrPropSet": ResolveProp(node, write: true); break;
            case "clrEventAdd": ResolveEvent(node); break;
            case "clrEventRemove": ResolveEvent(node); break;
            case "field": ResolveFieldAccess(node, write: false); break;
            case "setFieldExpr": ResolveFieldAccess(node, write: true); break;
            case "setField": ResolveFieldAccess(node, write: true); break;
        }
    }

    // A plain top-level/static Kotlin call into a referenced assembly is already attributed to its file class by
    // MemberCallSubstitution (`owner`) or the frontend provenance carry (`calleeOwner`). Replace the frontend call-site
    // `sig` with the referenced declaration's physical signature so ilemit links that exact slot. This is the plain-
    // call counterpart of `memberSig` on clr* nodes: no CLR policy is inferred by ilemit, and no arity fallback is
    // needed. Local same-assembly calls are absent from the reference index and remain unchanged.
    public static void ResolveReferencedStaticCalls(JsonNode root, ReferenceMetadataIndex refs)
    {
        _refs = refs;
        WalkReferencedStaticCalls(root);
        DropKotlinSigSnapshots(root);
    }

    // Nullable-generic/function erasure runs before top-level owner attribution. Preserve the frontend-resolved
    // descriptor out-of-band across those transforms; no temporary compiler field enters BIR or CIR.
    public static void CaptureReferencedStaticCallSignatures(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if ((obj["k"] as JsonValue)?.GetValue<string>() == "callStatic"
                && obj["sig"] is JsonArray sig && !KotlinSigSnapshots.ContainsKey(obj))
                KotlinSigSnapshots.Add(obj, (JsonArray)sig.DeepClone());
            foreach (var kv in obj.ToList())
                if (kv.Value != null) CaptureReferencedStaticCallSignatures(kv.Value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList())
                if (item != null) CaptureReferencedStaticCallSignatures(item);
        }
    }

    static void WalkReferencedStaticCalls(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
                if (kv.Value != null) WalkReferencedStaticCalls(kv.Value);
            if ((obj["k"] as JsonValue)?.GetValue<string>() == "callStatic")
                ResolveReferencedStaticCall(obj);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList())
                if (item != null) WalkReferencedStaticCalls(item);
        }
    }

    static void ResolveReferencedStaticCall(JsonObject node)
    {
        var ownerNode = node["owner"] is JsonNode owner && owner.GetValueKind() != System.Text.Json.JsonValueKind.Null
            ? owner
            : node["calleeOwner"];
        if (ReadOwnerNode(ownerNode) is not TypeNode.Fqn ownerFqn
            || (node["method"] as JsonValue)?.TryGetValue<string>(out var name) != true
            || node["sig"] is not JsonArray sig)
            return;
        var selectionSig = sig;
        if (KotlinSigSnapshots.TryGetValue(node, out var snapshot))
            selectionSig = snapshot;
        var callSig = selectionSig.Select(TypeJson.Read).Where(t => t != null).ToArray();
        if (callSig.Length != selectionSig.Count) return;
        var methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        if (!_refs.TryResolveStaticMemberSignature(
                ownerFqn.Name, name, methodArity, callSig, out var declarationSig))
            return;
        node["sig"] = new JsonArray(declarationSig.Select(TypeJson.Write).ToArray());
    }

    static void DropKotlinSigSnapshots(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            KotlinSigSnapshots.Remove(obj);
            foreach (var kv in obj.ToList())
                if (kv.Value != null) DropKotlinSigSnapshots(kv.Value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr.ToList())
                if (item != null) DropKotlinSigSnapshots(item);
        }
    }

    // ---- constructors --------------------------------------------------------------------------

    static void ResolveCtor(JsonObject node)
    {
        if (ReadOwnerNode(node["type"]) is not TypeNode.Fqn ownerFqn) return;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException($"bir2cir: newClr owner '{ownerFqn.Name}' does not resolve to a .NET type (#46 memberRef carry)");
        var argNodes = ReadArgTypes(node);
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == argNodes.Count).ToList();
        var win = PickUnique(ctors, c => c.GetParameters(), argNodes, ownerFqn.Args,
            $"newClr owner={TypeNode.ToJson(ownerFqn)} ({DescArgs(argNodes)})");
        node["memberSig"] = MemberSig(win.GetParameters());
        node.Remove("argTypes");
    }

    // ---- plain static / instance calls ---------------------------------------------------------

    static void ResolveCall(JsonObject node, bool instance)
    {
        var ownerNode = ReadOwnerNode(node["type"]);
        TypeNode.Fqn ownerFqn;
        Type open;
        // An ARRAY-owner instance method (`Array<T>.clone()` -> `System.Array.Clone`): every CLR array IS a
        // `System.Array`, so retarget the owner to it (the receiver `T[]` is assignable, no cast needed). Its `Clone`
        // etc. live on System.Array, not on the erased element type.
        if (instance && ownerNode is TypeNode.Array)
        {
            open = SystemArrayMlc();
            if (open == null) throw new InvalidOperationException("bir2cir: clrInstance array-owner method could not resolve System.Array (#46)");
            ownerFqn = new TypeNode.Fqn("System.Array");
            node["type"] = TypeJson.Write(ownerFqn);
        }
        else if (ownerNode is TypeNode.Fqn f)
        {
            ownerFqn = f;
            open = ResolveOwnerType(f);
            if (open == null)
                throw new InvalidOperationException($"bir2cir: {(instance ? "clrInstance" : "clrStatic")} owner '{ownerFqn.Name}' does not resolve to a .NET type (#46 memberRef carry)");
        }
        else return;
        var name = (node["method"] as JsonValue)?.GetValue<string>();
        var argNodes = ReadArgTypes(node);
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        var cands = Candidates(open, name, argNodes, ownerFqn.Args, flags);
        // WITHOUT `typeArgs`, exclude generic-method DEFINITIONS so `Task.fromException` binds the non-generic
        // `Task FromException(Exception)`, not `Task<T> FromException<T>(Exception)` (no inferable T). WITH `typeArgs`
        // keep BOTH kinds: a generic Kotlin @ClrIntrinsic (`arrayCopy<T>`) can bind a NON-generic BCL method
        // (`Array.Copy(Array,…)`) OR a generic one (`Array.Fill<T>`) — the structural param match then disambiguates.
        bool hasTypeArgs = node["typeArgs"] is JsonArray ta && ta.Count > 0;
        if (!hasTypeArgs) cands = cands.Where(m => !m.IsGenericMethodDefinition).ToList();
        MethodInfo win;
        try
        {
            win = PickUnique(cands, m => m.GetParameters(), argNodes, ownerFqn.Args,
                $"{(instance ? "clrInstance" : "clrStatic")} owner={TypeNode.ToJson(ownerFqn)} .{name}({DescArgs(argNodes)})");
        }
        // Interface-owner miss -> a DELIBERATE dynamic-dispatch node (the runtime value implements the BCL slot under a
        // different concrete type). Replaces ilemit EmitClrCall's SILENT runtime-reflection downgrade — now greppable.
        // GATED to a genuine "no such member": the name+arity does NOT exist on the owner or its base interfaces (the
        // legitimate lowercase-Kotlin `removeAll`/`addAll` case). A NON-empty candidate set that merely failed to match
        // is an ABI mismatch / matcher gap and RE-THROWS the hard error — never silently downgraded (the very class
        // W1-S2 deletes; the new matcher being stricter must NOT re-create it one layer up).
        catch (InvalidOperationException) when (instance && open.IsInterface && node["recv"] != null && cands.Count == 0)
        {
            node["k"] = "clrDynInstance";
            node.Remove("argTypes");
            return;
        }
        node["memberSig"] = MemberSig(win.GetParameters());
        node.Remove("argTypes");
        if (instance)
            node["dispatch"] = Dispatch(win, open, (node["super"] as JsonValue)?.GetValue<bool>() ?? false);
    }

    // ---- bound .NET method-reference (newBoundClrDelegate) --------------------------------------

    // W1-S5 (#46/#183) — RESOLVED-CLR-IR carry for a BOUND .NET method-reference (`netObj::method`, produced by
    // NetInteropBinding.ReshapeBoundDelegate). The target is ALWAYS a public INSTANCE method on the owner `clrType`
    // (Codex-confirmed: the bound receiver comes from an IR dispatch receiver — statics have none, extensions are
    // excluded). Until now ilemit resolved it with `type.GetMethod(name, argTypes) ?? type.GetMethod(name)` — a
    // name+params match with a NAME-ONLY first-pick fallback (exactly the class #46 removes). This carries the winning
    // method's DECLARED param signature as `memberSig` so ilemit LINKS the unique target (0 = hard ABI error, >1 =
    // malformed). The ldftn-vs-ldvirtftn choice stays driven by the node's existing `virtual` field — memberSig only
    // identifies the overload, so no `dispatch` is needed.
    static void ResolveBoundClrDelegate(JsonObject node)
    {
        if (ReadOwnerNode(node["clrType"]) is not TypeNode.Fqn ownerFqn) return;
        var open = ResolveOwnerType(ownerFqn);
        if (open == null)
            throw new InvalidOperationException($"bir2cir: newBoundClrDelegate owner '{ownerFqn.Name}' does not resolve to a .NET type (#46/#183 memberSig carry)");
        var name = (node["method"] as JsonValue)?.GetValue<string>();
        var argNodes = ReadArgTypes(node);
        // A bound method-ref carries no `typeArgs` (no `netObj::method<T>` form in the corpus) — exclude generic-method
        // DEFINITIONS, mirroring ResolveCall's no-typeArgs branch; a generic target would fail loud here (greppable).
        var cands = Candidates(open, name, argNodes, ownerFqn.Args, BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsGenericMethodDefinition).ToList();
        var win = PickUnique(cands, m => m.GetParameters(), argNodes, ownerFqn.Args,
            $"newBoundClrDelegate owner={TypeNode.ToJson(ownerFqn)} .{name}({DescArgs(argNodes)})");
        node["memberSig"] = MemberSig(win.GetParameters());
        node.Remove("argTypes");
    }

    // ---- shared resolution ---------------------------------------------------------------------

    // Resolve the owner's OPEN definition off the ref.dll — via ResolveRefType, which (unlike ResolveNetType) does NOT
    // skip `kotlin.*`, so a stdlib-owner clr* node (`kotlin.collections.Iterator.next()`, kept by IteratorConsumer-
    // Normalization for the rt-stdlib link) resolves its declared member sig here too. RESPECTS generic arity: a generic
    // owner (args present) binds the arity-suffixed def (`TaskCompletionSource`1`), never a same-named NON-generic sibling.
    static Type ResolveOwnerType(TypeNode.Fqn ownerFqn)
    {
        // A NESTED-generic reflection name already carries backtick arity + `+` separators (`Outer`1+Nested`, the
        // ConfigureAwait awaiter) — resolve it VERBATIM; BareOwnerFqn/StripGenericArity would truncate at the first
        // backtick and lose the nested type. (Its `args` instantiate the OUTER; a member whose sig has no outer type-var
        // — OnCompleted(Action) — matches on the open nested def regardless, so no MakeGenericType is needed.)
        if (ownerFqn.Name.Contains('`')) return _refs.ResolveRefType(ownerFqn.Name, 0);
        return RefDef(ReferenceMetadataIndex.BareOwnerFqn(ownerFqn.Name), ownerFqn.Args?.Length ?? 0);
    }

    // The owner type slot as a TypeNode: a structured `{t:…}` node, OR a LEGACY bare-STRING owner (kotc emits some clr*
    // owners — a `__mref` forwarder's `str(clrOwner)`, a referenced file class, the await marker — as a plain string).
    static TypeNode ReadOwnerNode(JsonNode typeSlot)
    {
        if (TypeJson.Read(typeSlot) is TypeNode t) return t;
        if (typeSlot is JsonValue v && v.TryGetValue<string>(out var s) && s != null) return new TypeNode.Fqn(s);
        return null;
    }

    static List<TypeNode> ReadArgTypes(JsonObject node) =>
        (node["argTypes"] as JsonArray)?.Where(x => x != null).Select(TypeJson.Read).ToList() ?? new List<TypeNode>();

    // Pick the UNIQUE member whose declared params match `argNodes`: Tier 1 all-exact; Tier 2 all-applicable (def-level
    // assignability, shallow — mirrors ilemit PickOpenCtor.ParamAccepts); Tier 3 fewest-`object` strict-min-unique.
    // 0 = hard ABI error, >1 = malformed. NEVER a first-pick.
    static T PickUnique<T>(List<T> cands, Func<T, ParameterInfo[]> paramsOf, List<TypeNode> argNodes,
                           TypeNode[] ownerArgs, string desc) where T : MethodBase
    {
        var scored = cands.Select(c => (c, m: Match(paramsOf(c), argNodes, ownerArgs))).Where(x => x.m != MatchKind.No).ToList();
        var exact = MostDerived(scored.Where(x => x.m == MatchKind.Exact).Select(x => x.c).ToList());
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw Malformed(desc, exact);
        var appl = MostDerived(scored.Select(x => x.c).ToList());
        if (appl.Count == 1) return appl[0];
        if (appl.Count == 0) throw NoMatch(desc, cands);
        // Tier 3 — most-specific: fewest `object` params, strict-min-unique (parity with C#'s "better function member"
        // falling out of specificity: a non-`object` param beats `object`). A tie is a HARD malformed error, never a pick.
        var ranked = appl.Select(c => (c, obj: paramsOf(c).Count(p => IsObjectMlc(p.ParameterType)))).ToList();
        var min = ranked.Min(x => x.obj);
        var best = ranked.Where(x => x.obj == min).Select(x => x.c).ToList();
        if (best.Count == 1) return best[0];
        throw Malformed(desc, best);
    }

    // C#'s "most-derived declaring type wins" (§12.8.10.2): discard a candidate whose declaring type is a STRICT BASE of
    // another candidate's — a base CLASS (`Task<T>.GetAwaiter()` on Task`1 beats the inherited `Task.GetAwaiter()` on the
    // base Task) OR a base INTERFACE (`IEnumerable<T>.GetEnumerator()` beats `IEnumerable.GetEnumerator()`). memberSig
    // (params) can't distinguish the return-only difference, so this shadowing rule is what makes the winner unique.
    static List<T> MostDerived<T>(List<T> hits) where T : MethodBase
    {
        if (hits.Count > 1) hits = hits.GroupBy(m => (m.Module, m.MetadataToken)).Select(g => g.First()).ToList();   // dedupe reflection duplicates
        if (hits.Count <= 1) return hits;
        return hits.Where(h => !hits.Any(o => !ReferenceEquals(o, h) && !SameDeclType(h.DeclaringType, o.DeclaringType)
            && (IsStrictBase(h.DeclaringType, o.DeclaringType) || IfaceImplementedBy(h.DeclaringType, o.DeclaringType)))).ToList();
    }

    static bool SameDeclType(Type a, Type b) => ReferenceEquals(a, b) || (a != null && b != null && SafeDef(a) == SafeDef(b));
    // True iff `baseT` is a STRICT base class of `derived` (walk `derived`'s base-CLASS chain; generic-def aware).
    static bool IsStrictBase(Type baseT, Type derived)
    {
        if (baseT == null || derived == null) return false;
        try { for (var t = derived.BaseType; t != null; t = t.BaseType) if (SafeDef(t) == SafeDef(baseT)) return true; } catch { }
        return false;
    }
    // True iff `baseIface` is a base INTERFACE of `derived` (derived implements/extends it) — the interface twin of IsStrictBase.
    static bool IfaceImplementedBy(Type baseIface, Type derived)
    {
        if (baseIface == null || derived == null || !baseIface.IsInterface) return false;
        try { return derived.GetInterfaces().Any(i => SafeDef(i) == SafeDef(baseIface)); } catch { return false; }
    }

    enum MatchKind { No, Assignable, Exact }

    static MatchKind Match(ParameterInfo[] ps, List<TypeNode> argNodes, TypeNode[] ownerArgs)
    {
        if (ps.Length != argNodes.Count) return MatchKind.No;
        var acc = MatchKind.Exact;
        for (int i = 0; i < ps.Length; i++)
        {
            var m = Applies(argNodes[i], ps[i].ParameterType, ownerArgs);
            if (m == MatchKind.No) return MatchKind.No;
            if (m == MatchKind.Assignable) acc = MatchKind.Assignable;
        }
        return acc;
    }

    // Applicability of a DECLARED arg TypeNode `a` (from kotc's FIR-resolved `sig`) to a candidate OPEN-def param `p`.
    // `p` is ALIAS-RESOLVED first (a ref.dll member param typed as a @ClrTypeAlias — `kotlin.clr.TaskCompletionSource<T>`
    // — is compared/emitted as its BCL twin `System.Threading.Tasks.TaskCompletionSource<T>`, matching the lowered arg).
    //   CONCRETE param -> the C#-binder LEAF rule: resolve the arg to an MLC Type, exact-identity or IsAssignableFrom
    //     (so `sbyte[]` binds `Sort(System.Array)`); an UNRESOLVABLE arg (a local/synthetic ref such as
    //     `dotkt$CharSequence`, or a function-type) binds only `object` (+ an arity-matching delegate param for a function
    //     arg) — the deterministic form of ilemit's former object-steering, never a first-pick.
    //   OPEN param (a type-var, or a constructed generic mentioning one) -> STRUCTURAL match under positional-tv
    //     equality: a class-var param at position i is satisfied by the DECLARED `tv(type,i)` (a method call carries the
    //     callee-owner's own class var) OR by the arg matching `ownerArgs[i]` (a ctor carries the SUBSTITUTED concrete
    //     arg — `RootContinuation<Int>(TaskCompletionSource<Int>)`); a method tv by `tv(method,i)`; a constructed generic
    //     recurses, with a shallow def-derivation assignability (IReadOnlyCollection<E> -> IEnumerable<E>).
    static MatchKind Applies(TypeNode a, Type p, TypeNode[] ownerArgs)
    {
        if (a is TypeNode.Oblivious ob) return Applies(ob.Of, p, ownerArgs);
        p = AliasResolve(p);
        if (!p.IsGenericParameter && !p.ContainsGenericParameters)
        {
            var aT = MapMlc(a);
            if (aT != null)
            {
                if (aT == p) return MatchKind.Exact;
                try { if (p.IsAssignableFrom(aT)) return MatchKind.Assignable; } catch { }
                // The DECLARED arg is the erased top type `object` (kotlin.Any) — the runtime value is really a subtype,
                // so a REFERENCE param accepts it via an implicit downcast (emitted as a castclass). Mirrors the old
                // object-arg acceptance (`fun createArray(cls: Any, ...)` binding to `Array.CreateInstance(Type,...)`).
                if (IsObjectMlc(aT) && !p.IsValueType) return MatchKind.Assignable;
                // Kotlin's primitive DUAL-REPRESENTATION: a signed integer and its SAME-WIDTH unsigned twin share the
                // bit pattern (Long==ULong, Int==UInt, …), so a @ClrIntrinsic bit-op binds `Long.countLeadingZeroBits()`
                // to `BitOperations.LeadingZeroCount(UInt64)` — the arg reinterprets. Same-width only (never Int64->UInt32).
                if (SameWidthIntegral(aT, p)) return MatchKind.Assignable;
                return MatchKind.No;
            }
            if (a is TypeNode.Fn fn) return MatchFnToDelegate(fn, p, ownerArgs);
            // An array with an UNRESOLVABLE element (`Array<T>`, T a type-var) still IS a System.Array/object and
            // implements the non-generic array interfaces — `System.Array` assignable to `p` means `T[]` is too (e.g.
            // the generic `arrayCopy(Array<T>,...)` binding to `Array.Copy(System.Array,...)`).
            if (a is TypeNode.Array)
            {
                var sysArr = SystemArrayMlc();
                if (sysArr != null) { try { if (p.IsAssignableFrom(sysArr)) return MatchKind.Assignable; } catch { } }
            }
            // A LOCAL Kotlin ENUM (a self-build `kind:"enum"` the MLC can't see) — bare or wrapped in a collection/array
            // — reinterprets to a .NET enum param: `RegexOption` -> `.ctor(String, RegexOptions)`, and `Set<RegexOption>`
            // -> the OR'd `RegexOptions` (`new Regex(pattern, options)`). GATED to an arg that MENTIONS a known local enum
            // so an arbitrary unresolvable arg (a local class, a `dotkt$` synthetic) does NOT slip into an enum param.
            if (IsEnumMlc(p) && MentionsLocalEnum(a)) return MatchKind.Assignable;
            return IsObjectMlc(p) ? MatchKind.Assignable : MatchKind.No;
        }
        // p is OPEN (a generic parameter or a constructed generic mentioning one).
        if (p.IsGenericParameter)
        {
            int i = p.GenericParameterPosition;
            if (p.DeclaringMethod != null)
                return a is TypeNode.Tv { Scope: "method" } mtv && mtv.I == i ? MatchKind.Exact : MatchKind.No;
            // class type-var: (a) DECLARED — the arg is the owner's own class var at position i (a method call); OR
            // (b) SUBSTITUTED — the arg matches the owner's instantiation arg at position i (a ctor's concrete param).
            if (a is TypeNode.Tv { Scope: "type" } ttv && ttv.I == i) return MatchKind.Exact;
            if (ownerArgs != null && i >= 0 && i < ownerArgs.Length && ownerArgs[i] != null) return NodeEq(a, ownerArgs[i]);
            return MatchKind.No;
        }
        if (p.IsByRef) return a is TypeNode.ByRef b ? Applies(b.Of, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (p.IsArray) return a is TypeNode.Array ar ? Applies(ar.Elem, p.GetElementType(), ownerArgs) : MatchKind.No;
        if (p.IsGenericType && SafeDef(p) == NullableDef())
            return a is TypeNode.Nullable nv ? Applies(nv.Of, p.GetGenericArguments()[0], ownerArgs) : MatchKind.No;
        if (a is TypeNode.Nullable nn) return Applies(nn.Of, p, ownerArgs);   // nullable ref arg = same .NET type
        if (a is TypeNode.Fn fnOpen) return MatchFnToDelegate(fnOpen, p, ownerArgs);     // a lambda arg binding an OPEN delegate param (`ThreadLocal<T>(Func<T>)`)
        if (a is not TypeNode.Fqn f) return MatchKind.No;
        var pdef = SafeDef(p);
        var adef = RefDef(ReferenceMetadataIndex.BareOwnerFqn(f.Name), f.Args?.Length ?? 0);
        if (adef == null) return MatchKind.No;
        var adefDef = SafeDef(adef);
        if (adefDef == pdef)
        {
            if (f.Args == null) return MatchKind.Exact;
            var pa = p.GetGenericArguments();
            if (pa.Length != f.Args.Length) return MatchKind.No;
            var acc = MatchKind.Exact;
            for (int i = 0; i < pa.Length; i++)
            {
                var m = Applies(f.Args[i], pa[i], ownerArgs);
                if (m == MatchKind.No) return MatchKind.No;
                if (m == MatchKind.Assignable) acc = MatchKind.Assignable;
            }
            return acc;
        }
        try { if (adefDef.GetInterfaces().Any(i => SafeDef(AliasResolve(i)) == pdef)) return MatchKind.Assignable; } catch { }
        return MatchKind.No;
    }

    // Structural equality of an arg TypeNode against the owner's instantiation-arg TypeNode (the ctor SUBSTITUTED-param
    // bridge): both resolve to the same MLC Type (exact), or the arg is assignable to the instantiation arg.
    static MatchKind NodeEq(TypeNode a, TypeNode ownerArg)
    {
        if (a == ownerArg) return MatchKind.Exact;
        // A `Nullable<value>` arg (`Int?`) binds the underlying value owner-arg (`T`=Int): the value-nullable GENERIC
        // erasure (#128) — `IComparer<Int>.Compare(x: Int?, y: Int?)` binds the constructed `Compare(Int,Int)`.
        if (a is TypeNode.Nullable an && NodeEq(an.Of, ownerArg) != MatchKind.No) return MatchKind.Assignable;
        var aT = MapMlc(a); var oT = MapMlc(ownerArg);
        if (aT != null && oT != null)
        {
            if (aT == oT) return MatchKind.Exact;
            try { if (oT.IsAssignableFrom(aT)) return MatchKind.Assignable; } catch { }
        }
        // The arg is the erased top type `object` (kotlin.Any) — in a GENERIC erasure it IS the owner-arg boxed/as-is
        // (a reference class, an unresolvable LOCAL class, OR a boxed value type unboxed at emit): `Comparable<Ver>.
        // compareTo(other:object)` binds `CompareTo(Ver)`, `IComparer<Int>.Compare(object,object)` binds `Compare(Int,Int)`.
        if (a is TypeNode.Fqn { Name: "object" or "System.Object", Args: null }) return MatchKind.Assignable;
        return MatchKind.No;
    }

    // Resolve a ref.dll-reflected type through the @ClrTypeAlias index to its BCL-MLC twin (a member param/return typed
    // `kotlin.clr.TaskCompletionSource<T>` -> `System.Threading.Tasks.TaskCompletionSource<T>`), recursively over generic
    // args / element types; a generic PARAMETER and a non-aliased type are returned unchanged.
    static Type AliasResolve(Type t)
    {
        if (t == null || t.IsGenericParameter) return t;
        if (t.IsArray) { var e = AliasResolve(t.GetElementType()); return ReferenceEquals(e, t.GetElementType()) ? t : e.MakeArrayType(); }
        if (t.IsByRef) { var e = AliasResolve(t.GetElementType()); return ReferenceEquals(e, t.GetElementType()) ? t : e.MakeByRefType(); }
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var rdef = AliasDef(def);
            var args = t.GetGenericArguments().Select(AliasResolve).ToArray();
            if (rdef == null && args.Zip(t.GetGenericArguments(), ReferenceEquals).All(x => x)) return t;
            try { return (rdef ?? def).MakeGenericType(args); } catch { return t; }
        }
        return AliasDef(t) ?? t;
    }

    // The BCL-MLC type an @ClrTypeAlias def maps to, or null when the def is not aliased.
    static Type AliasDef(Type def)
    {
        var name = StripArity(Dotted(def.FullName ?? def.Name));
        return _refs.Aliases.TryGetValue(name, out var bcl)
            ? RefDef(bcl, def.IsGenericType ? def.GetGenericArguments().Length : 0)
            : null;
    }

    // Resolve a .NET type by name off the ref.dll, RESPECTING generic arity: probe the arity-suffixed def (`Foo`1`)
    // FIRST when arity>0, so a same-named NON-generic sibling (`TaskCompletionSource`/the `System.Nullable` static class)
    // never shadows the generic def (ResolveRefType/ResolveNetType probe the bare name first).
    static Type RefDef(string bare, int arity)
    {
        if (arity > 0 && !bare.Contains('`') && _refs.ResolveRefType(bare + "`" + arity, arity) is { } g) return g;
        return _refs.ResolveRefType(bare, arity);
    }

    // A concrete arg TypeNode -> its MLC Type (null when it embeds an open tv / a local-emitted / a delegate / can't construct).
    static Type MapMlc(TypeNode t)
    {
        switch (t)
        {
            case TypeNode.Oblivious o: return MapMlc(o.Of);
            case TypeNode.ByRef b: { var e = MapMlc(b.Of); return e?.MakeByRefType(); }
            case TypeNode.Array a: { var e = MapMlc(a.Elem); return e?.MakeArrayType(); }
            case TypeNode.Nullable n:
            {
                var inner = MapMlc(n.Of);
                if (inner == null) return null;
                if (!inner.IsValueType) return inner;
                try { return NullableDef()?.MakeGenericType(inner); } catch { return null; }
            }
            case TypeNode.Tv: return null;
            case TypeNode.Fn: return null;
            case TypeNode.Fqn f:
            {
                var baseT = RefDef(ReferenceMetadataIndex.BareOwnerFqn(f.Name), f.Args?.Length ?? 0);
                if (baseT == null) return null;
                if (f.Args == null || f.Args.Length == 0) return baseT;
                if (!baseT.IsGenericTypeDefinition) return baseT;
                var margs = f.Args.Select(MapMlc).ToArray();
                if (margs.Any(x => x == null)) return null;
                try { return baseT.MakeGenericType(margs); } catch { return null; }
            }
        }
        return null;
    }

    static Type _nullableDef;
    static Type NullableDef() => _nullableDef ??= RefDef("System.Nullable", 1);
    static Type _sysArr;
    // This is an internal ABI-resolution probe, not a NetInterop ownership decision. Use ResolveRefType so DotKt
    // declaration ownership filters cannot hide the CLR root array type while matching `T[]` to Array.Copy(Array,...).
    static Type SystemArrayMlc() => _sysArr ??= _refs.ResolveRefType("System.Array");

    // The candidate set for `name`, PREFERRING the owner's OWN declared members over inherited base-INTERFACE members
    // (C#'s "most-derived declaring type wins" — §12.8.10.2): reflection's GetMethods already surfaces inherited CLASS
    // members, but for an INTERFACE owner the base-interface slots are only reached via GetInterfaces, and adding them
    // unconditionally makes `IEnumerable<T>.GetEnumerator()` ambiguous with the inherited non-generic
    // `IEnumerable.GetEnumerator()` (memberSig = [] can't distinguish return-type-differentiated slots). So the
    // base-interface members are a FALLBACK, consulted only when NO own member of that name+arity is applicable.
    static List<MethodInfo> Candidates(Type open, string name, List<TypeNode> argNodes, TypeNode[] ownerArgs, BindingFlags flags)
    {
        var own = new List<MethodInfo>();
        try { own.AddRange(open.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == argNodes.Count)); } catch { }
        if (!open.IsInterface || own.Any(m => Match(m.GetParameters(), argNodes, ownerArgs) != MatchKind.No)) return own;
        var withBases = new List<MethodInfo>(own);
        foreach (var bi in SafeInterfaces(open))
            try { withBases.AddRange(bi.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == argNodes.Count)); } catch { }
        return withBases;
    }

    static Type[] SafeInterfaces(Type t) { try { return t.GetInterfaces(); } catch { return Array.Empty<Type>(); } }

    // Kotlin's signed<->unsigned SAME-WIDTH integral TWIN (identical bit pattern; a @ClrIntrinsic bit-op reinterprets,
    // `Long.countLeadingZeroBits()` -> `BitOperations.LeadingZeroCount(UInt64)`). `Char` is DELIBERATELY excluded (it is
    // a distinct Kotlin type with explicit `.code`/`.toChar()` conversions — reinterpreting a Short into a `char` param
    // would silently print "A" for 65). Only the exact signed/unsigned pairs qualify.
    static bool SameWidthIntegral(Type a, Type b)
    {
        static string W(Type t) => (t.FullName ?? t.Name) switch
        {
            "System.SByte" or "System.Byte" => "8",
            "System.Int16" or "System.UInt16" => "16",
            "System.Int32" or "System.UInt32" => "32",
            "System.Int64" or "System.UInt64" => "64",
            "System.IntPtr" or "System.UIntPtr" => "n",
            _ => null,
        };
        var wa = W(a); return wa != null && wa == W(b);
    }
    static Type SafeDef(Type t) { try { return t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; } catch { return t; } }
    static bool IsObjectMlc(Type t) { try { return t.FullName == "System.Object"; } catch { return false; } }
    static bool IsEnumMlc(Type t) { try { return t.IsEnum; } catch { return false; } }
    // True iff the arg TypeNode is (or wraps, in a collection/array/nullable) a KNOWN local enum FQN.
    static bool MentionsLocalEnum(TypeNode t) => t switch
    {
        TypeNode.Fqn f => _localEnums.Contains(f.Name) || (f.Args?.Any(MentionsLocalEnum) ?? false),
        TypeNode.Array a => MentionsLocalEnum(a.Elem),
        TypeNode.Nullable n => MentionsLocalEnum(n.Of),
        TypeNode.Oblivious o => MentionsLocalEnum(o.Of),
        _ => false,
    };
    static bool IsVoidNode(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" or "kotlin.Unit" };

    // A function-type arg binds an `object` param OR a delegate param (BCL Func/Action or a stdlib delegate, possibly
    // OPEN — `Func<T>`). Match arity + void-ness, and each param/return STRUCTURALLY — but ONLY reject on a genuine
    // mismatch of RESOLVABLE types (`(Int)->Unit` must NOT bind `Action<string>`). An UNRESOLVABLE lambda side (a local/
    // kotlin.* type — `(MatchResult)->CharSequence` binding a facadegen `MatchEvaluator(System.Text..Match)`) is a
    // WILDCARD (the delegate mapping bridges the Kotlin↔BCL types). A lambda->delegate is a CONVERSION -> Assignable.
    static MatchKind MatchFnToDelegate(TypeNode.Fn fn, Type p, TypeNode[] ownerArgs)
    {
        if (IsObjectMlc(p)) return MatchKind.Assignable;
        if (!IsDelegateType(p)) return MatchKind.No;
        var targetFamily = DelegateFamily(p);
        if (targetFamily != null && fn.Clr != targetFamily) return MatchKind.No;
        MethodInfo invoke; try { invoke = p.GetMethod("Invoke"); } catch { return MatchKind.No; }
        if (invoke == null) return MatchKind.No;
        var ips = invoke.GetParameters();
        var dp = fn.DelegateParams;
        if (ips.Length != dp.Length) return MatchKind.No;
        bool retVoid; try { retVoid = invoke.ReturnType.FullName is "System.Void" or "kotlin.Unit"; } catch { retVoid = false; }
        if (retVoid != IsVoidNode(fn.Ret)) return MatchKind.No;
        for (int i = 0; i < dp.Length; i++) if (Incompatible(dp[i], ips[i].ParameterType, ownerArgs)) return MatchKind.No;
        if (!retVoid && Incompatible(fn.Ret, invoke.ReturnType, ownerArgs)) return MatchKind.No;
        return MatchKind.Assignable;
    }

    // A RESOLVABLE lambda side that structurally fails against the delegate's Invoke type — a genuine element mismatch.
    // An unresolvable side (MapMlc null) is a wildcard (No verdict), so the facadegen Kotlin↔BCL delegate bridge passes.
    static bool Incompatible(TypeNode side, Type invokeType, TypeNode[] ownerArgs) =>
        MapMlc(side) != null && Applies(side, invokeType, ownerArgs) == MatchKind.No;
    static bool IsDelegateType(Type t)
    {
        try { for (var c = t; c != null; c = c.BaseType) if (c.FullName == "System.MulticastDelegate") return true; } catch { }
        return false;
    }

    // dispatch (clrInstance): mirrors ilemit EmitInstanceCall (Bodies.cs). All facts are MLC-readable off the resolved
    // MethodInfo + the owner value-type-ness. constraintType is redundant (== the owner `type` slot), so NOT carried.
    // INVARIANT: IsVirtual/IsFinal are read off the REF.dll member while ilemit links the RT member (same-source builds
    // keep them identical); an rt-side `sealed`/virtuality change therefore requires a matching ref rebuild.
    //   super (non-virtual base slot, ref receiver) -> call ; non-virtual -> call ; virtual ref receiver -> callvirt ;
    //   virtual FINAL on a value type -> call ; virtual non-final inherited by a value type -> constrained.callvirt.
    static string Dispatch(MethodInfo mi, Type owner, bool superCall)
    {
        bool valueOwner; try { valueOwner = owner.IsValueType; } catch { valueOwner = false; }
        bool isVirtual; try { isVirtual = mi.IsVirtual; } catch { isVirtual = false; }
        bool isFinal; try { isFinal = mi.IsFinal; } catch { isFinal = false; }
        if (superCall && !valueOwner) return "call";
        if (!isVirtual) return "call";
        if (!valueOwner) return "callvirt";
        if (isFinal) return "call";
        return "constrained";
    }

    // ---- memberSig (winning member params -> lowered TypeNode array) ---------------------------

    static JsonArray MemberSig(ParameterInfo[] ps)
    {
        var arr = new JsonArray();
        foreach (var p in ps) arr.Add(TypeJson.Write(MemberSigOf(p.ParameterType)));
        return arr;
    }

    // A resolved OPEN-def member's param Type -> its declared-param TypeNode in the CLR-lowered vocabulary (BCL FullName
    // spellings, matching S1's lowered memberSig). A class/method generic param -> a positional tv; a delegate keeps its
    // concrete Fqn (unlike TypeNodeOf, which drops delegates) so ilemit can link the exact slot.
    static TypeNode MemberSigOf(Type t)
    {
        t = AliasResolve(t);   // a ref.dll @ClrTypeAlias param -> its BCL twin, so ilemit's MapType links the rt-stdlib slot
        if (t.IsByRef) return new TypeNode.ByRef(MemberSigOf(t.GetElementType()));
        if (t.IsArray) return new TypeNode.Array(MemberSigOf(t.GetElementType()));
        if (t.IsGenericParameter)
            return new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        // Kotlin function delegate families stay `fn`, carrying their exact nominal family. Unknown/custom CLR delegates
        // remain FQNs below.
        if (IsShapeDelegate(t)) return DelegateFn(t);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(MemberSigOf).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    static bool IsShapeDelegate(Type t) => IsDelegateType(t) && DelegateFamily(t) != null;

    static string DelegateFamily(Type t)
    {
        Type def;
        try { def = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t; }
        catch { return null; }
        if (def.Namespace == "System")
        {
            if (def.Name == "Action" || def.Name.StartsWith("Action`", StringComparison.Ordinal)) return "System.Action";
            if (def.Name.StartsWith("Func`", StringComparison.Ordinal)) return "System.Func";
        }
        if (def.Namespace == "DotKt.Runtime.CompilerServices")
        {
            if (def.Name.StartsWith("KAction`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KAction";
            if (def.Name.StartsWith("KFunc`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KFunc";
        }
        return null;
    }

    static TypeNode DelegateFn(Type t)
    {
        var invoke = t.GetMethod("Invoke");
        if (invoke == null) return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));   // defensive: not a real delegate
        var ps = invoke.GetParameters().Select(p => MemberSigOf(p.ParameterType)).ToArray();
        var ret = invoke.ReturnType.FullName is "System.Void" or "kotlin.Unit" ? new TypeNode.Fqn("void") : MemberSigOf(invoke.ReturnType);
        return new TypeNode.Fn(false, ret, ps, null, DelegateFamily(t));
    }

    static string Dotted(string s) => s.Replace('+', '.');
    static string StripArity(string s) { var i = s.IndexOf('`'); return i >= 0 ? s[..i] : s; }

    // ---- diagnostics ---------------------------------------------------------------------------

    static string DescArgs(List<TypeNode> a) => string.Join(",", a.Select(x => TypeNode.ToJson(x)));
    static InvalidOperationException NoMatch<T>(string desc, List<T> cands) where T : MethodBase =>
        new($"bir2cir: no .NET member matches the resolved descriptor {desc} (ABI mismatch; {cands.Count} same-name/arity candidate(s): {string.Join("; ", cands.Select(c => c.ToString()))})");
    static InvalidOperationException Malformed<T>(string desc, List<T> hits) where T : MethodBase =>
        new($"bir2cir: resolved descriptor {desc} is AMBIGUOUS — {hits.Count} members match (malformed): {string.Join("; ", hits.Select(c => c.ToString()))}");
}
