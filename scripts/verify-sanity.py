#!/usr/bin/env python3
# CIR IR-SANITY validator — the OFFLINE mirror of the in-process bir-common IrSanity gate
# (toolchain/bir-common/IrSanity.cs, run by BOTH bir2cir and ilemit). Where verify-schema.py checks
# document SHAPE (canonical kinds, enums, types-are-nodes), THIS checks MEANING — the same semantic
# invariants the codegen relies on — over a json file or corpus, build-free, for CI/dev.
#
# SCOPE = POST-LOWERING CIR (the exact tree the in-process gate checks: bir2cir on its CIR output; ilemit
# at EmitAssembly). Do NOT feed it PRE-lowering BIR: a BIR inline-lambda body still references `it` / loop
# vars that bir2cir materializes as `var` statements during splice, so the local-resolution check would
# legitimately (falsely) trip. tests/ir/run-sanity.sh globs CIR only for this reason.
#
# Keep IN SYNC with toolchain/bir-common/IrSanity.cs (the C# is normative; this is the corpus net).
# DELIBERATELY CONSERVATIVE: every check is calibrated to NEVER false-positive on a valid input (the
# categorized compiler-test corpus + the 250-file stdlib rt build). Call/new args-vs-argTypes arity is intentionally NOT checked:
# callers may legitimately omit trailing default args.
#
# The check set (per method / ctor / static-field-initializer scope):
#   1. LOCAL RESOLUTION  — local/setLocal/byref{Load,Store} name a var/param declared in scope.
#   2. CFG TARGETS       — goto/brIf id has a matching `label`; no `label` id declared twice in one body.
#   3. STRUCTURAL        — binOp has lhs+rhs; cond has cond+then+else.
#   4. OWNER PRESENCE    — fields carry ownerType; owner:null callStatic/newDelegate/newBoundDelegate carry calleeOwner.
#   5. `for` cmp in {<=, <, >=}.
#   6. SUSPENSION LOWERED — no node in a body ilemit EMITS still carries suspendCall:true. No exemption:
#      every body a CIR document declares is one ilemit emits.
#   7. STAMP AGREEMENT — a node's `sty` does not name a DIFFERENT type than the `ret`/`dynRet` beside it
#      (spec 2.7). IrSanity.CheckStampAgreement carries the accepted-equivalence set and its corpus evidence;
#      `_stamps_agree` below is its mirror, arm for arm. NOTE that the emitted CIR corpus contains no `sty`
#      at all (BirTypeLowering strips it), so the CHOKEPOINT for this check is bir2cir's own pre-lowering
#      call — here it is pinned by the tests/ir/selftest fixtures, exactly like check 6.
#   8. COLLECTION VIEW COMPLETENESS — a type stating a MUTABLE collection face also states its READ-ONLY
#      sibling. The mirror of IrSanity.CheckCollectionViewFaces + bir-common/CollectionViewFaces.cs; unlike
#      1-7 it is a fact about a TYPE's `interfaces`, not about a body scope.
#   9. SUSPEND MODIFIER CONSUMED — no method DECLARATION (abstract or concrete) still carries mods.suspend:
#      the Kotlin modifier is bir2cir's to consume (cold lowering + the [KotlinFunction(Suspend)] stamp),
#      and CIR describes a physical CLR graph.
import json, sys, glob, os


# ---- check 8: the read-only sibling of a mutable collection face -----------------------------------------
# The mirror of bir-common/CollectionViewFaces.cs. `IList<T>` does not derive from `IReadOnlyList<T>` on the
# CLR, so a Kotlin value flowing from a mutable type into a read-only slot reaches a castclass that is total
# only when the type declares that face. bir2cir states it; ilemit must not infer it.
_READONLY_SIBLING = {
    "System.Collections.Generic.IList": "System.Collections.Generic.IReadOnlyList",
    "System.Collections.Generic.ICollection": "System.Collections.Generic.IReadOnlyCollection",
    "System.Collections.Generic.ISet": "System.Collections.Generic.IReadOnlyCollection",
}


