using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// STATIC-TYPE RECOVERY (#59): the single uniform source bir2cir reads an operand's Kotlin static type from, replacing
// the per-operator TYPE HINTS kotc used to attach (EQEQ argTypes/argValueTypes, String.plus/concat partTypes,
// println argTypes, objMethod recvType/argType). kotc now emits ONLY the faithful op + the faithful operand
// expression nodes; bir2cir recovers each operand's static type STRUCTURALLY off the node itself + a local/param type
// environment. This closes the ad-hoc-per-consumer gap: ANY consumer that needs an operand's refined static type
// reads it through here, so none silently misresolves against the declared `Any`.
//
// Two flavors (the CLR/Roslyn twins of kotc's former birType(op.type) vs stripImplicit/stripCast):
//   Surface — the operand expression's OWN static type (a boxing/narrowing `cast` node's target IS the surface type).
//             Reproduces EQEQ `argTypes` (the primitive fast-path key).
//   Value   — peel a compiler/boxing `cast` (and the value-nullable unwrap) to the UNDERLYING value type.
//             Reproduces `argValueTypes` / `partTypes` (the collection/float/nullable Kotlin-semantic key).
//
// The refined smart-cast type is ALREADY a first-class BIR fact: a smart-cast USE emits `{k:cast,type:<refined>,e:…}`
// on the operand (BirEmitterExpressions IMPLICIT_CAST + the IrGetValue narrowing), so Surface reads it off `cast.type`.
// A smart-cast operand the frontend leaves un-narrowed (the EQEQ operand `a` in `if (a is Int) a == 5`, emitted as a
// bare `{k:local}` typed `Any`) resolves through the local environment to its DECLARED type — matching the former
// `birType(op.type)` hint exactly (that operand's IR type stays `Any`, so `a == 5` is `objEq`, unchanged).

// A local/param type environment for a method body: name -> declared TypeNode. Built by extending a parent scope with a
// declaration's params + its body's `var` locals (a local shadows a same-name param). Mirrors MemberCallSubstitution's
// SubstCtx.VarTypes, but usable by the EARLY passes (PrimitiveOperatorLowering / FaithfulHintRecognition) that run
// before MemberCallSubstitution builds its own SubstCtx.
sealed class BirScope
{
    public readonly Dictionary<string, TypeNode> VarTypes;
    public static readonly BirScope Empty = new();

    BirScope() { VarTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal); }
    BirScope(BirScope parent) { VarTypes = new Dictionary<string, TypeNode>(parent.VarTypes, StringComparer.Ordinal); }

    // A child scope carrying this declaration's PARAMS only (NOT its body locals — those are recorded LEXICALLY as the
    // walk passes each `var`, so two same-named locals in disjoint sub-scopes — e.g. `for ((k,v) in a){…}` twice with a
    // List<Int> then a List<String> `v` — do NOT collide via a flat last-wins dict). Returns `this` when there are no
    // params. NOTE the FULL declared type (with nullability) is recorded — unlike SubstCtx.VarTypes (which unwraps for
    // receiver DISPATCH): StaticType needs the nullability intact (a nullable primitive is NOT the `==` ceq fast-path;
    // a nullable concat part routes to the null-safe LibraryKt.toString), and ClassifyColl unwraps a nullable coll itself.
    public BirScope Extend(JsonObject decl)
    {
        var ps = decl["params"] as JsonArray;
        if (ps == null || ps.Count == 0) return this;
        var child = new BirScope(this);
        foreach (var p in ps)
            if (p is JsonObject po && (po["name"] as JsonValue)?.GetValue<string>() is string pn
                && TypeJson.Read(po["type"]) is TypeNode pt)
                child.VarTypes[pn] = pt;
        return child;
    }

    // A mutable child scope (a copy of this) that the walk grows in place as it passes each `var` in a statement
    // sequence — so a `var` is in scope for the SUBSEQUENT siblings/children only (lexical block scoping).
    public BirScope Child() => new(this);

    // Seed a scope from an existing name->type map (a consumer that tracks its own lexical environment — e.g. the
    // StringCharSequenceBridge's Env — hands StaticType.Surface a BirScope so `local` reads resolve).
    public static BirScope FromVars(IReadOnlyDictionary<string, TypeNode> vars)
    {
        var s = new BirScope();
        foreach (var kv in vars) s.VarTypes[kv.Key] = kv.Value;
        return s;
    }

    // Record a `var` declaration into THIS (mutable child) scope, in place. No-op for a non-var / untyped node.
    public void Declare(JsonObject o)
    {
        if ((o["k"] as JsonValue)?.GetValue<string>() == "var"
            && (o["name"] as JsonValue)?.GetValue<string>() is string vn
            && TypeJson.Read(o["type"]) is TypeNode vt)
            VarTypes[vn] = vt;
    }
}

