using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CHARSEQUENCE -> System.String (docs/design-charsequence-clr-string.md, the 3-point model). `kotlin.CharSequence` is
// a JVM-shaped polymorphic char view with no faithful .NET equivalent; on the CLR DotKt models it as `string` (an
// immutable snapshot). kotc emits it as the synthetic monomorphic interface `dotkt$CharSequence` in every type
// position. In a "pure" APP assembly (no user `class S : CharSequence` — verified by the driver's hasUserCharSeqImpl)
// this pass collapses that synthetic to `System.String`:
//   ① a CharSequence-typed param / return / local / field DECLARATION -> System.String (via kotlin.String, which the
//      subsequent BirTypeLowering renders as the CLR `string`);
//   member reads on such a now-`string` value — `cs.length` / `cs[i]` / `cs.subSequence(a,b)` (emitted by kotc as a
//      callInstance whose ownerType is the synthetic) -> System.String.Length / get_Chars / Substring(a, b-a);
//   ② a NON-String value (a StringBuilder) flowing into a now-`string` slot (a local call's CharSequence arg, a
//      CharSequence-return, an `as CharSequence` cast, a CharSequence-local init) -> an implicit `.toString()` snapshot
//      (an `objMethod ToString`, virtual — StringBuilder's override yields its content). A String flows directly.
// It touches ONLY this assembly's own declarations + LOCAL calls (a top-level fn in localTopLevelFns) + member reads on
// the synthetic; a call to an EXTERNAL stdlib CharSequence-extension keeps its synthetic `sig` untouched so the
// following StringCharSequenceBridge still adapter-wraps the (now-`string`) argument for the un-rebuilt stdlib. Lowering
// the STDLIB's own CharSequence-ext params to `string` (which would let the retire-B string ops route cleanly) needs a
// stdlib rebuild + a cross-assembly call-site coercion and is a documented follow-up — NOT done here.
static class CharSeqStringLowering
{
    const string CharSeq = "dotkt$CharSequence";
    // Monotonic counter for unique subSequence receiver/start spill-temp names (BUG-4 single-eval rewrite).
    static int _subSeqTmp;
    static readonly HashSet<string> StringTokens = new(StringComparer.Ordinal)
        { "kotlin.String", "System.String", "string" };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // Strip a leading `nullable:`/`array:` modifier then a `@` (this-assembly-emitted) marker, so `@dotkt$CharSequence`
    // / `nullable:dotkt$CharSequence` compare by bare identity.
    static string Bare(string t)
    {
        if (t == null) return null;
        t = t.Trim();
        foreach (var p in new[] { "nullable:", "array:" })
            if (t.StartsWith(p, StringComparison.Ordinal)) t = t[p.Length..];
        if (t.StartsWith("@", StringComparison.Ordinal)) t = t[1..];
        return t;
    }

    static bool IsCharSeq(string t) => Bare(t) == CharSeq;
    static bool IsStringTok(string t) => Bare(t) is string b && StringTokens.Contains(b);

    // Replace a CharSequence type token with `kotlin.String` (BirTypeLowering renders it as `string`), preserving a
    // leading `nullable:`/`array:` modifier; drops the `@` (String is foundational, not this-assembly-emitted).
    static string LowerTok(string t)
    {
        if (t == null) return null;
        foreach (var p in new[] { "nullable:", "array:" })
            if (t.StartsWith(p, StringComparison.Ordinal)) return p + LowerTok(t[p.Length..]);
        return "kotlin.String";
    }

