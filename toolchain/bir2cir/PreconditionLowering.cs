using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Nodes;

// PRECONDITION / ERROR FAMILY (#73 M6). kotc emits the FAITHFUL top-level call for these stdlib symbols — it no
// longer bakes the throw/condition. bir2cir SYNTHESIZES the semantics FQN-keyed, reproducing the exact CIR kotc used
// to emit at the call site:
//
//   require(cond)         -> cond ? Unit : throw IllegalArgumentException("Failed requirement")
//   check(cond)           -> cond ? Unit : throw IllegalStateException("Check failed")
//   error(msg)            -> throw IllegalStateException(msg)                     (always throws)
//   TODO() / TODO(reason) -> throw NotImplementedError("An operation is not implemented.")   (reason ignored, as kotc did)
//   requireNotNull(x)     -> { var t = x; t != null ? t : throw IllegalArgumentException("Required value was null") }
//   checkNotNull(x)       -> { var t = x; t != null ? t : throw IllegalStateException("Required value was null") }
//   noWhenBranchMatchedException (kotlin.internal.ir intrinsic) -> throw IllegalStateException("noWhenBranchMatchedException")
//
// WHY here: these are @InlineOnly helpers — their bodies are NOT in the rt.dll, so SOME layer must synthesize the
// throw/condition; per the 4-layer split that layer is bir2cir (the Kotlin<->CLR relation), not kotc. The exception
// TYPES stay bare Kotlin FQNs (`kotlin.IllegalArgumentException`/…): the IllegalArgumentException->System.ArgumentException
// / IllegalStateException->System.InvalidOperationException BCL mapping happens DOWNSTREAM off the ref.dll @ClrTypeAlias
// (MemberCallSubstitution.TransformNew). NotImplementedError is a real emitted Kotlin exception (not aliased).
//
// require/check `requireNotNull`/`checkNotNull` value-nullable awareness: the concrete `T` is on the call's `typeArgs`
// (e.g. `requireNotNull(s: String?)` -> `typeArgs:[kotlin.String]`, `checkNotNull(n: Int?)` -> `typeArgs:[kotlin.Int]`).
// A value-type `T` (Int/Long/…) uses the `Nullable<T>` HasValue/Value shape; a reference `T` the objEq-null shape —
// exactly kotc's former `nullableElem(arg.type)` split.
//
// The top-level helpers arrive as `callStatic owner:null method:<name>` (the `until`/`println` owner:null precedent).
// Two guards keep a user symbol from being mistaken for the helper: (1) a top-level PROPERTY accessor (`prop:` marker)
// is excluded, and (2) an app-build user top-level `fun <name>(...)` shadow is skipped via localTopLevelFns.
// require/check ADDITIONALLY gate on a Boolean first parameter, so a user `fun require(x: Foo)` is never miscompiled
// (error/TODO/requireNotNull/checkNotNull rest on guards (1)+(2) alone).
//
// Runs BEFORE ClosureSynthesis (so a discarded `require(cond){ lazyMessage }` closure — kotc drops the lazyMessage,
// hardcoding "Failed requirement", and this pass preserves that — is never synthesized into an orphan closure type)
// and before MemberCallSubstitution (which would else 0-candidate the bodiless @InlineOnly helper). Unconditional
// (ref + rt + app): a ref-build ctor field-init that calls require must still lower to the same throw/cond.
static class PreconditionLowering
{
    static int _counter;

    // The value-nullable split MUST match kotc's `nullableElem` = `makeNotNull().let { isPrimitiveType() ||
    // isUnsignedType() }` — the 8 primitives PLUS the unsigned inline-classes. Unsigned is a value type on the CLR
    // (`UInt?` = `Nullable<uint>`, #76 native-unsigned), so `requireNotNull(u: UInt?)` takes the same HasValue/Value
    // unwrap shape as `Int?` — a bare objEq pass-through would leave a `Nullable<uint>` STRUCT at the use site (#118,
    // the #56 struct-consumer issue). The `kotlin.UInt` elem lowers to `System.UInt32` downstream exactly as `kotlin.Int`.
    static readonly HashSet<string> ValueTypes = new(System.StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte", "kotlin.Char", "kotlin.Boolean",
        "kotlin.Double", "kotlin.Float",
        "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
    };

