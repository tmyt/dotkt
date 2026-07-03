using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

// bundle-6 bug #5 ROOT — the §11 "Continuation<object> uniformly" ABI erasure.
//
// The coroutine ABI must be MONOMORPHIC on kotlin.coroutines.Continuation<object>. CLR interface
// contravariance (`in T`) does NOT lift value types (Continuation<object> is not a Continuation<int>),
// AND the declared `in T` is illegal anyway because T sits inside Result<T> (an invariant reference
// class) — so ilemit drops the variance and Continuation emits INVARIANT. The only shape that composes
// for generic/value results is uniform erasure + boxing at the boundaries (JVM-equivalent erasure):
// EVERY `kotlin.coroutines.Continuation[X]` type token becomes `Continuation[kotlin.Any]`, in ALL
// positions. Then BlockOnSink : Continuation<object> IS the `Continuation<object>` that
// startCoroutine/resume/resumeWith expect — exact match, no variance needed — and the
// BlockOnSink → startCoroutine → createCoroutineUnintercepted → SM → resumeWith chain type-checks.
//
// The resumeWith(Result<T>) boundary (Codex-verified Option A): resumeWith stays `resumeWith(Result<object>)`
// uniformly (the stdlib cold-core bases already hand-declare Result<Any?>; the Continuation interface
// slot with T=object is Result<object>). kotlin.Result is emitted as an INVARIANT reference class, so
// Result<int>/Result<Unit>/Result<object> are mutually incompatible — thus every Result value flowing
// into resumeWith must be Result<object>. Two coordinated Result rewrites, SCOPED to the coroutine
// protocol (user Result<X> in runCatching/`result` is untouched):
//   (a) the WHOLE resumeWith method (interface decl + every override + their bodies): erase every
//       `kotlin.Result[X]` / `@kotlin.Result[X]` token -> `[kotlin.Any]` (the param, and the body's
//       result.get_value / result.exceptionOrNull ownerTypes on the now-Result<object> `result` local);
//   (b) every resumeWith CALL argument: the inlined `Result.success/failure` construction (typeArgs) and
//       any `new kotlin.Result[X]` -> Result<object>, plus the call's `sig`.
//
// Runs in ALL builds (ref/rt/app) so ref.dll + rt.dll signatures agree, BEFORE BirTypeLowering (it
// emits kotlin.* tokens that then lower: kotlin.Any -> object in rt/app, kept verbatim in ref).
static class ContinuationErasure
{
    const string Cont = "kotlin.coroutines.Continuation";
    const string ResultFqn = "kotlin.Result";

    public static void Apply(JsonNode root) => Walk(root, inResumeWith: false);

