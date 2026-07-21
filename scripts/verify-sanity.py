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
import json, sys, glob, os


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
            elif k in ("field", "staticField", "setField", "setFieldExpr", "lateinitGet"):
                if o.get("ownerType") is None:
                    self.err(_f, _dl, f"'{k}' is missing a non-null 'ownerType'")
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
            if m.get("abstract") is True:
                continue
            body = m.get("body")
            if not isinstance(body, list):
                continue
            name = m.get("name") if isinstance(m.get("name"), str) else "?"
            self.check_scope(f, f"{owner}.{name}", self._param_names(m), [body], m)
        for ct in (c.get("ctors") or []):
            if not isinstance(ct, dict):
                continue
            body = ct.get("body")
            if not isinstance(body, list):
                continue
            roots = [body]
            if isinstance(ct.get("thisArgs"), list):
                roots.append(ct["thisArgs"])
            if isinstance(ct.get("baseArgs"), list):
                roots.append(ct["baseArgs"])
            self.check_scope(f, f"{owner}..ctor", self._param_names(ct), roots, ct)
        if not is_interface:
            inits = [fd["init"] for fd in (c.get("fields") or [])
                     if isinstance(fd, dict) and fd.get("init") is not None and fd.get("static") is True]
            if inits:
                self.check_scope(f, f"{owner}..cctor", set(), inits, c)

    def check_file(self, f, doc):
        if not isinstance(doc, dict):
            return
        fc = doc.get("fileClass") if isinstance(doc.get("fileClass"), str) else "?"
        self.check_container(f, fc, doc, is_interface=False)
        for t in (doc.get("types") or []):
            if not isinstance(t, dict):
                continue
            tn = t.get("name") if isinstance(t.get("name"), str) else "?"
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