static class StaticType
{
    // The ref.dll metadata index (set by the passes' Apply) — used to RESOLVE a call/field read whose BIR node lacks a
    // `ret` (a non-generic call: kotc emits `ret` only for a generic call). Single-threaded tool, constant across files.
    public static ReferenceMetadataIndex Refs;

    // THIS-assembly emitted types (bare FQN -> the type decl), set per-file by the passes' Apply from the BIR `types`.
    // A call/field read on a USER class in this file (`Box.get_items`) is NOT in the ref.dll, so its member type is
    // recovered here from the emitted decl's fields/methods — the "file's emitted types" half of the #59 resolution.
    public static Dictionary<string, JsonObject> LocalTypes;

    // The current file's file-class name (top-level funs/props live here, keyed off the root `fileClass`). Lets a
    // `callStatic{owner:null}` to a THIS-file top-level fun resolve its return type locally (the ref.dll has no such
    // app symbol). Set as a side effect of CollectTypes (always paired with a LocalTypes assignment).
    public static string LocalFileClass;

    // CROSS-FILE aggregation (#149): the union of EVERY input file's declared types + file classes across the whole
    // compilation (bare FQN -> the decl). LocalTypes is PER-FILE, so a receiver whose static type is declared in a
    // SIBLING .kt of the SAME assembly — a cross-file user-class property (`c.body` where `class Cfg` lives in another
    // file), a cross-file top-level fun result — resolves in neither the current file's LocalTypes nor the ref.dll and
    // stays null (so a String-typed cross-file receiver reached the `dotkt$CharSequence` slot UNWRAPPED). The bir2cir
    // driver sees ALL the app's BIR, so it seeds this assembly-wide fallback ONCE before Phase 1; LocalMemberType
    // consults it whenever the per-file LocalTypes misses. The stored decls are LIVE references into each `bir.Root` —
    // an already-processed file is seen in its post-Phase-1 (in-place-mutated) state, a not-yet-processed file raw —
    // but only plain field/return IDENTITIES are read (`kotlin.String`, `kotlin.Int`, a user FQN), which no in-place
    // Phase-1 pass alters and which IsStringTokT matches in both the `kotlin.*` and lowered `System.*` spellings, so
    // the sole order-dependence is a benign residual MISS, never a wrong-type answer.
    public static Dictionary<string, JsonObject> GlobalTypes;
    // The file-class names across ALL files — a `callStatic{owner:null}` cross-file top-level fun walks these.
    public static List<string> GlobalFileClasses;

    // Seed the cross-file GlobalTypes / GlobalFileClasses from every input file's raw root. Idempotent; call once per
    // compilation before Phase 1. Mirrors CollectTypes' registration rule (a decl with fields/methods, recursing into
    // nested `types`, plus each root file class) but unions across all roots and does NOT touch LocalFileClass.
    public static void CollectGlobal(IEnumerable<JsonNode> roots)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var fcs = new List<string>();
        void Walk(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (o["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && (o["fields"] is JsonArray || o["methods"] is JsonArray))
                    map[name] = o;
                if (o["types"] is JsonArray ts) foreach (var t in ts) if (t != null) Walk(t);
            }
        }
        foreach (var root in roots)
            if (root is JsonObject ro)
            {
                if ((ro["fileClass"] as JsonValue)?.GetValue<string>() is string fc)
                {
                    if (ro["fields"] is JsonArray || ro["methods"] is JsonArray) map[fc] = ro;
                    fcs.Add(fc);
                }
                if (ro["types"] is JsonArray top) foreach (var t in top) if (t != null) Walk(t);
            }
        GlobalTypes = map;
        GlobalFileClasses = fcs;
    }

