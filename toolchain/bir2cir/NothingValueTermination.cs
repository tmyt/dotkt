using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// NOTHING-VALUE TERMINATION (#197): an expression the frontend typed `kotlin.Nothing` produces NO value — control
// leaves it and never comes back. `throw`/`return` in expression position say so in their own node kind, and ilemit
// emits them as the terminators they are; a CALL to a `fun f(): Nothing` says it only in its type stamp, and the CLR
// has no such type. BirTypeLowering erases that return to `object`, and the erased `object` is then the only thing
// the CONSUMING slot sees.
//
//     val r: String = if (n >= 0) "kept" else boom()      // boom(): Nothing
//
// ilemit's `cond` is a stack merge — both arms leave one value at the join — so the arm that "produces" an `object`
// meets the arm that produces a `string`, and the verifier rejects a merge the program never performs:
// `ilverify: StackUnexpected [found ref 'object'][expected ref 'string']`. The same erased `object` lands wrong in
// every other typed slot too (`fun f(): String = fail("x")` -> `ret` with `object` on the stack). Runtime-safe in
// each case, because the arm always throws before the join — but formally dirty, which is what blocks the
// ilverify-running test lanes.
//
// A cast would be the wrong fix: it keeps the fiction that the arm delivers a value. bir2cir owns the physical CLR
// representation of Kotlin meaning, so it states the fact instead — a `Nothing`-typed value position is TERMINATED
// where it stands, by wrapping it in the `throwExpr` the CIR vocabulary already has:
//
//     else boom()   ->   else throw boom()
//
// After that the arm has no fallthrough at all: ilemit's EmitCond leaves the join with the surviving arm's type as
// its only predecessor, and the suspend lowering's `__cond$` machinery (SuspendColdLowering.EmitCondBranch) already
// recognizes a `throwExpr` arm and emits it as a statement with NO store to the result slot. One rule, both
// lowerings — which is why this runs BEFORE the suspend transform.
//
// The `throw` is unreachable by construction (the wrapped expression never returns). Should a foreign `Nothing`
// declaration violate its contract and return anyway, the CLR throws the returned reference — wrapped in a
// RuntimeWrappedException when it is not an exception. That is a fail-closed answer to a broken contract, and the
// same shape Kotlin/JVM emits (`ACONST_NULL; ATHROW` after a `Nothing` call).
//
// ilemit still emits whatever the reading slot asked for AFTER the `throw` — a branch coercion, a `ret`, a `stloc` —
// because it projects CIR one-to-one and the terminator is a node, not a stop signal. Those instructions are
// unreachable, and a verifier walks reachable blocks only (Emitter.Bodies.cs says the same of the tail it emits after
// an always-returning body). That is load-bearing rather than incidental: it is why terminating the arm is enough
// and no reading slot has to be taught about it.
//
// SCOPE — every slot that CONSUMES a value, not just a conditional arm: the fault is the erased `object` meeting a
// typed slot, and an `if`/`when` arm, a `return` value, a call argument and a local initializer are all that same
// slot. A slot that DISCARDS its value (a statement, an `exprStmt`) is left alone: nothing reads it, so nothing is
// mistyped, and terminating it would only add a dead `throw` to every statement-position `fail()`.
//
// WHERE IT RUNS — once over each source/materialized graph, before BirTypeLowering erases `kotlin.Nothing` to
// `object`: the source-file entry runs after every raw-body splice, while a later synthesizer that authors a fresh
// executable bridge calls ApplyMaterialized on that exact bridge before publishing it. Correctness therefore does
// not depend on a later whole-file repair sweep remembering every producer. Unconditional (ref + rt + app): a ref
// build squashes its bodies later, so the pass is inert there, and the rt/app views of the same source must agree.
static class NothingValueTermination
{
    public static void Apply(JsonNode root)
    {
        if (root is JsonObject o) WalkObject(o);
        else if (root is JsonArray a) WalkArray(a, consumed: false);
    }

    /// <summary>Normalize one newly synthesized executable graph at its construction boundary.</summary>
    public static T ApplyMaterialized<T>(T root) where T : JsonNode
    {
        Apply(root);
        return root;
    }

    // Statement-list-valued keys: their elements are statements, whose value nothing reads. `preStmts` is the
    // constructor-delegation plan's list (CallEvalLowering) — a statement list like the other three, just not one a
    // body walk reaches by the usual route.
    static readonly HashSet<string> StmtListKeys = new(StringComparer.Ordinal) { "stmts", "body", "finally", "preStmts" };
    // Structural record lists (a `when` branch, a `catch` clause): the records themselves are not expressions; their
    // own slots are classified by their own kind once the walk reaches them.
    static readonly HashSet<string> RecordListKeys = new(StringComparer.Ordinal) { "catches", "branches" };

