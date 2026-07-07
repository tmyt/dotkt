#!/usr/bin/env python3
# BIR/CIR schema validator — the #37 freeze ENFORCER (spec docs/bir-cir-spec.md §5/§7,
# normative schema docs/bir-cir.schema.json). Walks emitted BIR/CIR JSON documents and
# structurally enforces the frozen contract so any future drift reddens the gate:
#
#   1. TYPES ARE NODES (§1): every document type slot is a structured {t:...} node
#      (fqn/tv/fn/nullable/oblivious/array/byRef), NEVER a bare string. This is enforced
#      by an INVERSE allow-list — the finite set of keys that MAY carry a bare string is
#      fixed (STR_OK / STRARR_OK); a bare string at ANY other key is a type-token leak.
#   2. CANONICAL NODE KINDS (§2.5/§2.6): every {k:...} is in the frozen KINDS set and every
#      type tag {t:...} is in TYPE_TAGS — both lowerCamel; an unknown/typo'd/retired spelling reds.
#   3. WELL-FORMED TYPES (§1): each {t} carries its required fields with the right value shapes.
#   4. mods keys ⊆ MOD_KEYS, vis ∈ VIS (§2.1).
#
# The carrier (§0) — [KotlinInline]/[KotlinSuspendFunctionType] ride as CLR attributes on the
# emitted assembly, not as document nodes; their version is guarded loudly at decode time by
# bir-common BirCarrier.DecodeBody (an unknown version throws NotSupportedException) and is
# exercised end-to-end by verify-roundtrip. This document validator scopes to document nodes;
# the decoded carrier BODY is itself a node/type that also appears inline in the emitting
# method's body (validated here). See spec §7.
import json, sys, glob, os

TYPE_TAGS = {"fqn", "tv", "fn", "nullable", "oblivious", "array", "byRef"}

# Keys that legitimately hold a bare STRING scalar: format vocabulary (k/t tags, enums),
# object-language NAME payloads, and the documented owner/member/attribute reference
# strings (spec §2.2.1 — a type IDENTITY used as a resolution key, not a document value-type
# slot). A bare string at any OTHER key = a type node that regressed to a string.
STR_OK = {
    "k", "t",                                   # node-kind / type-tag (validated vs frozen sets)
    "name",                                     # decl/local/var/field names AND fqn.name (the type identity string)
    "scope",                                    # tv.scope enum
    "op", "cmp",                                # binOp/unaryOp operator / structured-for comparison operator
    "value",                                    # const literal / attribute-arg scalar
    "vis", "variance", "kind",                  # visibility / variance / decl-kind enums
    "member", "method", "get", "set", "event",  # member/accessor/event NAME references (reflection/override — §2.2.1)
    "local",                                    # a byref*/delegate node's local-VARIABLE-NAME reference
    "attr", "nestedIn",                         # attribute-type name / enclosing-type name (owner-FQN island — §2.2.1)
    "fileClass", "fileClassFQN", "pkg",         # file-class / package identifiers
    "var",                                      # loop-variable name (for*)
    "accessOwner", "firstM", "lastM", "stepM",  # forRange progression-accessor owner+method-name island (§2.2.1)
    "label",                                    # goto/brIf/label CFG target (opaque string — §3)
    "smName", "closureName", "coName",          # synthetic method/class names (opaque §3)
    # OWNER-FQN string islands (§2.2.1 — a type IDENTITY used as a resolution key, "owner stays string by m1
    # design"; NOT a document value-type slot). The dedicated owner keys are always owner-role, so allow-listing
    # them cannot mask a value-type leak (a value type is never keyed `owner`/`ownerType`/`clrOverride`).
    "owner",                                    # callStatic owner
    "ownerType",                                # callInstance/field/setField/staticField owner
    "clrOverride",                              # the CLR base type whose member a method overrides (override-target owner)
}
# On these CLR-lowered kinds the `type` field is the call's OWNER (not a value type) — the owner-FQN island
# (§2.2.1). Every OTHER kind's `type` is a value type and stays enforced. Their argTypes/ret/typeArgs remain
# enforced value/type-arg slots.
CLR_OWNER_KINDS = {
    "clrStatic", "clrInstance", "clrGenericStatic", "clrGenericInstance",
    "clrPropGet", "clrPropSet", "clrStaticField", "clrEventAdd", "clrEventRemove", "constrainedCall",
}
# Keys that legitimately hold an ARRAY containing bare strings: only the type-PARAMETER
# name-declaration shorthand (typeParams may be ["T"] instead of [{name:"T"}]). A type-param
# DECLARATION names a variable; references to it use positional tv{scope,i} nodes (§1), so this
# is a decl-name list, NOT a type-usage slot. `shapes` is the SIG-KEY reflection island (§2.2.1) — the
# clrGeneric* param-SHAPE tokens rendered type->string SOLELY to match a reflected MethodInfo, never a
# document type slot.
STRARR_OK = {"typeParams", "shapes"}