def _readonly_sibling(face):
    """The read-only face a stated mutable collection face obliges, or None."""
    if not isinstance(face, dict) or face.get("t") != "fqn":
        return None
    args = face.get("args")
    if not isinstance(args, list) or len(args) != 1:
        return None
    name = _READONLY_SIBLING.get(face.get("name"))
    return None if name is None else {"t": "fqn", "name": name, "args": [args[0]]}


def _type_key(node):
    """Order-stable structural key for comparing two type nodes."""
    return json.dumps(node, sort_keys=True, separators=(",", ":"))


def _walk(node, fn):
    """Depth-first over the generic JSON tree, calling fn(obj) on every dict."""
    if isinstance(node, dict):
        fn(node)
        for v in node.values():
            _walk(v, fn)
    elif isinstance(node, list):
        for x in node:
            _walk(x, fn)


def _collect_declared(roots):
    names = set()
    def f(o):
        if o.get("k") == "var" and isinstance(o.get("name"), str):
            names.add(o["name"])
        if isinstance(o.get("var"), str):      # loop-var / catch-binding string carriers
            names.add(o["var"])
    for r in roots:
        _walk(r, f)
    return names


def _collect_labels(roots):
    ids = set()
    def f(o):
        if o.get("k") == "label" and isinstance(o.get("id"), int):
            ids.add(o["id"])
    for r in roots:
        _walk(r, f)
    return ids


# ---- check 7: `sty` vs `ret`/`dynRet` (spec 2.7) --------------------------------------------------------
# The mirror of IrSanity.CheckStampAgreement — read the block comment there for WHY each arm is what it is,
# and for the two LIMITS a green run does not cover. In short: the relation is a REFUTATION test that reports
# only two concrete, structurally comparable types that are confidently different, because `sty` is the
# frontend's INSTANTIATED type and `ret` the callee's DECLARED one, and they legitimately differ in ways that
# are not a difference of type IDENTITY. On a MALFORMED type node the two sides skip different amounts (this
# one abandons only the unreadable subtree, the C# abandons the whole node) — a difference of conservatism on
# input verify-schema rejects first, never of the relation.

# (b) the kotlin.* / CLR-shorthand / System.* spellings of one CLR type, collapsed to one token.
_CANON = {
    "kotlin.Int": "int", "System.Int32": "int",
    "kotlin.Long": "long", "System.Int64": "long",
    "kotlin.Short": "short", "System.Int16": "short",
    "kotlin.Byte": "sbyte", "System.SByte": "sbyte",
    "kotlin.Double": "double", "System.Double": "double",
    "kotlin.Float": "float", "System.Single": "float",
    "kotlin.Boolean": "bool", "System.Boolean": "bool",
    "kotlin.Char": "char", "System.Char": "char",
    "kotlin.String": "string", "System.String": "string",
    "kotlin.Any": "object", "System.Object": "object",
    "kotlin.Unit": "void", "System.Void": "void",
    "kotlin.UInt": "uint", "System.UInt32": "uint",
    "kotlin.ULong": "ulong", "System.UInt64": "ulong",
    "kotlin.UByte": "byte", "System.Byte": "byte",
    "kotlin.UShort": "ushort", "System.UInt16": "ushort",
}
_BOTTOM = "kotlin.Nothing"


def _unwrap(t):
    """(d) nullability is an annotation axis, not a difference of which type the node produces."""
    while isinstance(t, dict) and t.get("t") in ("nullable", "oblivious"):
        t = t.get("of")
    return t


def _fn_params(t):
    """The delegate ARG list — an extension receiver is the first argument (TypeNode.Fn.DelegateParams)."""
    ps = t.get("params")
    ps = list(ps) if isinstance(ps, list) else []
    return ([t["recv"]] + ps) if t.get("recv") is not None else ps