    public static void Apply(JsonNode node, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value, localTopLevelFns, appBuild);
            Rewrite(o, localTopLevelFns, appBuild);
        }
        else if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonNode c)
                {
                    Apply(c, localTopLevelFns, appBuild);
                    if (a[i] is JsonObject co) Rewrite(co, localTopLevelFns, appBuild);
                }
        }
    }

    static void Rewrite(JsonObject o, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (Str(o["k"]) != "callStatic") return;
        var method = Str(o["method"]);
        if (method == null) return;

        // COMPILER INTRINSICS (collision-safe names, no user shadow possible) — the exhaustive-when synthetic else and
        // the uninitialized-property-access throw. kotc re-emits them faithfully (like ieee754equals) as an intrinsic
        // call carrying a `kotlin.*` owner; we throw the IllegalStateException it used to synthesize, with the intrinsic
        // name as the message (kotc's `str(name)`). (On this pipeline only noWhenBranchMatchedException actually reaches
        // here — lateinit lowers to `lateinitGet` — but recognizing both keeps kotc's defensive emission from 0-candidating.)
        if (method is "noWhenBranchMatchedException" or "throwUninitializedPropertyAccessException"
            && TypeJson.OwnerName(o["owner"]) is string iowner && iowner.StartsWith("kotlin", System.StringComparison.Ordinal))
        {
            Replace(o, ThrowExpr(NewExc("kotlin.IllegalStateException", method)));
            return;
        }

        // Top-level precondition/error helpers: `callStatic owner:null` (no ownerType). A member call carries ownerType.
        if (o.ContainsKey("ownerType") || !o.ContainsKey("owner") || o["owner"] != null) return;
        // A top-level PROPERTY accessor rides the same owner:null shape (`callStatic owner:null method:<propName>
        // prop:"get"/"set"`, the #81 convention); its bare property name never enters localTopLevelFns (the decl emits
        // `get_`/`set_`). Exclude it so a user top-level `val error`/`var check`/… is never mistaken for the helper.
        if (o.ContainsKey("prop")) return;
        // User top-level `fun <name>(...)` shadow (app build) is NOT the kotlin helper — leave it.
        if (appBuild && localTopLevelFns.Contains(method)) return;

        var args = o["args"] as JsonArray;
        var sig = o["sig"] as JsonArray;
        JsonNode repl;
        switch (method)
        {
            case "TODO":
                repl = ThrowExpr(NewExc("kotlin.NotImplementedError", "An operation is not implemented.", nullableMessage: false));
                break;
            case "error":
                if (args == null || args.Count < 1) return;
                repl = ThrowExpr(NewExcExpr("kotlin.IllegalStateException", args[0].DeepClone()));
                break;
            case "require":
            case "check":
            {
                if (args == null || args.Count < 1) return;
                // Guard: the first parameter must be Boolean (a user `fun require(x: Foo)` must not be miscompiled).
                if (sig == null || sig.Count < 1 || TypeJson.OwnerName(sig[0]) != "kotlin.Boolean") return;
                var (exc, msg) = method == "require"
                    ? ("kotlin.IllegalArgumentException", "Failed requirement")
                    : ("kotlin.IllegalStateException", "Check failed");
                repl = new JsonObject
                {
                    ["k"] = "cond",
                    ["cond"] = args[0].DeepClone(),
                    ["then"] = UnitNull(),
                    ["else"] = ThrowExpr(NewExc(exc, msg)),
                };
                break;
            }
            case "requireNotNull":
            case "checkNotNull":
            {
                if (args == null || args.Count < 1) return;
                if (o["typeArgs"] is not JsonArray ta || ta.Count < 1 || ta[0] is not JsonNode elem) return;
                var exc = method == "requireNotNull" ? "kotlin.IllegalArgumentException" : "kotlin.IllegalStateException";
                var name = "__rn$" + Interlocked.Increment(ref _counter);
                var local = new JsonObject { ["k"] = "local", ["name"] = name };
                var varStmt = new JsonObject
                {
                    ["k"] = "var", ["name"] = name, ["type"] = Nullable(elem.DeepClone()), ["init"] = args[0].DeepClone(),
                };
                JsonNode cond;
                if (ValueTypes.Contains(TypeJson.OwnerName(elem) ?? ""))
                    // value-nullable T?: Nullable<T>.HasValue ? .Value : throw.
                    cond = new JsonObject
                    {
                        ["k"] = "cond",
                        ["cond"] = new JsonObject { ["k"] = "nullableHasValue", ["elem"] = elem.DeepClone(), ["e"] = local.DeepClone() },
                        ["then"] = new JsonObject { ["k"] = "nullableValue", ["elem"] = elem.DeepClone(), ["e"] = local.DeepClone() },
                        ["else"] = ThrowExpr(NewExc(exc, "Required value was null")),
                    };
                else
                    // reference T?: (t != null) ? t : throw.
                    cond = new JsonObject
                    {
                        ["k"] = "cond",
                        ["cond"] = new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = new JsonObject { ["k"] = "objEq", ["lhs"] = local.DeepClone(), ["rhs"] = UnitNull() } },
                        ["then"] = local.DeepClone(),
                        ["else"] = ThrowExpr(NewExc(exc, "Required value was null")),
                    };
                repl = new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { varStmt }, ["result"] = cond };
                break;
            }
            default:
                return;
        }
        Replace(o, repl);
    }

    static JsonObject UnitNull() => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null };

    static JsonObject Nullable(JsonNode of) => new() { ["t"] = "nullable", ["of"] = of };

    // `new <ExcType>(<msg>)` via the (String) ctor; or the no-arg ctor when msg is null.
    static JsonObject NewExc(string type, string msg, bool nullableMessage = true) => new()
    {
        ["k"] = "new",
        ["type"] = TypeJson.Fqn(type),
        ["argTypes"] = new JsonArray { nullableMessage ? Nullable(TypeJson.Fqn("kotlin.String")) : TypeJson.Fqn("kotlin.String") },
        ["args"] = new JsonArray { new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.String"), ["value"] = msg } },
    };

    // `new <ExcType>(<msgExpr>)` — the String-ctor over a caller-supplied message expression (error(msg)).
    static JsonObject NewExcExpr(string type, JsonNode msgExpr) => new()
    {
        ["k"] = "new",
        ["type"] = TypeJson.Fqn(type),
        ["argTypes"] = new JsonArray { Nullable(TypeJson.Fqn("kotlin.String")) },
        ["args"] = new JsonArray { msgExpr },
    };

    static JsonObject ThrowExpr(JsonNode exc) => new() { ["k"] = "throwExpr", ["value"] = exc };

    static void Replace(JsonObject o, JsonNode repl)
    {
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in (JsonObject)repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