    // Build the bare-FQN -> type-decl map from a BIR file root (recursing into nested `types`).
    public static Dictionary<string, JsonObject> CollectTypes(JsonNode root)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        LocalFileClass = (root as JsonObject)?["fileClass"] is JsonValue fcv && fcv.TryGetValue<string>(out var fcn) ? fcn : null;
        void Walk(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (o["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && (o["fields"] is JsonArray || o["methods"] is JsonArray))
                    map[name] = o;
                if (o["types"] is JsonArray ts) foreach (var t in ts) if (t != null) Walk(t);
            }
        }
        if (root is JsonObject ro)
        {
            // The FILE CLASS itself (top-level funs/props live at the root, keyed by `fileClass`, not under `types`):
            // register it so a top-level `staticField`/`callStatic` on it (e.g. a private `val HEX_DIGITS = charArrayOf(…)`
            // array constant read as `HexExtensionsKt.HEX_DIGITS[i]`) recovers its declared type here.
            if ((ro["fileClass"] as JsonValue)?.GetValue<string>() is string fc && (ro["fields"] is JsonArray || ro["methods"] is JsonArray))
                map[fc] = ro;
            if (ro["types"] is JsonArray top) foreach (var t in top) if (t != null) Walk(t);
        }
        return map;
    }

    // A member's declared type on a THIS-assembly emitted type: a property/field named `member` (or the field behind a
    // `get_<field>` / `is<Field>` getter), else a method named `member`'s return type. `argCount >= 0` disambiguates
    // same-name method OVERLOADS by preferring a `params`-count match (a name-only hit is the fallback when no arity
    // matches, e.g. a defaulted-param call whose emitted sig differs). null when the owner/member is not an emitted type.
    static TypeNode LocalMemberType(string ownerFqn, string member, int argCount = -1, bool exactArity = false)
    {
        if (member == null || LookupType(ownerFqn) is not JsonObject td) return null;
        var field = member.StartsWith("get_", StringComparison.Ordinal) ? member.Substring(4)
                  : member.StartsWith("set_", StringComparison.Ordinal) ? member.Substring(4)
                  : member;
        if (td["fields"] is JsonArray fs)
            foreach (var f in fs)
                if (f is JsonObject fo && (fo["name"] as JsonValue)?.GetValue<string>() == field && TypeJson.Read(fo["type"]) is TypeNode ft)
                    return ft;
        if (td["methods"] is JsonArray ms)
        {
            TypeNode nameHit = null;
            foreach (var m in ms)
                if (m is JsonObject mo && (mo["name"] as JsonValue)?.GetValue<string>() == member && TypeJson.Read(mo["ret"]) is TypeNode mr)
                {
                    if (argCount >= 0 && (mo["params"] as JsonArray)?.Count == argCount) return mr;  // exact overload
                    nameHit ??= mr;
                }
            // `exactArity` (cross-file owner:null resolution) suppresses the name-only fallback — a same-name sibling of
            // a DIFFERENT arity must not answer for the callee (it would guess a wrong return type; #149 should-fix 2).
            return exactArity ? null : nameHit;
        }
        return null;
    }

    // The type decl for an owner FQN: the current file's LocalTypes first (post-lowering, authoritative for THIS file),
    // then the cross-file GlobalTypes fallback (a sibling .kt's user class / file class — #149).
    static JsonObject LookupType(string ownerFqn)
    {
        if (ownerFqn == null) return null;
        if (LocalTypes != null && LocalTypes.TryGetValue(ownerFqn, out var td)) return td;
        if (GlobalTypes != null && GlobalTypes.TryGetValue(ownerFqn, out var gd)) return gd;
        return null;
    }

    // A `callStatic{owner:null}` to a top-level fun DECLARED in a SIBLING file (#149): search every OTHER file class for
    // an EXACT-arity method match (the current file class is tried by the caller first, so skip it here). Exact-arity
    // ONLY — an `owner:null` call carries no package, so a name-only cross-file guess could answer for a same-name
    // sibling of a different arity (or shadow the ref.dll's recv-key-disambiguated stdlib resolution) with a wrong
    // return type; a miss here falls through to `Refs.TryTopLevelReturn`, which filters by the receiver key.
    static TypeNode TryGlobalTopLevel(string method, int argCount)
    {
        if (GlobalFileClasses == null || argCount < 0) return null;
        foreach (var fc in GlobalFileClasses)
            if (fc != LocalFileClass && LocalMemberType(fc, method, argCount, exactArity: true) is TypeNode t) return t;
        return null;
    }

