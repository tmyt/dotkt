// SHARED IR SANITY gate (#84 Phase 4 / #112 Phase 4). Layer-agnostic SEMANTIC invariants over a BIR/CIR document
// tree, run IN-PROCESS before codegen so a malformed tree (an undeclared `local`, a dangling `goto`, a `field` with
// no owner) fails LOUD with a precise invariant message — instead of a cryptic Reflection.Emit crash / silent
// BadImageFormat two stages downstream. The OFFLINE schema validator (scripts/verify-schema.py) checks document
// SHAPE (canonical kinds, enums, types-are-nodes); this checks MEANING.
//
// Home: bir-common, compile-Included by BOTH bir2cir (runs it on the CIR it produces — earliest catch, at the
// bir2cir/CIR boundary) and ilemit (runs it at the head of EmitAssembly, before any emit). scripts/verify-sanity.py
// mirrors the same invariants offline for CI/dev. All three stay in sync via this invariant list.
//
// DELIBERATELY CONSERVATIVE: every check is calibrated to NEVER false-positive on a valid input — the verify-il gate
// + the stdlib rt build (250+ files) are the calibration corpus. An ambiguous shape is left UNCHECKED rather than
// risk a false reject. Two invariants were DROPPED for exactly that reason:
//   - call/`new` args-vs-argTypes arity: a caller may legitimately omit trailing DEFAULT args (args < argTypes),
//     and EmitNewArgs already tolerates the mismatch, so an equality check would false-reject valid CIR.
//
// The check set (all provably ilemit-equivalent — each mirrors a place ilemit already throws / miscompiles):
//   1. LOCAL RESOLUTION — every `local`/`setLocal`/`byref{Load,Store}` names a var/param declared in the same scope.
//   2. CFG TARGETS — every `goto`/`brIf` id has a matching `label` node in the body, and no `label` id is declared
//      twice in one body.
//   3. STRUCTURAL — `binOp` has both `lhs`+`rhs`; `cond` has `cond`+`then`+`else`.
//   4. OWNER PRESENCE — fields carry ownerType; owner:null callStatic/newDelegate/newBoundDelegate carry calleeOwner.
//   5. `for` `cmp` ∈ {<=, <, >=} — an unknown cmp silently miscompiles to an infinite loop.
//   6. SUSPENSION LOWERED — no node in a body ilemit EMITS still carries `suspendCall:true` (only a METHOD still
//      carrying `mods.suspend` is exempt — a ctor / static-initializer group never is; see CheckScope).
//   7. STAMP AGREEMENT — a node's `sty` must not name a DIFFERENT TYPE than the `ret`/`dynRet` beside it (spec §2.7:
//      a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`). See CheckStampAgreement for the
//      accepted-equivalence set and why it is what it is.
//
// SCOPE units mirror ilemit's `_locals`/`_cfgLabels` lifetimes exactly: a method = params ∪ body; a ctor ALSO folds
// in preStmts/thisArgs/baseArgs (emitted in the same frame); the static-field-initializer group shares ONE .cctor `_locals`
// scope across ALL of a type's static inits. Collection is a full generic JSON recursion (over-collecting can only
// WEAKEN a check → never a false reject) and stays intra-declaration.
using System.Text.Json;

namespace DotKt.Bir;

// A sanity violation, attributed to its declaration (with a #112 Phase-2 `File.kt:line` prefix when the decl carries
// a `pos`). Consumers (ilemit's CirSanityException, bir2cir's catch) format it into their own layer's diagnostic.
public sealed class IrSanityException : Exception
{
    public string Decl { get; }
    public IrSanityException(string decl, string message) : base(message) { Decl = decl; }
}

// WHICH invariants one traversal runs. The declaration walk, the scope units and the message attribution are the
// same either way — only the per-node predicate set differs, so the two entry points share one definition of "what
// counts as a declaration" rather than growing a second walker that could disagree with this one.
public enum IrSanityChecks
{
    /// <summary>Checks 1-7 — the POST-LOWERING CIR gate (bir2cir on its CIR output; ilemit at EmitAssembly).</summary>
    All,
    /// <summary>
    /// Check 7 alone — the spec §2.7 `sty` chokepoint, run by bir2cir on the fully-passed BIR while the stamp still
    /// exists (BirTypeLowering strips it on the way to CIR). Checks 1-6 are CIR invariants and several of them do not
    /// hold of a pre-lowering tree, so this mode runs the one check that is meaningful there and nothing else.
    /// </summary>
    StyStampsOnly,
}

