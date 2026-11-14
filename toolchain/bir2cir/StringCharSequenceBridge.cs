using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class StringCharSequenceBridge
{
    const string CharSeq = "dotkt$CharSequence";
    const string Adapter = "dotkt$StringCharSequence";
    static readonly HashSet<string> StringTokens = new(StringComparer.Ordinal)
        { "kotlin.String", "System.String", "string" };
    // A StringBuilder is a non-String CharSequence that does NOT implement the synthetic `dotkt$CharSequence`; flowing
    // one into a CharSequence slot is snapshot to a String first (#149-2, WrapStringBuilder). Every spelling the static
    // type can carry at this stage (the Kotlin typealias, the JDK class it aliases, the CLR type it lowers to).
    static readonly HashSet<string> StringBuilderTokens = new(StringComparer.Ordinal)
        { "kotlin.text.StringBuilder", "java.lang.StringBuilder", "java.lang.AbstractStringBuilder", "System.Text.StringBuilder" };

    // Process-wide: the app-local adapter type is emitted into EXACTLY ONE file's `types` per assembly (all of an app's
    // BIR files are lowered by a single bir2cir process; other files that also wrap resolve the type assembly-wide via
    // ilemit's `_types`). Fresh per process; non-reference builds only. `_fired` tracks whether a wrap was authored.
    static bool _adapterEmitted;
    static bool _fired;
    // The ref.dll index — consulted ONLY to read a spliced anonymous object's (`dotkt$obj*`) ctor param types, so a
    // static-String arg flowing into its `CharSequence` capture slot is adapted
    // by the owning source constructor boundary. Null-safe.
    static ReferenceMetadataIndex _refs;
    // #170 — lifted lambdas bound into a `dotkt$CharSequence`-returning delegate (from CharSeqStringLowering). Null-safe.
    static CharSeqStringLowering.CharSeqRetLambdas _retLambdas;

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // A lexical name -> declared-type environment (method/lambda params + local `var` decls), plus the enclosing
    // method's return type (for the return-site wrap). Copy-on-extend so a child scope never mutates its parent.
    sealed class Env
    {
        public readonly Dictionary<string, TypeNode> Vars;
        public readonly TypeNode RetType;
        public Env() { Vars = new(StringComparer.Ordinal); RetType = null; }
        Env(Dictionary<string, TypeNode> vars, TypeNode retType)
        { Vars = vars; RetType = retType; }

        // Type declarations do not open a value-parameter scope here. A method,
        // lambda or constructor does, including an explicitly empty vector.
        public Env WithDecl(JsonObject decl)
        {
            if (Str(decl["kind"]) is "class" or "interface" or "struct" or "enum")
                return this;
            if (decl["params"] is not JsonArray ps) return this;
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                    vars[pn] = pt;
            return new Env(vars, TypeJson.Read(decl["ret"]) ?? RetType);
        }

        public Env WithVar(string name, TypeNode type)
        {
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal) { [name] = type };
            return new Env(vars, RetType);
        }
    }

    internal static JsonObject RestoreMaterializedValue(JsonObject value, TypeNode source, TypeNode target) =>
        source is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" } && IsScalarCharSequence(target)
            ? MaterializedExecutable.Normalize(RestoreErasedCharSequence(value, TypeJson.Write(target)).AsObject()) : null;

    static void EmitAdapter(JsonObject fileObj)
    {
        if (!_adapterEmitted)
        {
            var types = fileObj["types"] as JsonArray;
            if (types == null) { types = new JsonArray(); fileObj["types"] = types; }
            // This is a newly authored declaration at a genuine late DOM
            // materialization boundary, not a conversion of an input graph.
            types.Add(BuildAdapterDeclaration().ToDom());
            _adapterEmitted = true;
        }
    }

    // Exact-return bridges are created after the owning source bridge. Adapt only their newly materialized returns,
    // using the same conversion rule and adapter declaration as source returns; do not sweep existing bodies.
    public static void ApplyMaterializedReturns(JsonObject method)
    {
        if (!IsCharSeqT(TypeJson.Read(method["ret"])) || method["body"] is not JsonArray body) return;
        _fired = false;
        method["body"] = WalkMaterializedReturnArray(body, new Env().WithDecl(method));
        MaterializedExecutable.Normalize(method);
        if (!_fired) return;
        JsonNode root = method;
        while (root.Parent != null) root = root.Parent;
        EmitAdapter((JsonObject)root);
    }

    static JsonNode WalkMaterializedReturns(JsonNode node, Env env)
    {
        if (node is JsonObject obj)
        {
            var childEnv = env.WithDecl(obj);
            // A valueBlock's statements declare locals that scope into its
            // sibling result. Preserve that environment for nested returns;
            // statement initializers still see only preceding declarations.
            var resultEnv = childEnv;
            if (Str(obj["k"]) == "valueBlock" && obj["stmts"] is JsonArray sarr)
                foreach (var s in sarr)
                    if (s is JsonObject so && Str(so["k"]) == "var" && Str(so["name"]) is string sn
                        && TypeJson.Read(so["type"]) is TypeNode st)
                        resultEnv = resultEnv.WithVar(sn, st);
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value is JsonArray arr ? WalkMaterializedReturnArray(arr, childEnv)
                             : kv.Value == null ? null
                             : WalkMaterializedReturns(kv.Value, kv.Key == "result" ? resultEnv : childEnv);
            if (Str(copy["k"]) == "return") WrapReturn(copy, env);
            return copy;
        }
        if (node is JsonArray topArr) return WalkMaterializedReturnArray(topArr, env);
        return node.DeepClone();
    }

    // Walk an array's elements in document order, threading each `var` decl's name->type forward so a LATER sibling
    // statement's read of that local resolves its static type (a `var`'s own init is walked BEFORE the var is added,
    // so `val x = <x>` can't see itself). Non-body arrays (args/params/…) contain no `var` nodes, so this is a no-op
    // for them.
    static JsonArray WalkMaterializedReturnArray(JsonArray arr, Env env)
    {
        var copy = new JsonArray();
        var cur = env;
        foreach (var item in arr)
        {
            var walked = item == null ? null : WalkMaterializedReturns(item, cur);
            copy.Add(walked);
            if (walked is JsonObject wo && Str(wo["k"]) == "var"
                && Str(wo["name"]) is string vn && TypeJson.Read(wo["type"]) is TypeNode vt)
                cur = cur.WithVar(vn, vt);
        }
        return copy;
    }

    static TypeNode.Fn UnwrapFn(TypeNode t) => t switch
    {
        TypeNode.Fn f => f,
        TypeNode.Nullable { Of: TypeNode.Fn f } => f,
        TypeNode.Oblivious { Of: TypeNode.Fn f } => f,
        _ => null,
    };

    static bool SameDelegateParams(TypeNode.Fn source, TypeNode.Fn target)
    {
        var sp = source.DelegateParams;
        var tp = target.DelegateParams;
        if (sp.Length != tp.Length) return false;
        for (var i = 0; i < sp.Length; i++)
            if (sp[i] != tp[i]) return false;
        return true;
    }

    static TypeNode SubstCallTvs(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArgs.Length => methodArgs[tv.I],
        TypeNode.Fqn { Args: { } args } f =>
            new TypeNode.Fqn(f.Name, args.Select(a => SubstCallTvs(a, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstCallTvs(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstCallTvs(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(SubstCallTvs(a.Elem, ownerArgs, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstCallTvs(b.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstCallTvs(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(p => SubstCallTvs(p, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : SubstCallTvs(fn.Recv, ownerArgs, methodArgs),
            fn.Clr, fn.Ctx?.Select(c => SubstCallTvs(c, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };

    static void CollectTvs(TypeNode type, List<TypeNode.Tv> result)
    {
        switch (type)
        {
            case TypeNode.Tv tv:
                if (!result.Contains(tv)) result.Add(tv);
                break;
            case TypeNode.Fqn { Args: { } args }:
                foreach (var a in args) CollectTvs(a, result);
                break;
            case TypeNode.Nullable n: CollectTvs(n.Of, result); break;
            case TypeNode.Oblivious o: CollectTvs(o.Of, result); break;
            case TypeNode.Array a: CollectTvs(a.Elem, result); break;
            case TypeNode.ByRef b: CollectTvs(b.Of, result); break;
            case TypeNode.Fn fn:
                CollectTvs(fn.Ret, result);
                foreach (var a in fn.DelegateParams) CollectTvs(a, result);
                break;
        }
    }

    static int _delegateAdapterCounter;

    // Coerce a value flowing into a `dotkt$CharSequence` slot into an interface-implementing value:
    //   - a statically-String value            -> `new dotkt$StringCharSequence(str)` (WrapAdapter).
    //   - a StringBuilder (#149-2)              -> snapshot to String via LibraryKt.toString, then WrapAdapter.
    //   - a polymorphic if/else unified to      -> DESCEND into then/else and coerce EACH branch, so a String branch
    //     CharSequence (#149-3)                    is wrapped while a genuine-CharSequence branch is left as-is.
    // Anything else (a user `class S : CharSequence`, an already-wrapped adapter, an unknown expr) is returned as-is.
    //
    // `nonNullSlot` = the target slot is a NON-nullable `dotkt$CharSequence`: the frontend guarantees a non-null value,
    // so a nullable-String value (a `!!`/elvis result still carrying the pre-strip `String?` type here) is safe to peel
    // + wrap (the `x!!.split(...)` receiver path). A NULLABLE slot (`CharSequence?`-receiver ext) keeps the strict
    // bare-String test so a genuine null stays unwrapped — EXCEPT a `!!` non-null assertion, which is provably non-null
    // and so is peel-safe even there (#149-4, `x!!.isNullOrEmpty()`).
    static JsonNode CoerceCharSeqArg(JsonNode a, Env env, bool nonNullSlot)
    {
        if (a is not JsonObject o) return a;
        var conversion = SelectCharSequenceConversion(StaticType.Surface(a, BirScope.FromVars(env.Vars)),
            nonNullSlot, IsNotNullAssertion(a), Str(o["k"]) == "cond");
        if (conversion == CharSequenceConversion.String) return WrapAdapter(a);
        if (conversion == CharSequenceConversion.StringBuilder) return WrapStringBuilder(a);
        // #156 — a genuinely-nullable String value (`z: String? = null`) into a NULLABLE `CharSequence?` slot (the strict
        // path, nonNullSlot=false): the checks above deliberately leave it RAW to preserve null, but a raw `String` into a
        // `dotkt$CharSequence` interface slot is ilverify-UNSOUND (String does not implement the synthetic adapter iface) —
        // it only runs because a null short-circuits (isNullOrEmpty). Emit a runtime-conditional wrap
        // `v == null ? (dotkt$CharSequence)null : new dotkt$StringCharSequence(v)`: the slot receives a genuine adapter or
        // a typed null, ilverify-clean, null-preserving. The non-null-String and `!!`/elvis cases already wrapped above.
        if (conversion == CharSequenceConversion.NullableString) return WrapAdapterNullable(a, env);
        // The target slot is CharSequence, so adapt each conditional result branch independently. This includes
        // conditionals inside valueBlock results, such as the frontend's elvis and non-null-assertion shapes.
        if (conversion == CharSequenceConversion.Branches)
        {
            if (o["then"] is JsonNode t) { var ct = CoerceCharSeqArg(t, env, nonNullSlot); if (!ReferenceEquals(ct, t)) o["then"] = ct; }
            if (o["else"] is JsonNode e) { var ce = CoerceCharSeqArg(e, env, nonNullSlot); if (!ReferenceEquals(ce, e)) o["else"] = ce; }
            return o;
        }
        return a;
    }

    enum CharSequenceConversion { None, String, StringBuilder, NullableString, Branches }

    // The source owner and the late materialized-return owner share the exact
    // positive recognition/order. Their graph access and construction differ.
    static CharSequenceConversion SelectCharSequenceConversion(TypeNode type, bool nonNullSlot,
        bool notNullAssertion, bool conditional)
    {
        var stringType = (nonNullSlot || notNullAssertion) && type is TypeNode.Nullable nullable ? nullable.Of : type;
        if (IsStringTokT(stringType)) return CharSequenceConversion.String;
        if (type is TypeNode.Fqn { Args: null } builder && StringBuilderTokens.Contains(builder.Name))
            return CharSequenceConversion.StringBuilder;
        if (!nonNullSlot && type is TypeNode.Nullable optional && IsStringTokT(optional.Of))
            return CharSequenceConversion.NullableString;
        return conditional ? CharSequenceConversion.Branches : CharSequenceConversion.None;
    }

    // A `!!` non-null assertion desugars to `valueBlock{ var t = <e>; result = if (!(t == null)) t else throw }`. In the
    // `then` arm `t` is proven non-null, so the whole block's value is GUARANTEED non-null (it throws otherwise) and is
    // wrap-safe even into a nullable `CharSequence?` slot (#149-4, `x!!.isNullOrEmpty()`). Matched STRUCTURALLY — the
    // condition is `!(t == null)` (the `objEq` RHS MUST be the null const, else `t != OTHER` does not prove non-null),
    // and the `then` reads the SAME local `t` — so it stays tight (a nullable `if (s != sep) s else throw` does NOT
    // match) and survives the exception-type substitution that already ran (`throw NPE` -> `newClr NRE`).
    static bool IsNotNullAssertion(JsonNode n)
    {
        if (n is not JsonObject o || Str(o["k"]) != "valueBlock" || o["result"] is not JsonObject r || Str(r["k"]) != "cond") return false;
        if (r["else"] is not JsonObject e || Str(e["k"]) != "throwExpr") return false;
        if (r["cond"] is not JsonObject c || Str(c["k"]) != "unaryOp" || Str(c["op"]) != "!"
            || c["e"] is not JsonObject eq || Str(eq["k"]) != "objEq") return false;
        // RHS must be the null literal (`{k:const, value:null}`) — a `t == someNonNull` comparison is NOT a null check.
        if (eq["rhs"] is not JsonObject rhs || Str(rhs["k"]) != "const" || rhs["value"] is not null) return false;
        var lhsName = eq["lhs"] is JsonObject l && Str(l["k"]) == "local" ? Str(l["name"]) : null;
        var thenName = r["then"] is JsonObject t && Str(t["k"]) == "local" ? Str(t["name"]) : null;
        return lhsName != null && lhsName == thenName;
    }

    // Snapshot a StringBuilder to a String via the null-safe `kotlin.LibraryKt.toString(object)` (the same coercion
    // CharSeqStringLowering uses for a non-String value flowing into a `string` slot), then wrap that String in the
    // adapter. StringBuilder.split/replace consume the sequence immediately, so the snapshot is faithful (#149-2).
    static JsonObject WrapStringBuilder(JsonNode sbExpr)
    {
        var snapshot = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"),
            ["method"] = "toString",
            ["sig"] = new JsonArray { TypeJson.Fqn("object") },
            ["args"] = new JsonArray { sbExpr.DeepClone() },
        };
        return WrapAdapter(snapshot);
    }

    // (c): a return of a String | StringBuilder | polymorphic if/else into a CharSequence return type.
    static void WrapReturn(JsonObject node, Env env)
    {
        if (env.RetType is TypeNode rt && IsCharSeqT(rt) && node["value"] is JsonNode v
            && CoerceCharSeqArg(v, env, nonNullSlot: rt is TypeNode.Fqn) is JsonNode cv && !ReferenceEquals(cv, v))
            node["value"] = cv;
    }

    // A structured Type is (nullable/array of) the CharSequence synthetic.
    static bool IsCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == CharSeq,
        TypeNode.Nullable n => IsCharSeqT(n.Of),
        TypeNode.Array a => IsCharSeqT(a.Elem),
        _ => false,
    };
    static bool IsSourceCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == "kotlin.CharSequence",
        TypeNode.Nullable n => IsSourceCharSeqT(n.Of),
        TypeNode.Array a => IsSourceCharSeqT(a.Elem),
        _ => false,
    };
    static bool IsScalarCharSequence(TypeNode type) => type switch
    {
        TypeNode.Fqn { Name: CharSeq } => true,
        TypeNode.Nullable nullable => IsScalarCharSequence(nullable.Of),
        TypeNode.Oblivious platform => IsScalarCharSequence(platform.Of),
        _ => false,
    };
    static bool IsStringTokT(TypeNode t) => t is TypeNode.Fqn { Args: null } f && StringTokens.Contains(f.Name);

    // Opaque carriers retain the original boxed value. Restore its CharSequence representation
    // only at typed consumption, evaluating the value once and preserving null and existing views.
    static JsonNode RestoreErasedCharSequence(JsonNode value, JsonNode target)
    {
        var name = "__csrestore$" + System.Threading.Interlocked.Increment(ref _counter);
        JsonObject Read() => new() { ["k"] = "local", ["name"] = name };
        JsonObject Cast(string type) => new()
        {
            ["k"] = "cast", ["type"] = TypeJson.Fqn(type), ["e"] = Read(),
        };
        JsonObject Branch(string type, JsonNode converted, JsonNode otherwise) => new()
        {
            ["k"] = "cond", ["type"] = target.DeepClone(),
            ["cond"] = new JsonObject { ["k"] = "isInst", ["type"] = TypeJson.Fqn(type), ["e"] = Read() },
            ["then"] = converted, ["else"] = otherwise,
        };
        var unchanged = new JsonObject { ["k"] = "cast", ["type"] = target.DeepClone(), ["e"] = Read() };
        var snapshot = new JsonObject
        {
            ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Text.StringBuilder"),
            ["method"] = "ToString", ["ret"] = TypeJson.Fqn("System.String"),
            ["recv"] = Cast("System.Text.StringBuilder"), ["args"] = new JsonArray(),
        };
        var builder = Branch("System.Text.StringBuilder", WrapAdapter(snapshot), unchanged);
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray(new JsonObject
            {
                ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Fqn("object"), ["init"] = value.DeepClone(),
            }),
            ["result"] = Branch("kotlin.String", WrapAdapter(Cast("kotlin.String")), builder),
        };
    }

    // `new kotlin.StringCharSequence(<str>)`. Not @ClrTypeAlias, so MemberCallSubstitution.TransformNew (already run)
    // leaves it; BirTypeLowering lowers `type`/`argTypes` (kotlin.String -> System.String); ilemit reflects the ctor
    // against the runtime stdlib.
    static JsonObject WrapAdapter(JsonNode strExpr)
    {
        _fired = true;   // request app-local adapter synthesis for this file (Apply)
        // Structured type slots (§1 — types are nodes): the adapter owner + the `kotlin.String` ctor-arg type as
        // `{t:"fqn",…}` nodes; BirTypeLowering lowers `kotlin.String` -> `System.String` downstream.
        return new JsonObject
        {
            ["k"] = "new",
            ["type"] = new JsonObject { ["t"] = "fqn", ["name"] = Adapter },
            ["argTypes"] = new JsonArray { new JsonObject { ["t"] = "fqn", ["name"] = "kotlin.String" } },
            ["args"] = new JsonArray { strExpr.DeepClone() },
        };
    }

    // #156 — the runtime-conditional adapter wrap for a nullable String into a `CharSequence?` slot:
    //   v == null ? (dotkt$CharSequence)null : new dotkt$StringCharSequence(v)
    // The value is read in BOTH legs (the null test and the adapter construction), so it is bound to a temp via a
    // valueBlock unless re-reading it is free — `ValueStability.IsReReadable` (Q1, the roster in
    // bir-common/ValueStability.cs) is the one answer to that question. The temp's declared type is the value's own
    // static type (a `String?` -> the nullable token, stripped downstream).
    static JsonNode WrapAdapterNullable(JsonNode v, Env env)
    {
        JsonNode read; JsonNode tempStmt = null;
        if (ValueStability.IsReReadable(v)) read = v;
        else
        {
            var name = "__cswrap$" + System.Threading.Interlocked.Increment(ref _counter);
            var vType = StaticType.Surface(v, BirScope.FromVars(env.Vars)) is TypeNode vt
                ? TypeNode.Write(vt) : TypeJson.Fqn("kotlin.String");
            tempStmt = new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = vType, ["init"] = v.DeepClone() };
            read = new JsonObject { ["k"] = "local", ["name"] = name };
        }
        var core = new JsonObject
        {
            ["k"] = "cond",
            ["type"] = TypeJson.Fqn(CharSeq),
            ["cond"] = new JsonObject
            {
                ["k"] = "binOp", ["op"] = "===",
                ["lhs"] = read.DeepClone(),
                ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn(CharSeq), ["value"] = null },
            },
            ["then"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn(CharSeq), ["value"] = null },
            ["else"] = WrapAdapter(read),
        };
        return tempStmt == null
            ? core
            : new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { tempStmt }, ["result"] = core };
    }

    static int _counter;

}