    // The operand expression's OWN static type (no cast peeling) — the former `birType(op.type)` hint. null when the
    // node carries no recoverable static type — a null surface type is treated by callers as "not a bare primitive"
    // exactly as a non-primitive hint would be.
    public static TypeNode Surface(JsonNode node, BirScope scope)
    {
        if (node is not JsonObject o) return null;
        var k = (o["k"] as JsonValue)?.GetValue<string>();
        switch (k)
        {
            // A boxing/narrowing cast's TARGET is the surface type (kotc's `birType(op.type)` = the cast target).
            case "cast": return TypeJson.Read(o["type"]);
            case "const": return TypeJson.Read(o["type"]);
            case "conv": return TypeJson.Read(o["to"]);
            case "nullableValue": return TypeJson.Read(o["elem"]);
            case "local":
                return (o["name"] as JsonValue)?.GetValue<string>() is string vn
                    && scope.VarTypes.TryGetValue(vn, out var vt) ? vt : null;
            case "clrInstance" or "clrStatic": return TypeJson.Read(o["ret"]);
            // A call carries `ret` only when GENERIC; a non-generic call lacks it -> resolve from the ref.dll (#59).
            // A generic member read surfaces the member's DECLARED return, which may be an OPEN type variable of the
            // owner (`Box<MutableList<Int>>.v` where `val v: X` -> ret = tv(type,0)); SubstMemberTv concretizes it.
            case "callStatic" or "callInstance": return SubstMemberTv(TypeJson.Read(o["ret"]) ?? ResolveCallReturn(o), o, scope);
            // A property/field read carries no type slot -> resolve the property getter's return type from the ref.dll.
            case "clrPropGet": return SubstMemberTv(TypeJson.Read(o["ret"]) ?? ResolveFieldType(o), o, scope);
            // A field read carries an explicit `ret` only when its owner is GENERIC (kotc's retHint) — a property read on
            // a generic owner instantiated at a concrete type (`Box<String>.item`); prefer it, else resolve the getter.
            case "field" or "lateinitGet" or "staticField": return SubstMemberTv(TypeJson.Read(o["ret"]) ?? ResolveFieldType(o), o, scope);
            case "new" or "newClr": return TypeJson.Read(o["type"]);
            case "newArray" or "newArrayInit" or "newArraySized":            // an array factory / sized ctor -> Array<elem>
                return TypeJson.Read(o["elem"]) is TypeNode ae ? new TypeNode.Fqn("kotlin.Array", new[] { ae }) : null;
            case "arrayGet": return TypeJson.Read(o["elem"]);                 // `a[i]` -> the element type
            case "enumValue": return TypeJson.Read(o["type"]);
            // `enumValues<T>()`/`T.entries` (basic/generic-param enum) -> Array<T>. `type` is the structured enum Type,
            // from BOTH producers: EnumIntrinsicLowering's top-level `enumValues<T>()` re-emission and kotc's direct
            // `Color.values()`/`.entries` recognition (both clone the FAITHFUL FQN node; #73 M3 retired the `@Name` string).
            case "enumValues":
                return TypeJson.Read(o["type"]) is TypeNode eet ? new TypeNode.Fqn("kotlin.Array", new[] { eet }) : null;
            case "safeCastValue": return TypeJson.Read(o["elem"]);           // `x as? V` -> V (value)
            case "concat": return new TypeNode.Fqn("kotlin.String");         // a template/`+` concat is always String
            case "isInst" or "isInstRef" or "objEq": return new TypeNode.Fqn("kotlin.Boolean");
            // An expression-level ternary (`if`-expr / elvis / when-expr) -> its branch type.
            // A value-position ternary carries kotc's UNIFIED branch type (`type`) — prefer it: the branches may differ
            // (`if (c) "s" else charSeq` unifies to CharSequence), so resolving off the then-branch alone would mis-type
            // the whole expression (and mis-wrap it). A statement-position/`!!`-desugar cond carries no `type` -> fall
            // back to the branch types (the `x!!.split(...)` receiver path relies on this then-branch resolution).
            case "cond": return TypeJson.Read(o["type"]) ?? Surface(o["then"], scope) ?? Surface(o["else"], scope);
            // A spliced inline call becomes a `valueBlock {stmts, result}` (InlineSplice) — its static type is the RESULT's,
            // resolved with the block's OWN `var`s in scope (e.g. an `apply`-splice's result is `{k:local,__self}` declared
            // in its stmts). Without this, a member call on a spliced value (`buildString{}.…`, a spliced map access) can't
            // recover its receiver type and mis-lowers (e.g. `.toString()` stays the un-mapped `objMethod toString`).
            case "valueBlock":
            {
                var inner = scope.Child();
                foreach (var arr in new[] { o["stmts"] as JsonArray, o["body"] as JsonArray })
                    if (arr != null) foreach (var st in arr) if (st is JsonObject so) inner.Declare(so);
                return TypeJson.Read(o["type"]) ?? Surface(o["result"], inner);
            }
            // A LOWERED primitive operator (PrimitiveOperatorLowering runs before the hint passes) — recover its RESULT
            // type, matching kotc's former `birType(op.type)` where `op` was the un-lowered `x.plus(y)`/`-x` member call.
            case "unaryOp":
                return (o["op"] as JsonValue)?.GetValue<string>() == "!" ? new TypeNode.Fqn("kotlin.Boolean") : Surface(o["e"], scope);
            case "binOp":
                return (o["op"] as JsonValue)?.GetValue<string>() is "<" or "<=" or ">" or ">=" or "==" or "!=" or "&&" or "||"
                    ? new TypeNode.Fqn("kotlin.Boolean")
                    : Surface(o["lhs"], scope) ?? Surface(o["rhs"], scope);
            // A bare `this` (the enclosing type, not carried here) and anything else remain null → the caller treats
            // them as non-primitive/non-collection (objEq / Object.Equals / no-wrap), the same posture the former hint
            // took for a non-primitive/non-collection operand.
            default: return null;
        }
    }