    static void Walk(JsonNode node, bool inResumeWith)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var here = inResumeWith || IsResumeWithMethod(obj);
                var isCall = IsResumeWithCall(obj);
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var val = obj[key];
                    if (val is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        // `name` is a declaration identity (the Continuation INTERFACE's own FQN) and `owner` is a
                        // callStatic's method container (resolved as a plain type / file-class, not a generic
                        // instantiation) — a bare Continuation there must NOT gain a [kotlin.Any] arg. Every actual
                        // type-reference key (type/ownerType/ret/base/interfaces/sig/funcType/typeArgs/…) is rewritten.
                        if (key == "name" || key == "owner") continue;
                        var ns = EraseContinuation(s);
                        if (here || isCall) ns = EraseResult(ns);   // scope Result to the protocol
                        if (!ReferenceEquals(ns, s)) obj[key] = ns;
                    }
                    else if (val != null)
                    {
                        Walk(val, here);
                    }
                }
                // A resumeWith call's Result argument construction (Result.success/failure typeArgs,
                // new kotlin.Result) must yield Result<object> to match the Result<object> slot.
                if (isCall && obj["args"] is JsonArray callArgs)
                    foreach (var a in callArgs)
                        if (a != null) EraseResultConstructions(a);
                break;
            }
            case JsonArray arr:
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var val = arr[i];
                    if (val is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        var ns = EraseContinuation(s);
                        if (inResumeWith) ns = EraseResult(ns);
                        if (!ReferenceEquals(ns, s)) arr[i] = ns;
                    }
                    else if (val != null)
                    {
                        Walk(val, inResumeWith);
                    }
                }
                break;
            }
        }
    }

    // A method DECLARATION named resumeWith (no `k` — a call carries `k`). The whole subtree is the
    // resume protocol operating on the (now Result<object>) result, so every Result token erases.
    static bool IsResumeWithMethod(JsonObject obj) =>
        obj["k"] == null &&
        (obj["name"] as JsonValue)?.TryGetValue<string>(out var n) == true && n == "resumeWith";

    static bool IsResumeWithCall(JsonObject obj) =>
        (obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true &&
        (k == "callInstance" || k == "callStatic") &&
        (obj["method"] as JsonValue)?.TryGetValue<string>(out var m) == true && m == "resumeWith";

    // Walk a resumeWith-argument subtree: reset every kotlin.Result.success/failure construction's
    // type-arg to object (so it constructs Result<object>), and erase any Result type token.
    static void EraseResultConstructions(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var owner = (obj["owner"] as JsonValue)?.TryGetValue<string>(out var ow) == true ? ow : null;
                var method = (obj["method"] as JsonValue)?.TryGetValue<string>(out var me) == true ? me : null;
                if (owner == ResultFqn && (method == "success" || method == "failure") &&
                    obj["typeArgs"] is JsonArray ta)
                {
                    var repl = new JsonArray();
                    foreach (var _ in ta) repl.Add(JsonValue.Create("kotlin.Any"));
                    obj["typeArgs"] = repl;
                }
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var val = obj[key];
                    if (val is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        var ns = EraseResult(s);
                        if (!ReferenceEquals(ns, s)) obj[key] = ns;
                    }
                    else if (val != null)
                    {
                        EraseResultConstructions(val);
                    }
                }
                break;
            }
            case JsonArray arr:
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var val = arr[i];
                    if (val is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        var ns = EraseResult(s);
                        if (!ReferenceEquals(ns, s)) arr[i] = ns;
                    }
                    else if (val != null)
                    {
                        EraseResultConstructions(val);
                    }
                }
                break;
            }
        }
    }

    // kotlin.coroutines.Continuation[X] -> Continuation[kotlin.Any] (and a BARE Continuation, the
    // star-projection `Continuation<*>` token, -> Continuation[kotlin.Any]), everywhere a type token
    // may sit. A leading-substring false match (ContinuationImpl / ContinuationInterceptor /
    // ContinuationKt / SafeContinuation) is rejected by the boundary check in ReplaceArg.
    static string EraseContinuation(string s) =>
        s.Contains(Cont, StringComparison.Ordinal) ? ReplaceArg(s, Cont, appendIfBare: true) : s;

    // @?kotlin.Result[X] -> @?kotlin.Result[kotlin.Any]; a bare `kotlin.Result` (a raw-type value / the
    // success/failure call `owner`) is left untouched (appendIfBare:false) — only the protocol's
    // constructed Result<X> instances erase.
    static string EraseResult(string s) =>
        s.Contains(ResultFqn + "[", StringComparison.Ordinal) ? ReplaceArg(s, ResultFqn, appendIfBare: false) : s;

    // Replace the single type-argument of every `fqn[...]` occurrence with `kotlin.Any` (balanced
    // brackets), and — when appendIfBare — turn a bare `fqn` (no bracket) into `fqn[kotlin.Any]`. An
    // occurrence immediately followed by an identifier char is a longer name (…Impl/…Kt) and is skipped.
    static string ReplaceArg(string s, string fqn, bool appendIfBare)
    {
        var sb = new StringBuilder(s.Length + 16);
        var i = 0;
        while (i < s.Length)
        {
            var idx = s.IndexOf(fqn, i, StringComparison.Ordinal);
            if (idx < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, idx - i);
            sb.Append(fqn);
            var after = idx + fqn.Length;
            if (after < s.Length && s[after] == '[')
            {
                var depth = 0;
                var j = after;
                for (; j < s.Length; j++)
                {
                    if (s[j] == '[') depth++;
                    else if (s[j] == ']') { depth--; if (depth == 0) { j++; break; } }
                }
                sb.Append("[kotlin.Any]");
                i = j;   // skip the original [...]
            }
            else if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_'))
            {
                i = after;   // ContinuationImpl / ResultKt / … — not this type
            }
            else
            {
                if (appendIfBare) sb.Append("[kotlin.Any]");
                i = after;
            }
        }
        return sb.ToString();
    }
}