public static class IrSanity
{
    // Run the sanity invariants over every method/ctor/static-field-initializer in the document, attributing each
    // violation to its declaration. Every check is intra-declaration and needs no codegen state.
    public static void Check(IEnumerable<JsonElement> files, IrSanityChecks which = IrSanityChecks.All)
    {
        foreach (var file in files)
        {
            var fileClass = file.TryGetProperty("fileClass", out var fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() : "?";
            CheckContainer(fileClass, file, isInterface: false, which);
            if (file.TryGetProperty("types", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                {
                    var tn = t.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : "?";
                    var iface = t.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "interface";
                    CheckContainer(tn, t, iface, which);
                }
        }
    }

    // A file class OR a user type: its methods, its ctors, and its static-field-initializer group (one .cctor scope).
    static void CheckContainer(string owner, JsonElement c, bool isInterface, IrSanityChecks which)
    {
        if (c.TryGetProperty("methods", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray()) CheckMethodDecl(owner, m, which);
        if (c.TryGetProperty("ctors", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var ct in cs.EnumerateArray()) CheckCtorDecl(owner, ct, which);
        // Static field initializers share ONE .cctor `_locals` scope (a temp declared in field A's init is
        // resolvable from field B's) — check them as a single scope over the UNION of the inits.
        if (!isInterface && c.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            var inits = new List<JsonElement>();
            foreach (var f in fs.EnumerateArray())
                if (f.TryGetProperty("init", out var iv) && iv.ValueKind != JsonValueKind.Null
                    && f.TryGetProperty("static", out var st) && st.ValueKind == JsonValueKind.True)
                    inits.Add(iv);
            // ALWAYS suspension-checked (checkSuspension: true). ilemit emits a type initializer from the fields
            // alone and never consults `mods.suspend` on the containing type (Emitter.Assembly.cs pass 4b), so
            // nothing here is exempt — and `decl` is the CONTAINER, whose modifiers say nothing about this body.
            if (inits.Count > 0) CheckScope(owner + "..cctor", null, inits, decl: c, checkSuspension: true, which);
        }
    }

    static void CheckMethodDecl(string owner, JsonElement m, IrSanityChecks which)
    {
        // Abstract / bodiless methods (interface members, abstract decls) emit no IL — nothing to check.
        if (m.TryGetProperty("abstract", out var ab) && ab.ValueKind == JsonValueKind.True) return;
        if (!m.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array) return;
        var name = m.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : "?";
        // The ONLY scope the suspension check exempts, and only when this METHOD still carries `mods.suspend` — the
        // exact flag ilemit's own guard keys on before it walks a method body (see CheckRefs check 6).
        CheckScope(owner + "." + name, ParamNames(m), new List<JsonElement> { body }, decl: m, checkSuspension: !IsSuspendDecl(m), which);
    }

    static void CheckCtorDecl(string owner, JsonElement c, IrSanityChecks which)
    {
        if (!c.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array) return;
        var roots = new List<JsonElement> { body };
        // `preStmts` and the `: this(...)` / `: base(...)` args are emitted in the SAME frame as the body (before it),
        // so a local declared in any of them shares the ctor's `_locals` — fold them all into the scope. `preStmts` is
        // the delegation's call-evaluation plan lowered to `var`s (spec §2.7): it DECLARES what the args READ.
        if (c.TryGetProperty("preStmts", out var pre) && pre.ValueKind == JsonValueKind.Array) roots.Add(pre);
        if (c.TryGetProperty("thisArgs", out var ta) && ta.ValueKind == JsonValueKind.Array) roots.Add(ta);
        if (c.TryGetProperty("baseArgs", out var ba) && ba.ValueKind == JsonValueKind.Array) roots.Add(ba);
        // ALWAYS suspension-checked (checkSuspension: true). ilemit's suspend guard lives in EmitMethodBody only;
        // EmitCtorBody has none, so a constructor body is emitted whatever modifiers it carries.
        CheckScope(owner + "..ctor", ParamNames(c), roots, decl: c, checkSuspension: true, which);
    }

    // Validate one `_locals`/`_cfgLabels` scope: collect its declared local names + label ids across all root trees,
    // then check every reference against them. `decl` supplies the #112 Phase-2 source position for the message.
    //
    // `checkSuspension` is check 6's gate, and the CALLER owns it because the exemption is not a property of the
    // tree — it is a property of whether ilemit will walk this particular KIND of body. Only a method scope can be
    // exempt (and only for its own `mods.suspend`); a constructor and a static-initializer group never are, because
    // ilemit emits those without consulting the flag. Deriving it here from `decl` was the bug: the .cctor scope's
    // `decl` is the CONTAINING TYPE, so a type carrying `mods.suspend` silently disabled the check over a body
    // ilemit really does emit.
    static void CheckScope(string declLabel, HashSet<string> paramNames, List<JsonElement> roots, JsonElement decl, bool checkSuspension,
                           IrSanityChecks which)
    {
        var pos = PosPrefix(decl);
        // Check 7 is NODE-LOCAL — it compares two slots of one node — so the `StyStampsOnly` traversal needs neither
        // the declared-name set nor the label set, and skips collecting them.
        if (which == IrSanityChecks.StyStampsOnly)
        {
            foreach (var r in roots) CheckRefs(pos + declLabel, r, null, null, checkSuspension: false, which);
            return;
        }
        var declared = paramNames != null ? new HashSet<string>(paramNames) : new HashSet<string>();
        var labels = new HashSet<int>();
        foreach (var r in roots) { CollectDeclared(r, declared); CollectSanityLabels(r, labels); }
        // Check 6 asks only of the bodies ilemit turns into IL, and a METHOD that STILL carries `mods.suspend` is
        // not one: ilemit's guard on that exact flag (`ModFlag(m, "suspend")`, Emitter.Bodies.cs) returns before it
        // ever reaches the statement walk — a throwing stub in a stdlib build, a loud refusal in an app build.
        // Checking those bodies would only restate that guard one layer earlier, in the one place it is expected to
        // be hit.
        //
        // Such a survivor is STDLIB-ONLY by construction, and not because a refused shape goes un-lowered — a
        // non-segmentable suspend fun still gets a cold entry + bridge, with a call-time throw for a body. The two
        // mechanisms that actually leave the flag on disk are both in SuspendColdLowering: the self-build RETAINS the
        // original beside the cold entry (kotc's pre-ignition @RestrictsSuspension `sequence{}`/`iterator{}` path
        // still calls SequenceScope.yield/yieldAll by name), where an app build removes it; and the admit gate
        // excludes an `inline` suspend fun outside an app build, which is how the coroutine PRIMITIVES — whose call
        // sites are reconstructed inline instead — keep their standalone bodies.
        //
        // Measured at this commit: all 7 `suspendCall` survivors in the runtime stdlib CIR sit in such declarations
        // (SequenceScope.yieldAll x2, SequenceBuilderIterator.yield/yieldAll, ContinuationKt.suspendCoroutine,
        // DeepRecursiveScopeImpl.callRecursive x2) and ZERO sit in an emitted body; the app corpus has none at all,
        // and neither does the 252-file reference CIR (RefBodySquash replaces its bodies with a throw stub before
        // this runs, so a ref build needs no separate exemption). The synthesized cold entries and state-machine
        // `invokeSuspend` bodies — where an escape would actually land — carry no `mods.suspend` and ARE checked.
        foreach (var r in roots)
        {
            CheckNoDupLabels(pos + declLabel, r);
            CheckRefs(pos + declLabel, r, declared, labels, checkSuspension, which);
        }
    }

    // Does this declaration still carry `mods.suspend`? §2.1 makes `mods` the single source (a redundant top-level
    // `suspend` field was removed), so this reads the structured slot only.
    static bool IsSuspendDecl(JsonElement decl) =>
        decl.ValueKind == JsonValueKind.Object
        && decl.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object
        && mods.TryGetProperty("suspend", out var s) && s.ValueKind == JsonValueKind.True;

    // The #112 Phase-2 `File.kt:line: ` decl-source prefix, or "" when the decl carries no `pos`. Optional (absent =
    // pre-#112 behavior); a synthetic decl with no source simply omits it.
    static string PosPrefix(JsonElement decl)
    {
        if (decl.ValueKind != JsonValueKind.Object || !decl.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Object)
            return "";
        if (!pos.TryGetProperty("f", out var f) || f.ValueKind != JsonValueKind.String) return "";
        var file = System.IO.Path.GetFileName(f.GetString());
        if (pos.TryGetProperty("l", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var line) && line >= 0)
            return file + ":" + line + ": ";
        return file + ": ";
    }

    // Every name a `local`/`setLocal`/`byref` can resolve to: the `var` STATEMENT's `name`, and the STRING `var`
    // property carried by loop nodes (for/forArray/forRange/forEachInline/repeatInline) and `try` catch bindings.
    static void CollectDeclared(JsonElement node, HashSet<string> into)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "var"
                && node.TryGetProperty("name", out var nn) && nn.ValueKind == JsonValueKind.String)
                into.Add(nn.GetString());
            if (node.TryGetProperty("var", out var v) && v.ValueKind == JsonValueKind.String)
                into.Add(v.GetString());
            foreach (var p in node.EnumerateObject()) CollectDeclared(p.Value, into);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var x in node.EnumerateArray()) CollectDeclared(x, into);
    }

    // Collect every `label` node's id. Fully ValueKind-GUARDED (a non-string `k` / missing-or-non-int `id` is not a
    // valid label here and is skipped — a shape concern, owned by the schema validator).
    static void CollectSanityLabels(JsonElement node, HashSet<int> into)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "label"
                && node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var id))
                into.Add(id);
            foreach (var p in node.EnumerateObject()) CollectSanityLabels(p.Value, into);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var x in node.EnumerateArray()) CollectSanityLabels(x, into);
    }

    // A `label` id declared twice in one body -> the second MarkLabel throws ArgumentException at emit. Scoped per
    // single tree (label lifetimes are).
    static void CheckNoDupLabels(string decl, JsonElement node)
    {
        var seen = new HashSet<int>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "label"
                    && e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var id) && !seen.Add(id))
                    throw new IrSanityException(decl, $"duplicate CFG label id {id} in the same body");
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) Walk(x);
        }
        Walk(node);
    }

    static bool HasNonNull(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null;

    // Walk the tree, checking each node's MEANING invariant. Unmatched kinds (and type nodes, whose `k` vocabulary is
    // disjoint from these) just recurse.
    static void CheckRefs(string decl, JsonElement node, HashSet<string> declared, HashSet<int> labels, bool checkSuspension,
                          IrSanityChecks which)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("k", out var kEl) && kEl.ValueKind == JsonValueKind.String)
            {
                // 7. STAMP AGREEMENT — runs in BOTH modes and in EVERY scope this walk reaches: unlike check 6, its
                // subject is not what ilemit emits. `sty` is consumed by bir2cir's own type derivers, which walk every
                // body whatever its modifiers say, so no scope is exempt. (Which scopes the walk REACHES is a
                // separate, narrower question — see the LIMITS note on CheckStampAgreement.)
                CheckStampAgreement(decl, node, kEl.GetString());
                if (which == IrSanityChecks.StyStampsOnly)
                {
                    foreach (var p0 in node.EnumerateObject()) CheckRefs(decl, p0.Value, declared, labels, checkSuspension, which);
                    return;
                }
                // 6. SUSPENSION LOWERED. `suspendCall:true` is kotc's FRONTEND FACT that this call site suspends;
                // bir2cir's SuspendColdLowering is its only consumer, rewriting every suspending call into its cold
                // shape — a resume label plus a call to the callee's `$dotkt_suspend` cold entry, or the awaiter
                // sequence for a `.await()` CLR bridge — out of FRESH nodes that carry no tag. So a survivor in a
                // body that ilemit EMITS is a suspension that
                // ESCAPED the lowering, and ilemit, which has no notion of one, emits it as an ordinary invocation:
                // the caller reads the raw `Task`/COROUTINE_SUSPENDED sentinel where the awaited value belongs, and
                // the state machine never gets a resume point. Loud here beats an InvalidCastException at runtime.
                //
                // `checkSuspension` is false for exactly the bodies ilemit never emits — see CheckScope.
                if (checkSuspension && node.TryGetProperty("suspendCall", out var scEl) && scEl.ValueKind == JsonValueKind.True)
                    throw new IrSanityException(decl, $"'{kEl.GetString()}' still carries 'suspendCall': a suspension escaped the cold lowering (every suspending call must be rewritten into its cold Continuation shape before CIR)");
                switch (kEl.GetString())
                {
                    case "local":
                    case "setLocal":
                        if (node.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String && !declared.Contains(nEl.GetString()))
                            throw new IrSanityException(decl, $"'{kEl.GetString()}' references undeclared local '{nEl.GetString()}' (no matching var/param in scope)");
                        break;
                    case "byrefLoad":
                    case "byrefStore":
                        if (node.TryGetProperty("local", out var blEl) && blEl.ValueKind == JsonValueKind.String && !declared.Contains(blEl.GetString()))
                            throw new IrSanityException(decl, $"'{kEl.GetString()}' references undeclared local '{blEl.GetString()}' (no matching var/param in scope)");
                        break;
                    case "goto":
                    case "brIf":
                        if (node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var gid) && !labels.Contains(gid))
                            throw new IrSanityException(decl, $"'{kEl.GetString()}' targets CFG label id {gid} with no matching 'label' node in the body");
                        break;
                    case "binOp":
                        if (!HasNonNull(node, "lhs") || !HasNonNull(node, "rhs"))
                            throw new IrSanityException(decl, "'binOp' is missing an operand (requires both 'lhs' and 'rhs')");
                        break;
                    case "cond":
                        if (!HasNonNull(node, "cond") || !HasNonNull(node, "then") || !HasNonNull(node, "else"))
                            throw new IrSanityException(decl, "'cond' is missing 'cond'/'then'/'else'");
                        break;
                    case "field":
                    case "staticField":
                    case "setField":
                    case "setFieldExpr":
                    case "lateinitGet":
                        if (!HasNonNull(node, "ownerType"))
                            throw new IrSanityException(decl, $"'{kEl.GetString()}' is missing a non-null 'ownerType'");
                        break;
                    case "callStatic":
                        if (node.TryGetProperty("owner", out var callOwner) && callOwner.ValueKind == JsonValueKind.Null
                            && !HasNonNull(node, "calleeOwner"))
                            throw new IrSanityException(decl, "'callStatic' with owner:null is missing required 'calleeOwner'");
                        break;
                    case "newDelegate":
                    case "newBoundDelegate":
                        if (!HasNonNull(node, "calleeOwner"))
                            throw new IrSanityException(decl, $"'{kEl.GetString()}' is missing required 'calleeOwner'");
                        break;
                    case "for":
                        if (node.TryGetProperty("cmp", out var cmpEl) && cmpEl.ValueKind == JsonValueKind.String)
                        {
                            var cmp = cmpEl.GetString();
                            if (cmp != "<=" && cmp != "<" && cmp != ">=")
                                throw new IrSanityException(decl, $"'for' loop has unsupported 'cmp' operator '{cmp}' (expected '<=', '<', or '>=')");
                        }
                        break;
                }
            }
            foreach (var p in node.EnumerateObject()) CheckRefs(decl, p.Value, declared, labels, checkSuspension, which);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var x in node.EnumerateArray()) CheckRefs(decl, x, declared, labels, checkSuspension, which);
    }

    static HashSet<string> ParamNames(JsonElement m)
    {
        var s = new HashSet<string>();
        if (m.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                if (p.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String && pn.GetString().Length > 0)
                    s.Add(pn.GetString());
        return s;
    }

    // ==== CHECK 7 — `sty` vs `ret`/`dynRet` STAMP AGREEMENT (spec §2.7) ===========================================
    //
    // The invariant: A PASS THAT CHANGES A NODE'S RESULT TYPE REWRITES OR DELETES ITS `sty`. `sty` is a CLAIM about
    // the value the node produces now, not a historical note about the node it used to be, and every deriver reads it
    // FIRST (bir-common/NodeType.cs PRECEDENCE). A stale stamp is therefore not merely imprecise: a spill local or a
    // state-machine field declared from `List<Nullable<int32>>` while the call actually returns `List<object>` — two
    // UNRELATED invariant reified generics — is invalid IL, not a diagnosable drop (that is exactly what
    // NullableTvErasureCallRealign left behind before commit c17dea34).
    //
    // MISSING IS NOT DISAGREEMENT. A node with no `sty`, or with no `ret`/`dynRet`, is skipped: dropping the stamp is
    // one of the two things §2.7 permits a retyping pass to do, and a node that arrives with no stamp at all belongs
    // to the separate (already loud) stamp-drop family.
    //
    // ACCEPTED EQUIVALENCES — the relation is deliberately a REFUTATION test: it reports a violation only where two
    // CONCRETE, structurally comparable types are confidently different, and accepts everything else. `sty` is the
    // frontend's INSTANTIATED type and `ret` the callee's DECLARED one, so they differ legitimately in ways that are
    // not a difference of type IDENTITY. CALIBRATION is the gate itself — bir2cir runs this over every stdlib and
    // test-project file, so `make verify` reddens the moment the relation is too strict. The classes below were
    // enumerated from an offline study of 442 pre-lowering documents from the stdlib reference and runtime builds
    // (16,070 sty/ret pairs, 58 of them not byte-equal) plus the app corpus, and every legitimate difference in it
    // falls in one of them:
    //
    //   (a) A TYPE VARIABLE or a `*` projection on EITHER side matches anything. `ret` may name the UNinstantiated
    //       declared type (`Sequence<!0>` against an instantiated `Sequence<!1>`, `object` against a `!!0`), and a
    //       substituted `Map$Entry<K,V>` legitimately faces a deeper re-substituted spelling of the same nest. 50 of
    //       the 58 differing pairs are this.
    //   (b) The three spellings of one CLR type — `kotlin.Boolean` / `bool` / `System.Boolean`, `kotlin.Unit` /
    //       `void` / `System.Void`, `kotlin.Any` / `object`, … — are one canonical token. bir2cir lowers a type token
    //       when its pass runs, so a node retyped by an early pass and one retyped by a late pass carry the same type
    //       in different vocabularies (8 of the 58: `kotlin.Boolean` vs `bool`, `kotlin.Unit` vs `System.Void`).
    //   (c) `kotlin.Nothing` matches anything — the bottom type inhabits every slot, and it also erases to `object`.
    //   (d) A `$dotkt_star` name matches anything — those are the synthesized non-generic EXISTENTIAL views
    //       (ExistentialReceiverBinding / FBoundStarProjectionErasure) of a star-projected generic, i.e. a deliberate
    //       re-spelling of the same value (`kotlin.Comparator<!!0>` vs `kotlin.Comparator$dotkt_star`).
    //   (e) `nullable`/`oblivious` wrappers are stripped on both sides before comparing: nullability is an annotation
    //       axis that DeclNullableFlags/ReferenceNullableStrip move off the type at their own point in the pipeline,
    //       not a difference of which type the node produces. (Stripping does not ADD acceptance — it makes the type
    //       UNDER the wrapper comparable, so it is what lets `String?` vs `Int?` be refuted at all.)
    //   (f) `{t:array,elem:E}` and the name-keyed `kotlin.Array<E>` are the same type in two spellings (spec §2.7
    //       *One deriver, two layers* — `StaticType.Surface` mints the name-keyed one on purpose). Like (e), this
    //       makes the ELEMENTS comparable rather than accepting more.
    //   (g) Anything not structurally comparable — two different `t` discriminators, two `fqn`s of the same name with
    //       DIFFERENT arities, two `fn`s of different arity — is ACCEPTED. An erasure that drops or adds a generic
    //       argument is a shape this check declines to judge; the check exists for the class that bit in FU-⑧, which
    //       is a same-shape, same-arity pair whose ARGUMENT NAMES a different type.
    //
    // What is left is exactly the refutation: two `fqn`s whose canonical names differ, or whose same-arity arguments
    // recursively refute (likewise two `fn`s). Note which arms therefore do WORK: (e) and (f) are what make a
    // refutation REACHABLE through a wrapper or across the two array spellings — without them `String?` vs `Int?`
    // and `Array<String>` vs `int[]` would fall to the catch-all and be accepted — while (a) restates, for a reader,
    // an acceptance the catch-all already gives (a `tv` is neither `Fqn` nor `Fn`, so nothing refutes it). The
    // `tests/ir/selftest` fixtures pin (b), (c), (d), the arity guard in (g), and the two refutations (e)/(f) unlock.
    //
    // TWO LIMITS, stated because they bound what a green gate means. (1) `CanonName` knows the PRIMITIVE vocabulary
    // only, not the whole `@ClrTypeAlias` index (`kotlin.collections.List` vs `IReadOnlyList`, `kotlin.Throwable` vs
    // `System.Exception`): those conversions all live in `BirTypeLowering`, downstream of the chokepoint, so no node
    // can carry the two spellings on one pair yet — a pass that moved alias resolution earlier would have to extend
    // the table with it. (2) The declaration walk descends one level of `types`, so a stale stamp inside a NESTED
    // type synthesized by bir2cir is not reached; measured over the calibration corpus that set is empty (every
    // sty/ret pair in it is inside a top-level declaration), but it is a hole, not a proof.
    //
    // The whole corpus above is green under this — after the FOUR genuine violations calibration found, all fixed in
    // the same change (ContinuationErasure, NullableGenericErasure, ReferenceExistentialAbiBinding,
    // ConstructedMemberReturnSubstitution). Those four discharge §2.7 through `NodeType.DropStampIfStale`, which asks
    // `StampAgrees` itself, so this check cannot fire on them by construction — DELIBERATELY: a pass that complies is
    // the outcome, and the purpose of a chokepoint is the pass that has not been written yet. The cost of that choice
    // is that weakening this relation silently weakens those four repairs too, which is the reason it lives here,
    // stated once, rather than as a checker plus four restatements.
    static readonly string[] StampSlots = { "ret", "dynRet" };

    static void CheckStampAgreement(string decl, JsonElement node, string kind)
    {
        if (!node.TryGetProperty("sty", out var styEl) || styEl.ValueKind != JsonValueKind.Object) return;
        var sty = TryReadType(styEl);
        if (sty == null) return;
        foreach (var slot in StampSlots)
        {
            if (!node.TryGetProperty(slot, out var otherEl) || otherEl.ValueKind != JsonValueKind.Object) continue;
            var other = TryReadType(otherEl);
            if (other == null || StampAgrees(sty, other)) continue;
            throw new IrSanityException(decl,
                $"'{kind}' carries a stale 'sty': the stamp names {TypeNode.ToJson(sty)} while its '{slot}' names "
                + $"{TypeNode.ToJson(other)} — a pass that changes a node's result type must rewrite or delete its 'sty' (spec §2.7)");
        }
    }

    // A malformed type node is the SCHEMA validator's business (scripts/verify-schema.py), never this gate's — an
    // unreadable slot is SKIPPED rather than turned into a meaning violation, and rather than crashing the build with
    // whatever exception the reader happens to raise. `TypeNode.Read` signals malformed input three different ways
    // (FormatException for an unknown/absent `t`, KeyNotFoundException for a missing required property,
    // InvalidOperationException for a property of the wrong JSON kind), so the catch is deliberately total: the
    // python mirror's `isinstance` guards accept the same shapes, and a checker that is stricter than its mirror on
    // documents neither is meant to judge is a difference with no upside. The two do still SKIP different amounts on
    // such a document — an unreadable subtree makes this side abandon the whole node, while the mirror's guards
    // abandon only that subtree and keep comparing the heads — which is a difference of conservatism on input the
    // schema validator rejects first, not of the relation.
    static TypeNode TryReadType(JsonElement e)
    {
        try { return TypeNode.Read(e); }
        catch (Exception) { return null; }
    }

    // The canonical token for a named type, collapsing the kotlin.* / CLR-shorthand / System.* spellings of one CLR
    // type (equivalence (b)). The alphabet is bir2cir's `BirTypeLowering.KotlinAllToClr` and ilemit's
    // `PrimShorthandName` — the two ends this gate sits between — joined at the shorthand.
    static readonly IReadOnlyDictionary<string, string> CanonName = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.Int"] = "int", ["System.Int32"] = "int",
        ["kotlin.Long"] = "long", ["System.Int64"] = "long",
        ["kotlin.Short"] = "short", ["System.Int16"] = "short",
        ["kotlin.Byte"] = "sbyte", ["System.SByte"] = "sbyte",
        ["kotlin.Double"] = "double", ["System.Double"] = "double",
        ["kotlin.Float"] = "float", ["System.Single"] = "float",
        ["kotlin.Boolean"] = "bool", ["System.Boolean"] = "bool",
        ["kotlin.Char"] = "char", ["System.Char"] = "char",
        ["kotlin.String"] = "string", ["System.String"] = "string",
        ["kotlin.Any"] = "object", ["System.Object"] = "object",
        ["kotlin.Unit"] = "void", ["System.Void"] = "void",
        ["kotlin.UInt"] = "uint", ["System.UInt32"] = "uint",
        ["kotlin.ULong"] = "ulong", ["System.UInt64"] = "ulong",
        ["kotlin.UByte"] = "byte", ["System.Byte"] = "byte",
        ["kotlin.UShort"] = "ushort", ["System.UInt16"] = "ushort",
    };

    const string Bottom = "kotlin.Nothing";
    const string ExistentialMark = "$dotkt_star";

    static TypeNode Unwrap(TypeNode t)
    {
        while (true)
            switch (t)
            {
                case TypeNode.Nullable n: t = n.Of; break;
                case TypeNode.Oblivious o: t = o.Of; break;
                default: return t;
            }
    }

    /// <summary>
    /// TRUE unless the two types confidently name DIFFERENT types — the spec §2.7 stamp-agreement relation, every
    /// arm's rationale in the block above. PUBLIC because check 7 is not its only caller: a pass that retypes a
    /// node's result asks the same question this asks — *does the stamp still describe this value?* — to decide
    /// whether ITS change is what invalidated the stamp. That keeps a pass from dropping a stamp that is still the
    /// better answer (bir-common/NodeType.cs `DropStampIfStale`), and it means the relation exists once rather than
    /// as a checker and a differently-worded restatement inside each pass.
    /// </summary>
    public static bool StampAgrees(TypeNode a, TypeNode b)
    {
        a = Unwrap(a);
        b = Unwrap(b);
        if (a is TypeNode.Tv or TypeNode.Star || b is TypeNode.Tv or TypeNode.Star) return true;   // (a)
        if (a is TypeNode.Array aa)
            return b switch
            {
                TypeNode.Array ba => StampAgrees(aa.Elem, ba.Elem),
                TypeNode.Fqn { Name: "kotlin.Array", Args: { Length: 1 } bg } => StampAgrees(aa.Elem, bg[0]),   // (f)
                _ => true,                                                                                // (g)
            };
        if (b is TypeNode.Array bb)
            return a is TypeNode.Fqn { Name: "kotlin.Array", Args: { Length: 1 } ag } ? StampAgrees(ag[0], bb.Elem) : true;
        if (a is TypeNode.Fqn fa && b is TypeNode.Fqn fb)
        {
            if (fa.Name == Bottom || fb.Name == Bottom) return true;                                       // (c)
            if (fa.Name.Contains(ExistentialMark) || fb.Name.Contains(ExistentialMark)) return true;       // (d)
            if (Canon(fa.Name) != Canon(fb.Name)) return false;                                            // REFUTED
            if (fa.Args == null || fb.Args == null || fa.Args.Length != fb.Args.Length) return true;        // (g)
            for (var i = 0; i < fa.Args.Length; i++)
                if (!StampAgrees(fa.Args[i], fb.Args[i])) return false;
            return true;
        }
        if (a is TypeNode.Fn na && b is TypeNode.Fn nb)
        {
            if (!StampAgrees(na.Ret, nb.Ret)) return false;
            var pa = na.DelegateParams;
            var pb = nb.DelegateParams;
            if (pa.Length != pb.Length) return true;                                                       // (g)
            for (var i = 0; i < pa.Length; i++)
                if (!StampAgrees(pa[i], pb[i])) return false;
            return true;
        }
        return true;                                                                                       // (g)
    }

    static string Canon(string name) => CanonName.TryGetValue(name, out var c) ? c : name;
}