    // --- structured Type versions (for the object-valued type slots; the string ones above stay for the m3 sig) ---
    static readonly TypeNode StringTn = new TypeNode.Fqn("kotlin.String");
    static bool IsCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == CharSeq,
        TypeNode.Nullable n => IsCharSeqT(n.Of),
        TypeNode.Array a => IsCharSeqT(a.Elem),
        _ => false,
    };
    static bool IsCharSeqSlot(JsonNode n) => TypeJson.Read(n) is TypeNode t && IsCharSeqT(t);
    // A CharSequence Fqn (under nullable/array) -> kotlin.String, preserving the wrappers.
    static TypeNode LowerTokT(TypeNode t) => t switch
    {
        TypeNode.Nullable n => new TypeNode.Nullable(LowerTokT(n.Of)),
        TypeNode.Array a => new TypeNode.Array(LowerTokT(a.Elem)),
        _ => StringTn,
    };
    static JsonNode LowerSlot(JsonNode n) => TypeJson.Read(n) is TypeNode t ? TypeJson.Write(LowerTokT(t)) : n;

    // Lexical name -> declared type (params + local vars, with CharSequence already mapped to kotlin.String), plus
    // whether the enclosing method's return type was CharSequence. Copy-on-extend (mirrors StringCharSequenceBridge.Env).
    sealed class Env
    {
        public readonly Dictionary<string, TypeNode> Vars;
        public readonly bool RetWasCharSeq;
        public Env() { Vars = new(StringComparer.Ordinal); RetWasCharSeq = false; }
        Env(Dictionary<string, TypeNode> vars, bool ret) { Vars = vars; RetWasCharSeq = ret; }

        public Env WithDecl(JsonObject decl)
        {
            if (decl["params"] is not JsonArray ps) return this;
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                    vars[pn] = IsCharSeqT(pt) ? StringTn : pt;
            var ret = TypeJson.Read(decl["ret"]) is TypeNode rt ? IsCharSeqT(rt) : RetWasCharSeq;
            return new Env(vars, ret);
        }

        public Env WithVar(string name, TypeNode type)
        {
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal) { [name] = type };
            return new Env(vars, RetWasCharSeq);
        }
    }

    static HashSet<string> _localFns = new(StringComparer.Ordinal);
    // Lambda/method names used as a `newDelegate` target whose funcType carries a `dotkt$CharSequence` PARAM position.
    // Such a method is a delegate body invoked by a (stdlib or app-local) higher-order caller, which passes a GENUINE
    // `dotkt$CharSequence` value into that slot — e.g. `CharSequence.windowed(size){…}` calls `transform(subSequence(…))`
    // and `subSequence` returns a real `dotkt$StringCharSequence`, NOT a `System.String`. CharSeqStringLowering never
    // lowers a `funcType` token (it must keep matching the stdlib's `Func<CharSequence,R>` generic sig), so if we ALSO
    // collapsed the target lambda's own CharSequence param to `string` its member reads would be emitted as
    // `System.String.get_Length/get_Chars` and run against a non-String object -> garbage (a value-type `R` transform
    // reads pointer bits as an int; a reference-type `R` masked it because `toString()` is a virtual objMethod). So the
    // delegate contract requires the target's param to stay the (un-lowered) synthetic — exempt the whole subtree.
    static HashSet<string> _delegateTargets = new(StringComparer.Ordinal);

    public static JsonNode Apply(JsonNode root, HashSet<string> localTopLevelFns)
        => Apply(root, localTopLevelFns, out _);

    public static JsonNode Apply(JsonNode root, HashSet<string> localTopLevelFns, out CharSeqRetLambdas retLambdas)
    {
        _localFns = localTopLevelFns ?? new HashSet<string>(StringComparer.Ordinal);
        _delegateTargets = CollectCharSeqDelegateTargets(root);
        // Collect (on the PRE-collapse tree) the lifted lambdas bound into a delegate whose funcType RETURN is the
        // synthetic `dotkt$CharSequence` — this pass is about to collapse that return signal to `string` (below), so
        // the StringCharSequenceBridge (which runs next) would never see it. The bridge consumes this set to retype
        // those lambdas' `ret` back to the synthetic and adapter-wrap their String returns, so the lifted ldftn
        // signature matches the KFunc<…,dotkt$CharSequence> ilemit rewraps the literal into (#170). (A CharSequence
        // PARAM target is a DIFFERENT case — that lambda stays verbatim via `_delegateTargets`; here it is the RETURN.)
        retLambdas = CollectCharSeqRetDelegateTargets(root);
        return Walk(root, new Env());
    }

    // Collect the `newDelegate`/`delegateInvoke` target method names whose funcType names `dotkt$CharSequence` in a
    // PARAM position (i.e. an argument slot the caller supplies — `func:<ret>:<arg0>,<arg1>,…`). The funcType's leading
    // segment is the RETURN (a CharSequence return is handled by the return-coercion path, not this exemption).
    static HashSet<string> CollectCharSeqDelegateTargets(JsonNode root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Scan(JsonNode n)
        {
            if (n is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k is "newDelegate" or "delegateInvoke"
                    && Str(o["method"]) is string mn
                    && FuncTypeHasCharSeqParam(o["funcType"]))
                    set.Add(mn);
                foreach (var kv in o) if (kv.Value != null) Scan(kv.Value);
            }
            else if (n is JsonArray a) foreach (var it in a) if (it != null) Scan(it);
        }
        Scan(root);
        return set;
    }

    // The lifted lambdas bound into a delegate whose funcType RETURN is `dotkt$CharSequence` (#170). A `newDelegate`
    // names its lifted static by `method` (`dotkt:lambda:<n>`); a `newClosure` names its synthetic class by `closureType`
    // (its lifted body is the class's `invoke`, since every closure's `method` is the constant "invoke"). The bridge
    // resolves each accordingly.
    public sealed record CharSeqRetLambdas(HashSet<string> Statics, HashSet<string> Closures);

    // Collect (pre-collapse) the delegate targets whose funcType RETURN is the synthetic CharSequence. Only a BARE
    // (non-nullable) CharSequence return qualifies — a nullable `(T) -> CharSequence?` delegate return is the separate
    // nullable/NRT-erasure axis (delegnull), out of scope here.
    static CharSeqRetLambdas CollectCharSeqRetDelegateTargets(JsonNode root)
    {
        var statics = new HashSet<string>(StringComparer.Ordinal);
        var closures = new HashSet<string>(StringComparer.Ordinal);
        void Scan(JsonNode n)
        {
            if (n is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k == "newDelegate" && FuncTypeHasCharSeqRet(o["funcType"]) && Str(o["method"]) is string mn)
                    statics.Add(mn);
                else if (k == "newClosure" && FuncTypeHasCharSeqRet(o["funcType"])
                         && TypeJson.Read(o["closureType"]) is TypeNode.Fqn { Name: { } ct })
                    closures.Add(ct);
                foreach (var kv in o) if (kv.Value != null) Scan(kv.Value);
            }
            else if (n is JsonArray a) foreach (var it in a) if (it != null) Scan(it);
        }
        Scan(root);
        return new CharSeqRetLambdas(statics, closures);
    }

    // A function type whose RETURN is a bare `dotkt$CharSequence`. Structured Fn (newDelegate) or the legacy
    // `func:<ret>:<args>` string (newClosure) — the leading segment is the return.
    static bool FuncTypeHasCharSeqRet(JsonNode ftNode)
    {
        if (TypeJson.Read(ftNode) is TypeNode.Fn fn) return fn.Ret is TypeNode.Fqn { Name: CharSeqSynthetic };
        if (Str(ftNode) is not string ft || !ft.StartsWith("func:", StringComparison.Ordinal)) return false;
        var rest = ft["func:".Length..];
        var ci = TopLevelColon(rest);
        var retSeg = ci < 0 ? rest : rest[..ci];
        return retSeg == CharSeqSynthetic;
    }

    const string CharSeqSynthetic = "dotkt$CharSequence";

    // A function type any of whose PARAMS is CharSequence (the delegate-target exemption). funcType is a structured Fn
    // (newDelegate) or, on a newClosure, a legacy `func:<ret>:<args>` string.
    static bool FuncTypeHasCharSeqParam(JsonNode ftNode)
    {
        if (TypeJson.Read(ftNode) is TypeNode.Fn fn) return fn.DelegateParams.Any(IsCharSeqT);   // incl. a `CharSequence.() -> X` receiver (#145)
        if (Str(ftNode) is not string ft || !ft.StartsWith("func:", StringComparison.Ordinal)) return false;
        var rest = ft["func:".Length..];
        var ci = TopLevelColon(rest);
        if (ci < 0) return false;
        return SplitTopLevel(rest[(ci + 1)..]).Any(IsCharSeq);
    }

    // Index of the first `:` not nested inside `[`/`<`/`(` brackets, or -1.
    static int TopLevelColon(string s)
    {
        int depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '[' or '<' or '(') depth++;
            else if (c is ']' or '>' or ')') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    static JsonNode Walk(JsonNode node, Env env)
    {
        if (node is JsonObject obj)
        {
            // A delegate-target lambda keeps its signature matching the (un-lowered) funcType — do not collapse its
            // CharSequence params/reads to String. Leave the whole subtree verbatim (its member reads stay virtual
            // interface calls that resolve on the real dotkt$CharSequence the caller passes in).
            if (obj["k"] == null && Str(obj["name"]) is string dn && _delegateTargets.Contains(dn))
                return obj.DeepClone();
            var childEnv = env.WithDecl(obj);
            // A valueBlock's `stmts` locals scope into its sibling `result` (see StringCharSequenceBridge.Walk) — thread
            // them so a coercion/lowering site in `result` resolves a String local declared in stmts.
            var resultEnv = childEnv;
            if (Str(obj["k"]) == "valueBlock" && obj["stmts"] is JsonArray sarr)
                foreach (var s in sarr)
                    if (s is JsonObject so && Str(so["k"]) == "var" && Str(so["name"]) is string sn
                        && TypeJson.Read(so["type"]) is TypeNode st)
                        resultEnv = resultEnv.WithVar(sn, IsCharSeqT(st) ? StringTn : st);
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value is JsonArray arr ? WalkArray(arr, childEnv)
                             : kv.Value == null ? null
                             : Walk(kv.Value, kv.Key == "result" ? resultEnv : childEnv);
            return Transform(copy, env);
        }
        if (node is JsonArray topArr) return WalkArray(topArr, env);
        return node.DeepClone();
    }

    // Thread each `var` decl's (already-lowered) name->type forward so a later sibling's read resolves its static type.
    static JsonArray WalkArray(JsonArray arr, Env env)
    {
        var copy = new JsonArray();
        var cur = env;
        foreach (var item in arr)
        {
            var walked = item == null ? null : Walk(item, cur);
            copy.Add(walked);
            if (walked is JsonObject wo && Str(wo["k"]) == "var"
                && Str(wo["name"]) is string vn && TypeJson.Read(wo["type"]) is TypeNode vt)
                cur = cur.WithVar(vn, IsCharSeqT(vt) ? StringTn : vt);
        }
        return copy;
    }

    static JsonNode Transform(JsonObject node, Env env)
    {
        var k = Str(node["k"]);

        // #122: `sty` is the frontend static-type stamp the following StringCharSequenceBridge CONSUMES (its `Surface`
        // reads `sty` BEFORE the lexical scope). This pass collapses a CharSequence local/param/field/top-level-val
        // DECLARATION to String, but a READ of one still carries the pre-collapse `dotkt$CharSequence` `sty` — so the
        // bridge's sty-first Surface reports the stale CharSequence, judges the value "not a static String", and SKIPS
        // the adapter-wrap. A raw `string` then reaches a `dotkt$CharSequence` slot -> ilverify StackUnexpected
        // (regexreplace's `val cs: CharSequence = "banana"; a.replaceFirst(cs, ...)`). Retype the read's stale `sty` to
        // String in lockstep with the decl — reproducing the pre-#122 resolution, which read a variable/field/
        // staticField reference's static type off its (already-collapsed) declaration.
        //   • field/lateinitGet/staticField: UNCONDITIONAL — no .NET field is typed `dotkt$CharSequence`, a same-module
        //     class/file-class field DECL is collapsed by LowerDeclTypes, and object/enum staticField sty is the
        //     owner FQN (never the synthetic), so a CharSeq sty here is always a collapsed-to-String declaration read.
        //   • local: GATED on the lexical scope — collapse only when the var's (already-collapsed) decl type is String
        //     (or the name is a bir2cir-synthesized temp absent from the scope). A bare `local` whose sty is a
        //     SMART-CAST refinement over a NON-CharSequence decl (`val x: SomeIface; when (x) { is CharSequence -> … }`,
        //     kotc emits a bare `{k:local}` typed by the refined `dotkt$CharSequence` — BirEmitterExpressions IrGetValue
        //     narrowing) can hold a GENUINE `dotkt$StringCharSequence` adapter; its decl was never collapsed, so leaving
        //     its sty intact keeps the bridge from wrongly adapter-wrapping a non-String value (Fable-flagged, gate-blind).
        // A call / subSequence RESULT that genuinely stays a `dotkt$CharSequence` adapter is a callInstance/callStatic —
        // NOT in this set — so it keeps its `sty`. (Delegate-target lambda subtrees never reach here — Walk clones them
        // out before Transform — so their un-collapsed CharSequence reads are untouched.)
        if (IsCharSeqSlot(node["sty"]))
        {
            var collapseSty = k switch
            {
                "field" or "lateinitGet" or "staticField" => true,
                "local" => Str(node["name"]) is not string sn
                           || !env.Vars.TryGetValue(sn, out var st) || IsStringTokT(st),
                _ => false,
            };
            if (collapseSty) node["sty"] = LowerSlot(node["sty"]);
        }

        // A member READ on a CharSequence value (kotc: callInstance whose ownerType is the synthetic). A stdlib
        // CharSequence-EXTENSION is a callStatic (receiver as arg[0]), never this shape, so this only ever hits the
        // synthetic interface's own length/get/subSequence.
        if (k == "callInstance" && IsCharSeqSlot(node["ownerType"]))
        {
            var rewritten = RewriteMemberRead(node);
            if (rewritten != null) return rewritten;
        }

        switch (k)
        {
            case null:   // a declaration node (method/lambda def, field): lower its own signature tokens
                LowerDeclTypes(node);
                return node;
            case "var":
                if (IsCharSeqSlot(node["type"]))
                {
                    node["type"] = LowerSlot(node["type"]);
                    if (node["init"] is JsonNode init && CoerceOrNull(init, env) is JsonNode w) node["init"] = w;
                }
                return node;
            case "callStatic":
                LowerLocalCall(node, env);
                return node;
            case "return":
                if (env.RetWasCharSeq && node["value"] is JsonNode rvv && CoerceOrNull(rvv, env) is JsonNode rw)
                    node["value"] = rw;
                return node;
            case "cast":
                if (IsCharSeqSlot(node["type"]) && node["e"] is JsonNode ce)
                    return CoerceOrNull(ce, env) ?? ce.DeepClone();
                return node;
            default:
                return node;
        }
    }

    // Lower a declaration's own type tokens: params[].type, ret, and a bare `type` (a field). Never a call `sig`.
    static void LowerDeclTypes(JsonObject node)
    {
        if (node["params"] is JsonArray ps)
            foreach (var p in ps)
                if (p is JsonObject po && IsCharSeqSlot(po["type"])) po["type"] = LowerSlot(po["type"]);
        if (IsCharSeqSlot(node["ret"])) node["ret"] = LowerSlot(node["ret"]);
        if (node["k"] == null && IsCharSeqSlot(node["type"]) && node["name"] != null)
            node["type"] = LowerSlot(node["type"]);   // a field {name,type}
    }

    // A LOCAL top-level call (owner null, method in this assembly): lower each CharSequence `sig` slot to kotlin.String
    // and coerce the matching arg (a non-String value -> implicit .toString()). An EXTERNAL stdlib call (attributed
    // owner, or a name absent from localTopLevelFns) is left untouched -> the StringCharSequenceBridge handles it.
    static void LowerLocalCall(JsonObject node, Env env)
    {
        if (TypeJson.OwnerName(node["owner"]) != null) return;   // attributed -> external
        if (Str(node["method"]) is not string method || !_localFns.Contains(method)) return;
        if (node["sig"] is not JsonArray sig) return;   // sig is a structured TypeNode array (#37 m3b)
        var args = node["args"] as JsonArray;
        for (var i = 0; i < sig.Count; i++)
            if (TypeJson.Read(sig[i]) is TypeNode tn && IsCharSeqT(tn))
            {
                sig[i] = TypeJson.Write(LowerTokT(tn));
                if (args != null && i < args.Count && args[i] is JsonNode a && CoerceOrNull(a, env) is JsonNode w)
                    args[i] = w;
            }
        if (IsCharSeqSlot(node["dynRet"])) node["dynRet"] = LowerSlot(node["dynRet"]);
    }

    // `cs.length` -> System.String.Length; `cs[i]` (get) -> get_Chars; `cs.subSequence(a,b)` -> Substring(a, b-a).
    // Structurally identical to the dotkt$StringCharSequence adapter's proven bodies. Returns null for an
    // unrecognized member (leave as-is).
    static JsonObject RewriteMemberRead(JsonObject node)
    {
        var recv = node["recv"];
        var args = node["args"] as JsonArray;
        switch (Str(node["method"]))
        {
            case "get_length":
                return new JsonObject
                {
                    ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn("System.String"), ["name"] = "Length",
                    ["ret"] = TypeJson.Fqn("System.Int32"), ["static"] = false, ["recv"] = recv?.DeepClone(),
                };
            case "get":
                return new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.String"), ["method"] = "get_Chars",
                    ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Int32") }, ["ret"] = TypeJson.Fqn("System.Char"),
                    ["recv"] = recv?.DeepClone(),
                    ["args"] = new JsonArray { args != null && args.Count > 0 ? args[0].DeepClone() : null },
                };
            case "subSequence":
                if (args == null || args.Count < 2) return null;
                // `cs.subSequence(a, b)` -> `cs.Substring(a, b - a)`. `a` (start) is needed BOTH as Substring's
                // start arg AND inside the length `b - a`, so a naive rewrite evaluates `a` twice — a side-effecting
                // start index runs twice (bundle-6 BUG-4). Spill the receiver and start to temps (a `valueBlock`) so
                // each subexpression evaluates exactly once, in Kotlin order (receiver, then start, then end).
                var id = System.Threading.Interlocked.Increment(ref _subSeqTmp);
                var recvTmp = "$subSeqRecv$" + id;
                var startTmp = "$subSeqStart$" + id;
                return new JsonObject
                {
                    ["k"] = "valueBlock",
                    ["stmts"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "var", ["name"] = recvTmp, ["type"] = TypeJson.Fqn("System.String"), ["init"] = recv?.DeepClone() },
                        new JsonObject { ["k"] = "var", ["name"] = startTmp, ["type"] = TypeJson.Fqn("System.Int32"), ["init"] = args[0].DeepClone() },
                    },
                    ["result"] = new JsonObject
                    {
                        ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.String"), ["method"] = "Substring",
                        ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.Int32") }, ["ret"] = TypeJson.Fqn("System.String"),
                        ["recv"] = new JsonObject { ["k"] = "local", ["name"] = recvTmp },
                        ["args"] = new JsonArray
                        {
                            new JsonObject { ["k"] = "local", ["name"] = startTmp },
                            new JsonObject { ["k"] = "binOp", ["op"] = "-", ["lhs"] = args[1].DeepClone(), ["rhs"] = new JsonObject { ["k"] = "local", ["name"] = startTmp } },
                        },
                    },
                };
            default:
                return null;
        }
    }

    // A value flowing into a now-`string` slot: a provably-String value needs NO coercion (return null); anything else
    // (a StringBuilder, an Any) is snapshot via `.toString()` (the returned wrapper is a fresh, detached node). Callers
    // assign the wrapper only when non-null, avoiding a JsonNode reparenting error.
    //
    // NULL-SAFE (bundle-6 BUG-3): a bare `objMethod ToString` (callvirt object::ToString) NREs when `value` is null —
    // Kotlin's `x.toString()` on a null yields "null". Route through the `Any?.toString()` stdlib extension
    // (`kotlin.LibraryKt.toString` == `this?.toString() ?: "null"`), which is null-safe AND preserves the virtual
    // dispatch for a StringBuilder/Any (its `this?.toString()` calls the member override). `value` here is always a
    // CharSequence/StringBuilder/Any REFERENCE (it flows into a string slot), so no value->object boxing is needed.
    static JsonNode CoerceOrNull(JsonNode value, Env env)
    {
        if (IsStaticString(value, env)) return null;
        return new JsonObject
        {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"), ["method"] = "toString",
            ["sig"] = new JsonArray { TypeJson.Fqn("object") }, ["args"] = new JsonArray { value.DeepClone() },
        };
    }

    // POSITIVE static-String detection (mirrors StringCharSequenceBridge.IsStaticString, extended with dynRet and the
    // already-rewritten clr* String result nodes).
    static bool IsStaticString(JsonNode n, Env env)
    {
        if (n is not JsonObject o) return false;
        switch (Str(o["k"]))
        {
            case "const": return IsStringTokT(TypeJson.Read(o["type"]));
            case "local": return Str(o["name"]) is string nm && env.Vars.TryGetValue(nm, out var t) && IsStringTokT(t);
            case "cast": return IsStringTokT(TypeJson.Read(o["type"]));
            case "concat": return true;   // string concatenation
            case "this": return false;
            default:
                return IsStringTokT(TypeJson.Read(o["ret"]) ?? TypeJson.Read(o["dynRet"]));
        }
    }

    static bool IsStringTokT(TypeNode t) => t is TypeNode.Fqn { Args: null } f && StringTokens.Contains(f.Name);

    static IReadOnlyList<string> SplitTopLevel(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();
        var result = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '[' or '<' or '(') depth++;
            else if (c is ']' or '>' or ')') depth--;
            else if (c == ',' && depth == 0) { result.Add(value[start..i].Trim()); start = i + 1; }
        }
        result.Add(value[start..].Trim());
        return result;
    }
}