def _stamps_agree(a, b):
    """True unless the two types confidently name DIFFERENT types."""
    a, b = _unwrap(a), _unwrap(b)
    if not isinstance(a, dict) or not isinstance(b, dict):
        return True
    ta, tb = a.get("t"), b.get("t")
    if ta in ("tv", "star") or tb in ("tv", "star"):            # (a)
        return True
    if ta == "array" or tb == "array":                          # (e)
        arr, other = (a, b) if ta == "array" else (b, a)
        if other.get("t") == "array":
            return _stamps_agree(a.get("elem"), b.get("elem"))
        if other.get("t") == "fqn" and other.get("name") == "kotlin.Array" \
                and isinstance(other.get("args"), list) and len(other["args"]) == 1:
            return _stamps_agree(arr.get("elem"), other["args"][0])
        return True                                             # (f)
    if ta == "fqn" and tb == "fqn":
        na, nb = a.get("name"), b.get("name")
        if not isinstance(na, str) or not isinstance(nb, str):
            return True
        if na == _BOTTOM or nb == _BOTTOM:                      # (c)
            return True
        if _CANON.get(na, na) != _CANON.get(nb, nb):            # REFUTED
            return False
        aa, ab = a.get("args"), b.get("args")
        if not isinstance(aa, list) or not isinstance(ab, list) or len(aa) != len(ab):
            return True                                         # (f)
        return all(_stamps_agree(x, y) for x, y in zip(aa, ab))
    if ta == "fn" and tb == "fn":
        if not _stamps_agree(a.get("ret"), b.get("ret")):
            return False
        pa, pb = _fn_params(a), _fn_params(b)
        if len(pa) != len(pb):
            return True                                         # (f)
        return all(_stamps_agree(x, y) for x, y in zip(pa, pb))
    return True                                                 # (f)


def _compact(t):
    return json.dumps(t, separators=(",", ":"))


def _pos_prefix(decl):
    pos = decl.get("pos")
    if not isinstance(pos, dict) or not isinstance(pos.get("f"), str):
        return ""
    fname = os.path.basename(pos["f"])
    line = pos.get("l")
    if isinstance(line, int) and line >= 0:
        return f"{fname}:{line}: "
    return f"{fname}: "