MOD_KEYS = {
    "inline", "infix", "operator", "tailrec", "external", "ext", "override", "abstract",
    "open", "suspend", "data", "sealed", "inner", "enum", "fun", "annotation", "value",
    "const", "lateinit", "vararg", "noinline", "crossinline",
}
VIS = {"public", "private", "protected", "internal"}
CARRIER_VERSIONS = {"bir-json/1"}

# The frozen node-kind set (§2.5) — the union of every kind the current toolchain emits across
# a full fresh build (stdlib + apps), post-m5 canonical spellings. An unknown k (a typo, or a
# retired spelling such as bin/un/isinst/isinstRef/setFieldExpr/staticFieldSet) reds the gate.
# Regenerate deliberately with:  scripts/verify-schema.py --dump-kinds <files...>
KINDS = {
    # --- core expr/stmt (kotc emit) ---
    "local", "const", "this", "var", "setLocal", "field", "setField", "staticField",
    "callInstance", "callStatic", "objMethod", "delegateInvoke",
    "binOp", "unaryOp", "conv", "cast", "isInst", "isInstRef", "objEq", "concat", "cond",
    "new", "newArray", "newArraySized", "newArrayInit", "arrayGet", "arraySet", "arrayLen",
    "newList", "newSet", "newMap", "newClosure", "newDelegate", "newSam", "newSuspendLambda",
    "newBoundDelegate", "newBoundClrDelegate",
    "enumValue", "enumOrdinal", "default", "defaultArg", "classRef", "console",
    "nullableWrap", "nullableValue", "nullableHasValue", "nullableNull",
    "block", "valueBlock", "exprStmt", "return", "returnExpr", "throw", "throwExpr",
    "if", "cond2", "while", "label", "goto", "brIf", "break", "continue",
    "for", "forRange", "forArray", "forEachInline", "try",
    # field-write family — the setField/setFieldExpr/staticFieldSet merge (§2.5) is "[finalize in impl]", so all
    # three remain LIVE kinds until that lands.
    "setFieldExpr", "staticFieldSet",
    "lateinitGet", "getType", "safeCastValue", "constrainedCall", "spreadConcat", "strReversed",
    "tupleItem", "unsupportedExpr",
    "stackAlloc", "stackGet", "stackSet", "stackAsSpan",
    "byrefOf", "byrefLoad", "byrefStore",
    # --- CLR-lowered (bir2cir → CIR) ---
    "newClr", "clrInstance", "clrStatic", "clrGenericStatic", "clrGenericInstance",
    "clrPropGet", "clrPropSet", "clrStaticField", "clrEventAdd", "clrEventRemove",
    # --- coroutine-lowered (bir2cir → CIR) ---
    "coReturn", "coSuspend", "coLabel", "coGoto", "coCondGoto", "coYield", "coYieldAll",
    "coTryBegin", "coCatchBegin", "coTryEnd",
}