    /// <summary>Does the slot <paramref name="key"/> of a node of kind <paramref name="kind"/> need a terminator when
    /// it holds a value that can never be delivered? Everything does except a statement list, a structural record
    /// list, and the four slots below.</summary>
    ///
    /// The three "evaluated for effect" ones — an `exprStmt`'s expression, a `forIn`'s non-array `fallback` block, a
    /// statement `if`'s branch bodies — govern TIDINESS, not correctness. Only a `Nothing`-STAMPED node is ever
    /// rewritten, and appending a terminator to an expression that never returns cannot change what the program does,
    /// so a slot mis-called consuming costs one dead `throw` while one mis-called discarding costs the fix at that
    /// slot. That asymmetry is why the excluded set is the short, enumerated one and everything else is included.
    ///
    /// The fourth — a `throw`'s own operand — preserves an already-terminated producer input. Every position this pass
    /// terminates becomes a `throwExpr` whose `value` is the original still-`Nothing`-stamped expression; descending
    /// into that operand would wrap a terminator again. It is also simply unnecessary: the `throw` opcode takes any
    /// object reference, so an erased `object` is well-typed there, and the path already terminates.
    static bool Consumes(string kind, string key)
    {
        if (StmtListKeys.Contains(key) || RecordListKeys.Contains(key)) return false;
        if (kind == "exprStmt" && key == "expr") return false;
        // `forIn.fallback` is a `block` of statements (kotc's non-array iteration path), not a value.
        if (kind == "forIn" && key == "fallback") return false;
        // The schema (docs/bir-cir.schema.json) admits a `{k:"if", cond, then, else}` STATEMENT whose branches are
        // statements. Today's producers emit the `branches` chain instead, so this arm is a schema guard, not a
        // description of emitted BIR.
        if (kind == "if" && (key == "then" || key == "else")) return false;
        if ((kind == "throwExpr" || kind == "throw") && key == "value") return false;
        return true;
    }

    // Post-order: a node is considered only after its own sub-expressions are, so a `cond` whose BOTH arms are
    // `Nothing` has already had each arm terminated by the time the walk leaves it.
    static void WalkObject(JsonObject obj)
    {
        // A TYPE node (`{t:…}`, spec §1) carries no expression; it also spells `params` the way a declaration does,
        // so descending would walk every `(A) -> B` for nothing.
        if (TypeJson.IsType(obj)) return;
        var kind = Str(obj["k"]);
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            var consumed = Consumes(kind, key);
            switch (obj[key])
            {
                case JsonArray arr:
                    WalkArray(arr, consumed);
                    break;
                case JsonObject child:
                    WalkObject(child);
                    if (consumed && NeedsTermination(child)) obj[key] = Terminate(child);
                    break;
            }
        }
    }

    static void WalkArray(JsonArray arr, bool consumed)
    {
        for (var i = 0; i < arr.Count; i++)
            switch (arr[i])
            {
                case JsonArray inner:
                    WalkArray(inner, consumed);
                    break;
                case JsonObject child:
                    WalkObject(child);
                    if (consumed && NeedsTermination(child)) arr[i] = Terminate(child);
                    break;
            }
    }

    /// <summary>A value that can never be delivered AND can still physically fall through to whatever reads it.</summary>
    ///
    /// The frontend's own STAMP (`sty`/`ret`/`dynRet`), never a derivation. `NodeType.Of` also DERIVES a type from a
    /// node's kind and operands, and there it answers best-effort: a `cond` whose one arm it cannot type reports the
    /// OTHER arm's, so kotc's `!!` desugar — `{ var __nn = x; __nn != null ? __nn : throw }`, whose `then` is an
    /// unstamped local read — derives as `Nothing` while its value is plainly an `x`. Terminating on that deletes a
    /// live value. A stamp is a fact; a derivation is an inference, and only the fact may authorize this rewrite.
    ///
    /// Nothing is lost by refusing the derivation, because a COMPOSITE carries no stamp to refuse: kotc puts a
    /// conditional's unified result type in `type`, not `sty`, and a `valueBlock` is usually left untyped entirely.
    /// So a `when` whose every arm is `Nothing`, or a block whose LAST expression is, is reached ARM BY ARM — the
    /// walk is post-order, each stamped arm is terminated where it stands, and the composite then has no arm that
    /// can fall through. Terminating the composite as well would add nothing.
    ///
    /// The `k` test is load-bearing too: a DECLARATION carries its return type in the same `ret` slot a call carries
    /// its result in, so `fun f(): Nothing` stamps `kotlin.Nothing` on the METHOD. Only a node that names an
    /// expression kind can be an expression; a declaration names none.
    static bool NeedsTermination(JsonObject node)
        => Str(node["k"]) != null && NodeType.IsNothing(NodeType.Stamp(node)) && !Terminates(node);

    // IDEMPOTENCE: does control already leave this expression, so that terminating it would only nest one `throw`
    // inside another? Every kind here is a terminator in its own right — kotc's expression-position `throw`/`return`
    // and the `break`/`continue` wrappers, which the frontend may stamp `kotlin.Nothing` exactly as it stamps a call.
    static bool Terminates(JsonNode node) => node is JsonObject o && Str(o["k"]) switch
    {
        "throwExpr" or "returnExpr" or "throw" or "return" or "goto" or "break" or "continue" => true,
        _ => false,
    };

    // The wrapper is deliberately bare — no `sty` stamp. `throwExpr` IS the "produces no value" kind (NodeType.Of
    // answers `kotlin.Nothing` for it structurally), so a stamp would only give BirTypeLowering another slot to erase
    // to `object`. Downstream this matters twice: ilemit emits `<expr>; throw`, which ends the basic block so the
    // merge label takes its stack state from the surviving arm alone; and SuspendColdLowering's `EmitCondBranch`
    // recognizes the kind and emits the arm as a statement with NO store to the `__cond$` slot. DeepClone rather than
    // a detach dance: a terminated value position is rare, and this keeps the rewrite a pure construction.
    static JsonObject Terminate(JsonObject node) => new() { ["k"] = "throwExpr", ["value"] = node.DeepClone() };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