class Sanity:
    def __init__(self):
        self.viol = []   # (file, decl, msg)

    def err(self, f, decl, msg):
        self.viol.append((f, decl, msg))

    def check_scope(self, f, decl_label, param_names, roots, decl):
        # Check 6 has no exemption: every scope this reaches is a body ilemit turns into IL — a method, a
        # constructor and a static-initializer group alike. IrSanity.CheckScope has the full reasoning.
        declared = set(param_names) | _collect_declared(roots)
        labels = _collect_labels(roots)
        dl = _pos_prefix(decl) + decl_label
        # dup labels — scoped per single tree
        for r in roots:
            seen = set()
            def dupf(o, _seen=seen, _dl=dl, _f=f):
                if o.get("k") == "label" and isinstance(o.get("id"), int):
                    if o["id"] in _seen:
                        self.err(_f, _dl, f"duplicate CFG label id {o['id']} in the same body")
                    _seen.add(o["id"])
            _walk(r, dupf)
        # meaning refs
        def reff(o, _dl=dl, _f=f):
            k = o.get("k")
            # 6. SUSPENSION LOWERED — kotc's frontend `suspendCall` fact is consumed by bir2cir's cold lowering,
            # which rebuilds the call out of fresh untagged nodes. A survivor means a suspension escaped it and
            # ilemit will emit an ordinary invocation with no resume point.
            if isinstance(k, str) and o.get("suspendCall") is True:
                self.err(_f, _dl, f"'{k}' still carries 'suspendCall': a suspension escaped the cold lowering "
                                  "(every suspending call must be rewritten into its cold Continuation shape before CIR)")
            # 7. STAMP AGREEMENT — unlike check 6 this asks of EVERY scope: `sty` is consumed by bir2cir's type
            # derivers, which walk every body whatever its modifiers say, so a stale stamp is a bug wherever it
            # sits. A MISSING stamp is not a disagreement (dropping it is what 2.7 permits) and is skipped.
            if isinstance(k, str) and isinstance(o.get("sty"), dict):
                for slot in ("ret", "dynRet"):
                    other = o.get(slot)
                    if isinstance(other, dict) and not _stamps_agree(o["sty"], other):
                        self.err(_f, _dl, f"'{k}' carries a stale 'sty': the stamp names {_compact(o['sty'])} "
                                          f"while its '{slot}' names {_compact(other)} — a pass that changes a node's "
                                          "result type must rewrite or delete its 'sty'")
            if k in ("local", "setLocal"):
                n = o.get("name")
                if isinstance(n, str) and n not in declared:
                    self.err(_f, _dl, f"'{k}' references undeclared local '{n}' (no matching var/param in scope)")
            elif k in ("byrefLoad", "byrefStore"):
                n = o.get("local")
                if isinstance(n, str) and n not in declared:
                    self.err(_f, _dl, f"'{k}' references undeclared local '{n}' (no matching var/param in scope)")
            elif k in ("goto", "brIf"):
                i = o.get("id")
                if isinstance(i, int) and i not in labels:
                    self.err(_f, _dl, f"'{k}' targets CFG label id {i} with no matching 'label' node in the body")
            elif k == "binOp":
                if o.get("lhs") is None or o.get("rhs") is None:
                    self.err(_f, _dl, "'binOp' is missing an operand (requires both 'lhs' and 'rhs')")
            elif k == "cond":
                if o.get("cond") is None or o.get("then") is None or o.get("else") is None:
                    self.err(_f, _dl, "'cond' is missing 'cond'/'then'/'else'")
            elif k in ("field", "staticField", "setField", "setFieldExpr"):
                if o.get("ownerType") is None:
                    self.err(_f, _dl, f"'{k}' is missing a non-null 'ownerType'")
            elif k == "lateinitGet":
                # A lateinitGet addresses the field itself and needs its owner — UNLESS the field content is already
                # supplied as 'value', which is what an accessor-routed read leaves behind: there is no field access
                # left in the node to name an owner for.
                if o.get("value") is None and o.get("ownerType") is None:
                    self.err(_f, _dl, "'lateinitGet' is missing a non-null 'ownerType'")
            elif k == "callStatic":
                if "owner" in o and o.get("owner") is None and o.get("calleeOwner") is None:
                    self.err(_f, _dl, "'callStatic' with owner:null is missing required 'calleeOwner'")
            elif k in ("newDelegate", "newBoundDelegate"):
                if o.get("calleeOwner") is None:
                    self.err(_f, _dl, f"'{k}' is missing required 'calleeOwner'")
            elif k == "for":
                cmp = o.get("cmp")
                if isinstance(cmp, str) and cmp not in ("<=", "<", ">="):
                    self.err(_f, _dl, f"'for' loop has unsupported 'cmp' operator '{cmp}' (expected '<=', '<', or '>=')")
        for r in roots:
            _walk(r, reff)

    @staticmethod
    def _param_names(m):
        s = set()
        for p in (m.get("params") or []):
            if isinstance(p, dict) and isinstance(p.get("name"), str) and p["name"]:
                s.add(p["name"])
        return s

    def check_container(self, f, owner, c, is_interface):
        for m in (c.get("methods") or []):
            if not isinstance(m, dict):
                continue
            name = m.get("name") if isinstance(m.get("name"), str) else "?"
            # 9. SUSPEND MODIFIER CONSUMED — asked BEFORE the bodiless early-outs: an abstract slot carries the
            # modifier just as a concrete one does.
            mods = m.get("mods")
            if isinstance(mods, dict) and mods.get("suspend") is True:
                self.err(f, _pos_prefix(m) + f"{owner}.{name}",
                         "declaration still carries 'mods.suspend': the Kotlin suspend modifier is consumed by "
                         "bir2cir's cold-core lowering and must not reach CIR (every suspend declaration becomes a "
                         "state machine, a cold entry and a Task bridge; one the stdlib self-build retains keeps "
                         "only its physical stub body)")
            if m.get("abstract") is True:
                continue
            body = m.get("body")
            if not isinstance(body, list):
                continue
            self.check_scope(f, f"{owner}.{name}", self._param_names(m), [body], m)
        for ct in (c.get("ctors") or []):
            if not isinstance(ct, dict):
                continue
            body = ct.get("body")
            if not isinstance(body, list):
                continue
            roots = [body]
            # `preStmts` (the delegation's call-evaluation plan lowered to `var`s, spec §2.7) is emitted in the same
            # frame, ahead of the delegating call: it DECLARES what thisArgs/baseArgs read.
            if isinstance(ct.get("preStmts"), list):
                roots.append(ct["preStmts"])
            if isinstance(ct.get("thisArgs"), list):
                roots.append(ct["thisArgs"])
            if isinstance(ct.get("baseArgs"), list):
                roots.append(ct["baseArgs"])
            self.check_scope(f, f"{owner}..ctor", self._param_names(ct), roots, ct)
        if not is_interface:
            inits = [fd["init"] for fd in (c.get("fields") or [])
                     if isinstance(fd, dict) and fd.get("init") is not None and fd.get("static") is True]
            if inits:
                # ilemit builds a type initializer from the fields alone; the CONTAINER's modifiers say nothing about it.
                self.check_scope(f, f"{owner}..cctor", set(), inits, c)

    def check_collection_view_faces(self, f, owner, t):
        ifaces = t.get("interfaces")
        if not isinstance(ifaces, list):
            return
        stated = {_type_key(i) for i in ifaces if isinstance(i, dict)}
        for i in ifaces:
            sibling = _readonly_sibling(i)
            if sibling is None or _type_key(sibling) in stated:
                continue
            self.err(f, owner,
                     f"type states the mutable collection face '{_type_key(i)}' without its read-only view "
                     f"'{_type_key(sibling)}'; the read-only face is a CLR representation decision bir2cir "
                     "must state (bir-common/CollectionViewFaces.cs), not one the emitter may infer")

    def check_file(self, f, doc):
        if not isinstance(doc, dict):
            return
        fc = doc.get("fileClass") if isinstance(doc.get("fileClass"), str) else "?"
        self.check_container(f, fc, doc, is_interface=False)
        for t in (doc.get("types") or []):
            if not isinstance(t, dict):
                continue
            tn = t.get("name") if isinstance(t.get("name"), str) else "?"
            self.check_collection_view_faces(f, tn, t)
            self.check_container(f, tn, t, is_interface=(t.get("kind") == "interface"))


def main(argv):
    files = []
    for a in argv:
        files.extend(sorted(glob.glob(a)))
    if not files:
        print("verify-sanity: no input files matched", file=sys.stderr)
        return 2
    s = Sanity()
    for f in files:
        try:
            d = json.load(open(f))
        except Exception as e:
            s.err(f, "", f"JSON parse failure: {e}")
            continue
        s.check_file(f, d)
    if s.viol:
        print(f"SANITY VIOLATIONS: {len(s.viol)} across {len(files)} files")
        for f, decl, m in s.viol[:40]:
            print(f"  {os.path.basename(f)}  |  {decl}  |  {m}")
        if len(s.viol) > 40:
            print(f"  ... and {len(s.viol) - 40} more")
        print(f"SANITY GATE: FAIL ({len(s.viol)} violations)")
        return 1
    print(f"SANITY GATE: PASS — {len(files)} files, 0 violations")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