    // A call node without a `ret`: resolve the callee's declared return type from the ref.dll. For `owner=null` (a
    // top-level fun) route through the file-class owner (TryTopLevelReturn); for an explicit owner, look the member up
    // directly. Overload disambiguation uses the sig's first param (the receiver) + the sig arity.
    static TypeNode ResolveCallReturn(JsonObject o)
    {
        if ((o["method"] as JsonValue)?.GetValue<string>() is not string method) return null;
        var sig = o["sig"] as JsonArray;
        var argCount = sig?.Count ?? (o["args"] as JsonArray)?.Count ?? 0;
        var recvKey = sig != null && sig.Count > 0 && TypeJson.Read(sig[0]) is TypeNode.Fqn sf
            ? ReferenceMetadataIndex.BareOwnerFqn(sf.Name) : null;
        // An explicit owner (a member call `Box.get_items`): try the THIS-assembly emitted type first, then the ref.dll.
        if (TypeJson.Read(o["owner"] ?? o["ownerType"]) is TypeNode.Fqn of)
        {
            var owner = ReferenceMetadataIndex.BareOwnerFqn(of.Name);
            return LocalMemberType(owner, method, argCount) ?? Refs?.TryMemberReturn(owner, method, argCount);
        }
        // owner=null: a top-level fun — resolve via the THIS-assembly file class first (an app-own top-level fun the
        // ref.dll can't know), then the ref.dll file-class owner (a stdlib top-level fun).
        return o["owner"] is null
            ? LocalMemberType(LocalFileClass, method, argCount) ?? TryGlobalTopLevel(method, argCount) ?? Refs?.TryTopLevelReturn(method, recvKey, argCount)
            : null;
    }

    // A field / property read without a type slot: resolve the property GETTER's declared return type (`get_<name>`,
    // 0-arg) from the ref.dll owner. A Kotlin property's backing-field type = the property type = the getter return.
    static TypeNode ResolveFieldType(JsonObject o)
    {
        if ((o["name"] as JsonValue)?.GetValue<string>() is not string name) return null;
        if (TypeJson.Read(o["ownerType"]) is not TypeNode.Fqn owner) return null;
        var ownerFqn = ReferenceMetadataIndex.BareOwnerFqn(owner.Name);
        // THIS-assembly emitted type (a user class field) first, then the ref.dll property getter, then a ref.dll
        // STATIC field (a cross-file top-level `val` / companion array constant — no getter, a plain field).
        return LocalMemberType(ownerFqn, name)
            ?? Refs?.TryMemberReturn(ownerFqn, "get_" + name, 0) ?? Refs?.TryMemberReturn(ownerFqn, name, 0)
            ?? Refs?.TryFieldType(ownerFqn, name);
    }

