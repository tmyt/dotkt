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
//   - callStatic owner-presence: callStatic's owner is OPTIONAL (absent = a file-class sibling call), so requiring
//     it would false-reject every top-level-function call. (field/staticField/setField owners ARE mandatory.)
//   - call/`new` args-vs-argTypes arity: a caller may legitimately omit trailing DEFAULT args (args < argTypes),
//     and EmitNewArgs already tolerates the mismatch, so an equality check would false-reject valid CIR.
//
// The check set (all provably ilemit-equivalent — each mirrors a place ilemit already throws / miscompiles):
//   1. LOCAL RESOLUTION — every `local`/`setLocal`/`byref{Load,Store}` names a var/param declared in the same scope.
//   2. CFG TARGETS — every `goto`/`brIf` id has a matching `label` node in the body, and no `label` id is declared
//      twice in one body.
//   3. STRUCTURAL — `binOp` has both `lhs`+`rhs`; `cond` has `cond`+`then`+`else`.
//   4. OWNER PRESENCE — field/staticField/setField/setFieldExpr/lateinitGet carry a non-null `ownerType`.
//   5. `for` `cmp` ∈ {<=, <, >=} — an unknown cmp silently miscompiles to an infinite loop.
//
// SCOPE units mirror ilemit's `_locals`/`_cfgLabels` lifetimes exactly: a method = params ∪ body; a ctor ALSO folds
// in thisArgs/baseArgs (emitted in the same frame); the static-field-initializer group shares ONE .cctor `_locals`
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

public static class IrSanity
{
    // Run the sanity invariants over every method/ctor/static-field-initializer in the document, attributing each
    // violation to its declaration. Every check is intra-declaration and needs no codegen state.
    public static void Check(IEnumerable<JsonElement> files)
    {
        foreach (var file in files)
        {
            var fileClass = file.TryGetProperty("fileClass", out var fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() : "?";
            CheckContainer(fileClass, file, isInterface: false);
            if (file.TryGetProperty("types", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                {
                    var tn = t.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : "?";
                    var iface = t.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "interface";
                    CheckContainer(tn, t, iface);
                }
        }
    }

    // A file class OR a user type: its methods, its ctors, and its static-field-initializer group (one .cctor scope).
    static void CheckContainer(string owner, JsonElement c, bool isInterface)
    {
        if (c.TryGetProperty("methods", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray()) CheckMethodDecl(owner, m);
        if (c.TryGetProperty("ctors", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var ct in cs.EnumerateArray()) CheckCtorDecl(owner, ct);
        // Static field initializers share ONE .cctor `_locals` scope (a temp declared in field A's init is
        // resolvable from field B's) — check them as a single scope over the UNION of the inits.
        if (!isInterface && c.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            var inits = new List<JsonElement>();
            foreach (var f in fs.EnumerateArray())
                if (f.TryGetProperty("init", out var iv) && iv.ValueKind != JsonValueKind.Null
                    && f.TryGetProperty("static", out var st) && st.ValueKind == JsonValueKind.True)
                    inits.Add(iv);
            if (inits.Count > 0) CheckScope(owner + "..cctor", null, inits, decl: c);
        }
    }

    static void CheckMethodDecl(string owner, JsonElement m)
    {
        // Abstract / bodiless methods (interface members, abstract decls) emit no IL — nothing to check.
        if (m.TryGetProperty("abstract", out var ab) && ab.ValueKind == JsonValueKind.True) return;
        if (!m.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array) return;
        var name = m.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : "?";
        CheckScope(owner + "." + name, ParamNames(m), new List<JsonElement> { body }, decl: m);
    }

    static void CheckCtorDecl(string owner, JsonElement c)
    {
        if (!c.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array) return;
        var roots = new List<JsonElement> { body };
        // `: this(...)` / `: base(...)` args are emitted in the SAME frame as the body (before it), so a temp
        // declared inside them shares the ctor's `_locals` — fold them into the scope.
        if (c.TryGetProperty("thisArgs", out var ta) && ta.ValueKind == JsonValueKind.Array) roots.Add(ta);
        if (c.TryGetProperty("baseArgs", out var ba) && ba.ValueKind == JsonValueKind.Array) roots.Add(ba);
        CheckScope(owner + "..ctor", ParamNames(c), roots, decl: c);
    }

    // Validate one `_locals`/`_cfgLabels` scope: collect its declared local names + label ids across all root trees,
    // then check every reference against them. `decl` supplies the #112 Phase-2 source position for the message.
    static void CheckScope(string declLabel, HashSet<string> paramNames, List<JsonElement> roots, JsonElement decl)
    {
        var pos = PosPrefix(decl);
        var declared = paramNames != null ? new HashSet<string>(paramNames) : new HashSet<string>();
        var labels = new HashSet<int>();
        foreach (var r in roots) { CollectDeclared(r, declared); CollectSanityLabels(r, labels); }
        foreach (var r in roots)
        {
            CheckNoDupLabels(pos + declLabel, r);
            CheckRefs(pos + declLabel, r, declared, labels);
        }
    }

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
    static void CheckRefs(string decl, JsonElement node, HashSet<string> declared, HashSet<int> labels)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("k", out var kEl) && kEl.ValueKind == JsonValueKind.String)
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
                    case "for":
                        if (node.TryGetProperty("cmp", out var cmpEl) && cmpEl.ValueKind == JsonValueKind.String)
                        {
                            var cmp = cmpEl.GetString();
                            if (cmp != "<=" && cmp != "<" && cmp != ">=")
                                throw new IrSanityException(decl, $"'for' loop has unsupported 'cmp' operator '{cmp}' (expected '<=', '<', or '>=')");
                        }
                        break;
                }
            foreach (var p in node.EnumerateObject()) CheckRefs(decl, p.Value, declared, labels);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var x in node.EnumerateArray()) CheckRefs(decl, x, declared, labels);
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
}
