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
// Result<int>/Result<Unit>/Result<object> are mutually incompatible.
//
// RESULT MONOMORPHIZATION (bundle-6 collops2 / windowed): the same invariance that forces the resumeWith
// boundary to Result<object> forces kotlin.Result to be MONOMORPHIC on Result<object> EVERYWHERE — exactly
// like Continuation<object>. kotlin.Result's payload is already `object` (get_value : object, the ctor takes
// object), so the reference class `Result`1<T>` is a phantom-generic wrapper: T only names the get_value cast
// at the use site, never the storage. The generic accessor family `getOrThrow<T>/getOrNull<T>/…` is declared
// on `Result<T>`, but its body calls the NON-generic `throwOnFailure(Result<*>)` — star-projected to
// `Result<object>`. Passing the accessor's `Result<!!T>` receiver into that `Result<object>` param is an
// invariant-reference mismatch: the generic accessor method never fully resolves ("the method itself or the
// containing type is not fully instantiated") when driven through a nested generic (windowedIterator →
// SequenceBuilderIterator.resumeWith → getOrThrow<T> → throwOnFailure). The fix is to erase EVERY
// `kotlin.Result[X]` / `@kotlin.Result[X]` TYPE token to Result<object> and every `Result.success/failure`
// construction's type-arg to object, in ALL positions and ALL builds. The accessor's own type-parameter T
// survives ONLY on the RETURN (`gp:T` + the `cast gp:T` on the object payload), so `getOrThrow<int>(Result<object>)
// : int` still returns int — but there is no longer any cross-instantiation Result value, so throwOnFailure
// always receives the exact `Result<object>` it declares. (getOrThrow's typeArgs at call sites stay source-typed
// so the return type is right; only the resumeWith-DISCARDED receiver's typeArgs/retType are promoted to Any so
// the popped value's hint is non-void — EraseResultReceiverTypeArgs, still scoped to the protocol.)
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
                // Inside the erased resumeWith boundary, the `result` local is now Result<object> (invariant
                // reference class). A generic Result-accessor whose EXTENSION RECEIVER (first arg) is that erased
                // `result` — e.g. `getOrThrow<Unit>(result)` from the source `result.getOrThrow()` — is instantiated
                // at the SOURCE element type (Unit), so it expects a Result<Unit> receiver and mismatches the
                // Result<object> we pass (invalid IL / InvalidProgramException). Re-instantiate its receiver-dependent
                // T as object so `getOrThrow<object>(result: Result<object>)` type-checks. The Unit prologue value is
                // discarded (exprStmt), so no unbox/cast at the use site is needed.
                if (here) EraseResultReceiverTypeArgs(obj);
                // Result is monomorphic Result<object> globally — every `Result.success/failure<X>` construction
                // must yield Result<object>, so its type-arg is erased at EVERY call site (not just resumeWith args).
                EraseResultFactoryTypeArgs(obj);
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
                        ns = EraseResult(ns);   // Result<X> -> Result<object> everywhere (invariant ref, payload=object)
                        if (!ReferenceEquals(ns, s)) obj[key] = ns;
                    }
                    else if (val != null)
                    {
                        Walk(val, here);
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
                        var ns = EraseContinuation(s);
                        ns = EraseResult(ns);
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

    // A generic call (getOrThrow/getOrNull/exceptionOrNull/…) whose extension receiver — the first `args` entry —
    // is the erased `result` local. Its type argument parametrizes the receiver's Result<T>, so with the receiver
    // now Result<object> the call must be instantiated at object. Rewrites every `typeArgs` element to kotlin.Any.
    static void EraseResultReceiverTypeArgs(JsonObject obj)
    {
        var k = (obj["k"] as JsonValue)?.TryGetValue<string>(out var kk) == true ? kk : null;
        if (k != "callStatic" && k != "callInstance") return;
        if (obj["typeArgs"] is not JsonArray ta || ta.Count == 0) return;
        if (obj["args"] is not JsonArray args || args.Count == 0) return;
        if (args[0] is not JsonObject a0
            || (a0["k"] as JsonValue)?.TryGetValue<string>(out var ak) != true || ak != "local"
            || (a0["name"] as JsonValue)?.TryGetValue<string>(out var an) != true || an != "result")
            return;
        var repl = new JsonArray();
        foreach (var _ in ta) repl.Add(JsonValue.Create("kotlin.Any"));
        obj["typeArgs"] = repl;

        // A T-returning accessor (getOrThrow/getOrNull/getOrDefault/getOrElse) instantiated at the erased element
        // type now returns `object`, but kotc left its retType at the SOURCE element (`void`/Unit for `Result<Unit>`).
        // ilemit decides whether to POP a discarded exprStmt from this retType hint (Emitter.Statements exprStmt:
        // `if (t != void) Pop`), so a stale `void` leaks the pushed value onto the stack -> ReturnVoid / invalid IL.
        // Promote the hint to kotlin.Any so the discarded getOrThrow is popped. (exceptionOrNull returns Throwable
        // regardless of T, so it is excluded — its retType is already correct.)
        var method = (obj["method"] as JsonValue)?.TryGetValue<string>(out var me) == true ? me : null;
        if (method is "getOrThrow" or "getOrNull" or "getOrDefault" or "getOrElse")
        {
            var rk = obj.ContainsKey("retType") ? "retType" : (obj.ContainsKey("ret") ? "ret" : null);
            if (rk != null && (obj[rk] as JsonValue)?.TryGetValue<string>(out var rt) == true
                && rt is "void" or "kotlin.Unit")
                obj[rk] = JsonValue.Create("kotlin.Any");
        }
    }

    // Reset a `kotlin.Result.success/failure<X>` construction's type-arg to kotlin.Any so it yields
    // Result<object> (monomorphic). The value/exception arg keeps its own `sig` (gp:T / System.Exception);
    // only the Result type-parameter is erased. A `new kotlin.Result[X]` is handled by the global type-token
    // erasure (its `type` key). No-op on any other node.
    static void EraseResultFactoryTypeArgs(JsonObject obj)
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