    // A generic member's resolved return type may be an OPEN type variable of the owner (a generic member read on a
    // CONCRETELY-instantiated owner: `Box<MutableList<Int>>.v` where `val v: X` surfaces `ret = tv(type,0)`). Left as
    // a bare `tv`, downstream recognizers (ClassifyColl, the println collection-wrap) misfire — they key on the CONCRETE
    // type. Substitute each tv against the concrete instantiation: tv(scope="type", i) -> the owner's concrete arg i,
    // tv(scope="method", i) -> the call's own typeArgs[i]; recursively through nested Fqn/Nullable/Array/ByRef/Fn.
    // GATED precisely — an already-concrete `ret` (no tv anywhere) is returned untouched; an UNRESOLVABLE tv (no
    // concrete owner args, or index out of range) is left untouched, and a tv whose concrete arg is ITSELF open stays
    // open (tv-for-tv) — never a concrete guess.
    static TypeNode SubstMemberTv(TypeNode ret, JsonObject o, BirScope scope)
    {
        if (ret == null || !ContainsTv(ret)) return ret;
        // Owner concrete args: prefer the DECLARING owner's token (`ownerType`/`owner` — the exact owner class, so its
        // args line up 1:1 with the member's tv(type,*) indices). Fall back to the SURFACED receiver's args ONLY when
        // it names the SAME owner class (a bare/argless base token paired with a differently-parameterized subclass
        // receiver must NOT borrow the subclass's args against the base member's tv indices).
        var ownerTok = TypeJson.Read(o["ownerType"] ?? o["owner"]) as TypeNode.Fqn;
        var ownerArgs = ownerTok?.Args
                        ?? (Surface(o["recv"], scope) is TypeNode.Fqn rf
                            && (ownerTok == null
                                || ReferenceMetadataIndex.BareOwnerFqn(rf.Name) == ReferenceMetadataIndex.BareOwnerFqn(ownerTok.Name))
                            ? rf.Args : null);
        // Method type args: the generic call's own typeArgs (kotc's `typeArgs` slot).
        var methodArgs = (o["typeArgs"] as JsonArray)?.Select(a => TypeJson.Read(a)).Where(t => t != null).ToArray();
        return Subst(ret, ownerArgs, methodArgs);
    }

    static bool ContainsTv(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } a } => a.Any(ContainsTv),
        TypeNode.Nullable n => ContainsTv(n.Of),
        TypeNode.Oblivious ob => ContainsTv(ob.Of),
        TypeNode.Array ar => ContainsTv(ar.Elem),
        TypeNode.ByRef b => ContainsTv(b.Of),
        TypeNode.Fn fn => ContainsTv(fn.Ret) || fn.Params.Any(ContainsTv) || (fn.Recv != null && ContainsTv(fn.Recv)),
        _ => false,
    };

    static TypeNode Subst(TypeNode t, TypeNode[] ownerArgs, TypeNode[] methodArgs) => t switch
    {
        TypeNode.Tv { Scope: "type", I: var i } => ownerArgs != null && i >= 0 && i < ownerArgs.Length ? ownerArgs[i] : t,
        TypeNode.Tv { Scope: "method", I: var i } => methodArgs != null && i >= 0 && i < methodArgs.Length ? methodArgs[i] : t,
        TypeNode.Fqn { Args: { } a } f => new TypeNode.Fqn(f.Name, a.Select(x => Subst(x, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Subst(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious ob => new TypeNode.Oblivious(Subst(ob.Of, ownerArgs, methodArgs)),
        TypeNode.Array ar => new TypeNode.Array(Subst(ar.Elem, ownerArgs, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(Subst(b.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Subst(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(p => Subst(p, ownerArgs, methodArgs)).ToArray(), fn.Recv == null ? null : Subst(fn.Recv, ownerArgs, methodArgs)),
        _ => t,
    };

    // The operand's UNDERLYING value type: peel a `cast` (a compiler boxing/narrowing OR explicit `as`; the BIR does
    // not distinguish them, so this peels both — the CLR twin of kotc's `stripCast`) and the value-nullable unwrap,
    // then read the inner. Reproduces `argValueTypes` / `partTypes` (the collection/float/nullable recognition key).
    public static TypeNode Value(JsonNode node, BirScope scope)
    {
        if (node is not JsonObject o) return null;
        var k = (o["k"] as JsonValue)?.GetValue<string>();
        if (k == "cast" && o["e"] is JsonNode ce) return Value(ce, scope);
        if (k == "nullableValue" && o["e"] is JsonNode ne)
        {
            // A value-nullable unwrap's underlying value type is its `elem` (the non-null value); prefer it over the
            // wrapped `e` (which is the Nullable<T> local).
            return TypeJson.Read(o["elem"]) ?? Value(ne, scope);
        }
        return Surface(node, scope);
    }
}