class V:
    def __init__(self):
        self.viol = []          # (file, path, msg)
        self.kinds_seen = set()

    def err(self, f, path, msg):
        self.viol.append((f, path, msg))

    def type_node(self, f, path, o):
        """Validate a {t:...} type node: known tag + required fields (§1)."""
        t = o.get("t")
        if t not in TYPE_TAGS:
            self.err(f, path, f"unknown type tag t={t!r} (not in {sorted(TYPE_TAGS)})")
            return
        req = {
            "fqn": ["name"], "tv": ["scope", "i"], "fn": ["suspend", "ret", "params"],
            "nullable": ["of"], "oblivious": ["of"], "array": ["elem"], "byRef": ["of"],
        }[t]
        for r in req:
            if r not in o:
                self.err(f, path, f"type {t!r} missing required field {r!r}")
        if t == "tv":
            if o.get("scope") not in ("type", "method"):
                self.err(f, path, f"tv.scope={o.get('scope')!r} not in ['type','method']")
            if not isinstance(o.get("i"), int):
                self.err(f, path, f"tv.i must be int, got {o.get('i')!r}")
        if t == "fn" and not isinstance(o.get("suspend"), bool):
            self.err(f, path, f"fn.suspend must be bool, got {o.get('suspend')!r}")

    def walk(self, f, o, path):
        if isinstance(o, dict):
            if "t" in o and "k" in o:
                # disjoint structural roles (Codex-confirmed blind spot): a type node has `t`, an IR node has `k`;
                # an object carrying BOTH is ill-formed and must not slip past as either.
                self.err(f, path, f"object carries BOTH k={o.get('k')!r} and t={o.get('t')!r} (node/type roles are disjoint)")
            elif "t" in o:
                self.type_node(f, path, o)
            if isinstance(o.get("k"), str):
                k = o["k"]
                self.kinds_seen.add(k)
                if k not in KINDS:
                    self.err(f, path, f"unknown node kind k={k!r}")
            if isinstance(o.get("mods"), dict):
                for mk in o["mods"]:
                    if mk not in MOD_KEYS:
                        self.err(f, path + "/mods", f"unknown mod key {mk!r}")
            if isinstance(o.get("vis"), str) and o["vis"] not in VIS:
                self.err(f, path, f"unknown vis {o['vis']!r}")
            clr_owner = o.get("k") in CLR_OWNER_KINDS
            for key, val in o.items():
                p = path + "/" + key
                if isinstance(val, str):
                    if key == "type" and clr_owner:
                        pass  # clr*.type is the call OWNER (owner-FQN island §2.2.1), not a value type
                    elif key not in STR_OK:
                        self.err(f, p, f"bare STRING at type slot {key!r}: {val!r} (types must be {{t:...}} nodes)")
                elif isinstance(val, list):
                    for i, x in enumerate(val):
                        if isinstance(x, str) and key not in STRARR_OK:
                            self.err(f, p + f"[{i}]", f"bare STRING in type-array {key!r}: {x!r} (must be a {{t:...}} node)")
                        else:
                            self.walk(f, x, p + f"[{i}]")
                else:
                    self.walk(f, val, p)
        elif isinstance(o, list):
            for i, x in enumerate(o):
                self.walk(f, x, path + f"[{i}]")


def main(argv):
    if argv and argv[0] == "--dump-kinds":
        seen = set()
        for f in argv[1:]:
            for g in glob.glob(f):
                def w(o):
                    if isinstance(o, dict):
                        if isinstance(o.get("k"), str):
                            seen.add(o["k"])
                        for v in o.values():
                            w(v)
                    elif isinstance(o, list):
                        for x in o:
                            w(x)
                w(json.load(open(g)))
        print("\n".join(sorted(seen)))
        return 0

    files = []
    for a in argv:
        files.extend(sorted(glob.glob(a)))
    if not files:
        print("verify-schema: no input files matched", file=sys.stderr)
        return 2
    v = V()
    for f in files:
        try:
            d = json.load(open(f))
        except Exception as e:
            v.err(f, "", f"JSON parse failure: {e}")
            continue
        v.walk(f, d, "")
    # report
    if v.viol:
        # group by message-prefix for a readable summary; cap examples per kind
        from collections import Counter, defaultdict
        by = defaultdict(list)
        for f, p, m in v.viol:
            key = m.split(":")[0]
            by[key].append((f, p, m))
        print(f"SCHEMA VIOLATIONS: {len(v.viol)} across {len(files)} files")
        for key in sorted(by, key=lambda k: -len(by[k])):
            lst = by[key]
            print(f"  [{len(lst):5d}] {key}")
            for f, p, m in lst[:4]:
                print(f"           {os.path.basename(f)} @ {p[-90:]}  |  {m}")
        print(f"SCHEMA GATE: FAIL ({len(v.viol)} violations)")
        return 1
    print(f"SCHEMA GATE: PASS — {len(files)} files, {len(v.kinds_seen)} distinct node kinds, 0 violations")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
