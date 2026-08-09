#!/usr/bin/env python3
# BIR/CIR schema validator — the #37 freeze ENFORCER (spec docs/bir-cir-spec.md §5/§7,
# normative schema docs/bir-cir.schema.json). Walks emitted BIR/CIR JSON documents and
# structurally enforces the frozen contract so any future drift reddens the gate:
#
#   1. TYPES ARE NODES (§1): every document type slot is a structured {t:...} node
#      (fqn/tv/star/fn/nullable/oblivious/array/byRef), NEVER a bare string. This is enforced
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
# exercised end-to-end by the ProjectReference roundtrip NUnit suite. This document validator scopes to document nodes;
# the decoded carrier BODY is itself a node/type that also appears inline in the emitting
# method's body (validated here). See spec §7.
import json, sys, glob, os

TYPE_TAGS = {"fqn", "tv", "star", "fn", "nullable", "oblivious", "array", "byRef"}

# Keys that legitimately hold a bare STRING scalar: format vocabulary (k/t tags, enums),
# object-language NAME payloads, and the documented owner/member/attribute reference
# strings (spec §2.2.1 — a type IDENTITY used as a resolution key, not a document value-type
# slot). A bare string at any OTHER key = a type node that regressed to a string.
STR_OK = {
    "k", "t",                                   # node-kind / type-tag (validated vs frozen sets)
    "name",                                     # decl/local/var/field names AND fqn.name (the type identity string)
    "scope",                                    # tv.scope enum
    "op", "cmp",                                # binOp/unaryOp operator / structured-for comparison operator
    "value", "constant",                       # expression literal / attribute-arg scalar; CIR field Constant value
    "entry",                                    # enumValue's Kotlin entry-name identity
    "underlying", "physicalValue",              # resolved external-enum underlying CLR type + invariant integral
                                                # spelling (string preserves the full signed/unsigned 64-bit domain)
    "vis", "variance", "kind",                  # visibility / variance / decl-kind enums
    "clr",                                      # fn.clr: CIR-only physical delegate-family decision authored by
                                                # bir2cir; validated structurally + by BIR/CIR phase in type_node
    "dispatch",                                  # clrInstance dispatch enum ("call"/"callvirt"/"constrained") — a
                                                # bir2cir DECISION (W1-S2 #46), NOT a type slot; ilemit emits the opcode
                                                # verbatim (no re-derivation from reflected IsVirtual/IsFinal)
    "member", "method", "get", "set", "event",  # member/accessor/event NAME references (reflection/override — §2.2.1)
    "accessor",                                  # W1-S3 (#46/#121): the ref.dll-resolved get_/set_/add_/remove_ accessor
                                                # METHOD NAME ilemit links (clrPropGet/Set, clrEvent*, external field) — a
                                                # bir2cir resolution decision, NOT a type slot (paired with `member`+`dispatch`)
    "clrBridgeRole",                            # the reverse-enumerator-bridge role marker ("hasNext"/"next" on
                                                # kotlin.collections.Iterator's members, "iterator" on a class iterator())
                                                # bir2cir stamps so ilemit drives its GetEnumerator adapter off a semantic
                                                # marker, never the Kotlin FQN/member names. A resolution HINT, NOT a type
                                                # slot (never emitted as .NET metadata); ilemit reads it into TypeInfo.BridgeRoles
    "prop",                                     # callInstance/callStatic accessor KIND ("get"/"set"/"index-get"/"index-set")
                                                # — a BIR-only frontend fact (A2 step 3/4); bir2cir consumes it into
                                                # clrPropGet/clrPropSet (get/set) or the default-indexed-property accessor
                                                # (index-get/index-set), so it never survives to CIR. A marker, not a type slot.
    "id", "phase", "role",                      # §2.7 plan ids and lexical local-declaration ids
                                                # (`dotkt$bN` / `cir$bN`, also the `bindRef.id` that reads it), the
                                                # evaluation PHASE enum (recv/arg/default) and the source-level ROLE a
                                                # storage diagnostic names the value by ("receiver of 'copy'"). The
                                                # role travels onto the lowered `var`. Names/enums, not type slots.
    "local",                                    # a byref*/delegate node's local-VARIABLE-NAME reference
    "semanticOwner", "staticSemanticOwner",     # #225 BIR-only Kotlin lexical owner identities; the latter marks a
                                                # lifted implementation inside a Kotlin-static member (no owner T capture).
    "memberVisibility",                         # #225 BIR-only frontend visibility enum on a lexical member edge;
                                                # bir2cir consumes it into a caller-side UnsafeAccessor when needed.
    "companionSetterVisibility",                # #389 BIR-only visibility enum for a field-backed companion
                                                # extension var's default setter; bir2cir consumes it into the
                                                # C# 14 signature/implementation accessors.
    "memberOwnerTypeParams",                    # #225 BIR-only target-owner generic declaration facts.
    "memberMethodTypeParams",                   # #225 BIR-only target-method generic declaration facts.
    "sourceName",                              # #225 BIR-only lexical localFun source name.
    "typeFrame",                               # bir2cir-internal generic-frame vocabulary (currently "dense").
    "declaringLocalFunctionId",                # #225 optional lexical owner of a synthetic captured-var ref cell.
    "nestedIn",                                 # CIR enclosing-type name (owner-FQN island — §2.2.1). (The applied-attribute
                                                # `attr` type is now a structured `{t:fqn}` node — #48; kotc flags an
                                                # imported .NET attr with `attrClr:true`, which bir2cir AttrExternalNormalize
                                                # consumes into the `attrExternal` bool. No `clr:` prefix, no attr string.)
    "attrAssembly",                             # exact external custom-attribute declaration assembly identity;
                                                # disambiguates private same-FQN compiler-synthesized lookalikes. This
                                                # names a metadata scope, not a document value-type slot.
    "fileClass", "fileClassFQN", "pkg",         # file-class / package identifiers
    "f",                                        # #112 P2: the decl-level source-position FILE path (pos.{f,l,c});
                                                # `l`/`c` are ints. A diagnostics-only breadcrumb, NOT a type slot.
    "var",                                      # loop-variable name (for*)
    "firstM", "lastM", "stepM",                 # forRange progression-accessor method-name island (§2.2.1); the paired
                                                # `accessOwner` is now a structured `{t:fqn}` node (#48 S4), not a string
    "label",                                    # goto/brIf/label CFG target (opaque string — §3)
    "smName", "closureName", "coName",          # synthetic method/class names (opaque §3)
    "inlineBir",                                 # #71/#75: the raw-BIR [KotlinInline] carrier — an OPAQUE base64 string
                                                 # (base64(BirCarrier.EncodeBody(raw decl))) bir2cir InlineBirStash stamps on
                                                 # an inline method decl; ilemit emits it verbatim as the carrier bytes. A
                                                 # metadata payload, NOT a type slot (§3 opaque, like smName/closureName).
    "nullableGeneric", "nullableGenericRet",     # #18/#147: PRE-erasure `Holder<T?>` declaration-slot TypeNodes, stashed
                                                 # as OPAQUE canonical-JSON strings by bir2cir NullableGenericErasure
                                                 # before nested `Nullable(Tv)` is object-erased. RoundtripMetadata
                                                 # carrier-encodes them into [KotlinNullableGeneric] for dll2klib.
                                                 # Payloads, NOT type slots.
    "companionReceiver",                         # #382: the Kotlin type a COMPANION EXTENSION (`companion fun C.f()`)
                                                 # is associated with, stashed by kotc as a canonical-JSON string so the
                                                 # CLR type-lowering passes leave the KOTLIN identity untouched.
                                                 # RoundtripMetadata carrier-encodes it into [KotlinCompanionExtension]
                                                 # for dll2klib. A payload, NOT a value-type slot.
    "companionSourceName", "companionMemberKind", # #382: BIR-only source declaration identity and explicit
                                                 # function/get/set/field role. bir2cir selects a collision-free physical
                                                 # name without classifying ordinary get_/set_-prefixed functions.
    "collIdentity", "collIdentityRet",           # #29: PRE-collapse Kotlin collection TypeNodes stashed as canonical-JSON
                                                 # strings by CollectionIdentityRecord. RoundtripMetadata immediately turns
                                                 # them into [KotlinCollectionIdentity] carrier bytes for dll2klib; these
                                                 # are opaque metadata payloads, not the CIR parameter/return type slots.
    "bytes",                                     # #71 S2: a base64 attribute-arg VALUE (RoundtripMetadata) — the carrier
                                                 # payload for [KotlinInline]/[KotlinSuspendFunctionType] and the nested
                                                 # NullableAttribute(byte[]) form; ilemit's ConstArgValue decodes it to a
                                                 # real byte[] fixed argument. An opaque payload, NOT a type slot.
    "companionCaptureOwner", "externalCompanionOwner", # #275: temporary semantic companion association keys for
                                                # lifted/suspend callable-reference captures. Declaration/carrier
                                                # identities, not value-type slots; consumed before CIR.
    # OWNER-FQN owner slots (§2.2.1 — a type IDENTITY used as a resolution key) are ALL structured `{t:fqn}` nodes now
    # (#48 fully realized): `owner` (callStatic AND callInline.owner/callee), `ownerType`
    # (callInstance/field/setField/staticField, incl. the top-level-file-class + interop-extension owners kotc formerly
    # emitted as bare strings), `clrOverride`, `accessOwner`, clr* `type`. bir2cir reads them via TypeJson.Read/OwnerName
    # and ilemit via SlotName/ParseOwnerSlot/ClrRef — both node-native. No owner-FQN string slot remains.
}
# On these CLR-lowered kinds the `type` field is the call's OWNER (not a value type) — the owner-FQN island
# (§2.2.1). Every OTHER kind's `type` is a value type and stays enforced. Their argTypes/ret/typeArgs remain
# enforced value/type-arg slots.
CLR_OWNER_KINDS = {
    "clrStatic", "clrInstance", "clrDynInstance", "clrGenericStatic", "clrGenericInstance",
    "clrPropGet", "clrPropSet", "clrStaticField", "clrEventGet", "clrEventAdd", "clrEventRemove", "constrainedCall",
    "clrEventRaise",   # §4.3: the raise handle read — its `type` is the receiver's owner FQN (BIR-only; bir2cir -> callInstance)
}
# Keys that legitimately hold an ARRAY containing bare strings: only the type-PARAMETER
# name-declaration shorthand (typeParams may be ["T"] instead of [{name:"T"}]). A type-param
# DECLARATION names a variable; references to it use positional tv{scope,i} nodes (§1), so this
# is a decl-name list, NOT a type-usage slot. (The clrGeneric* overload key is now the STRUCTURED `memberSig`
# TypeNode array — W1-S1 #46 — walked as ordinary type nodes; the retired lossy `shapes` string island is gone.)
STRARR_OK = {
    "typeParams",
    "typeParamDecls",                          # newSuspendLambda's full declaration-form copy of typeParams;
                                                # bare names are the same declaration shorthand, not type usages.
    "capturedTypeParams",                       # #275: enclosing CLR generic-slot declaration names copied onto
                                                # the nested companion carrier, not Type usages.
    "specialConstraints",                      # CIR-only CLR generic-param flags (class/struct/new/allows-ref-struct), copied from a
                                                # referenced receiver onto a C# 14 extension grouping/wrapper.
    "memberOwnerTypeParams",                    # #225: declaration-form owner frame carried on a member edge.
    "memberMethodTypeParams",                   # #225: declaration-form method frame carried on a member edge.
}

MOD_KEYS = {
    "inline", "infix", "operator", "tailrec", "external", "ext", "override", "abstract",
    "open", "suspend", "data", "sealed", "inner", "enum", "fun", "annotation", "value",
    "object", "const", "lateinit", "vararg", "noinline", "crossinline",
    "inlineOnly",                                # #98: @InlineOnly → [MethodImpl(AggressiveInlining)] (ilemit reads mods.inlineOnly)
    "context",                                   # a Kotlin CONTEXT parameter (a param-only mod; bir2cir turns it into
                                                 # the [KotlinContextParameter] marker projected into reference KLIBs)
}
VIS = {"public", "private", "protected", "internal", "protectedInternal"}
CARRIER_VERSIONS = {"bir-json/1"}

# The frozen node-kind set (§2.5) — the union of every kind the current toolchain emits across
# a full fresh build (stdlib + apps), post-m5 canonical spellings. An unknown k (a typo, or a
# retired spelling such as bin/un/isinst/isinstRef/setFieldExpr/staticFieldSet) reds the gate.
# Regenerate deliberately with:  scripts/verify-schema.py --dump-kinds <files...>
KINDS = {
    # --- core expr/stmt (kotc emit) ---
    "local", "const", "this", "var", "setLocal", "field", "setField", "staticField",
    "callInstance", "callStatic", "callLocal", "localFunRef", "objMethod", "delegateInvoke",
    "binOp", "unaryOp", "conv", "cast", "isInst", "isInstRef", "objEq", "concat", "cond",
    "new", "newArray", "newArraySized", "newArrayInit", "arrayGet", "arraySet", "arrayLen",
    "newList", "newSet", "newMap", "newClosure", "newDelegate", "newSam", "newSuspendLambda",
    "newBoundDelegate", "newBoundClrDelegate", "newClrStaticDelegate",
    "companionValue",                           # #275 BIR-only semantic access; bir2cir resolves the nested carrier.
    "enumValue", "enumValues", "enumParse", "enumOrdinal", "default", "defaultArg", "classRef", "console",
    # §2.7 CALL-EVALUATION PLAN — BIR-only. `callEval` wraps a call in its ordered bindings; `bindRef` is a pure READ
    # of one. bir2cir's CallEvalLowering lowers both (to `var`+`valueBlock`, or to a ctor's `preStmts`) before CIR.
    "callEval", "bindRef",
    "nullableWrap", "nullableValue", "nullableHasValue", "nullableNull",
    "localFun", "block", "valueBlock", "exprStmt", "return", "returnExpr", "throw", "throwExpr",
    "if", "cond2", "while", "label", "goto", "brIf", "break", "continue",
    "for", "forRange", "forArray", "forEachInline", "forIn", "repeatInline", "callInline", "inlineLambda", "try",
    # field-write family — the setField/setFieldExpr/staticFieldSet merge (§2.5) is "[finalize in impl]", so all
    # three remain LIVE kinds until that lands.
    "setFieldExpr", "staticFieldSet",
    "lateinitGet", "getType", "safeCastValue", "constrainedCall", "spreadConcat",
    "tupleItem", "unsupportedExpr",
    "stackAlloc", "stackGet", "stackSet", "stackAsSpan",
    "byrefOf", "byrefLoad", "byrefStore",
    # kotc-dialect CLR-only-vocab: a .NET event READ (`w.Changed`), the ClrEvent<T> handle. Emitted by kotc (a .NET
    # event has no plain-Kotlin call form), consumed by bir2cir ClrEventOperatorBinding with the +=/-= into
    # clrEventAdd/Remove — never reaches ilemit/CIR. (byref/ClrRef are the other two CLR-only-vocab forms.)
    "clrEventGet",
    # kotc-dialect CLR-only-vocab for the .NET-event IMPLEMENT/RAISE feature (§4.2/§4.3, #187). BIR-only (kotc -> bir2cir):
    #   clrEventBacking  — a per-event `by clrEvent()` backing directive (in a type's `clrEvents` array; carries the handler
    #                      Kotlin fn type). bir2cir ClrEventImplBinding -> a real `<E>$delegate` field + a `clrEventDecl`.
    #   clrEventAccessor — the tagged body of a synthesized add_/remove_/raise_<E> accessor. bir2cir -> clrEventAccessorImpl.
    #   clrEventRaise    — a Kotlin-declared event handle `.invoke(...)`. bir2cir -> a `callInstance raise_<E>`.
    "clrEventBacking", "clrEventAccessor", "clrEventRaise",
    # --- CLR-lowered (bir2cir → CIR) ---
    "newClr", "clrInstance", "clrStatic", "clrGenericStatic", "clrGenericInstance",
    "clrDynInstance",   # W1-S2 (#46): a clrInstance on an interface owner with no static BCL slot — a DELIBERATE
                        # runtime-reflection dispatch node (replacing ilemit's former silent EmitDynamicCall downgrade)
    "clrPropGet", "clrPropSet", "clrStaticField", "clrEventAdd", "clrEventRemove",
    # .NET-event IMPLEMENT (§4.2, #187): the bir2cir-lowered CIR forms of the synthesis — a resolved accessor body (CAS
    # loop / raise, carrying the backing field + concrete delegate D) and a type-level `.event` metadata record.
    "clrEventAccessorImpl", "clrEventDecl",
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
            "star": [],
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
        if t == "fn":
            if not isinstance(o.get("suspend"), bool):
                self.err(f, path, f"fn.suspend must be bool, got {o.get('suspend')!r}")
            clr = o.get("clr")
            delegate_families = {
                "System.Func", "System.Action",
                "DotKt.Runtime.CompilerServices.KFunc", "DotKt.Runtime.CompilerServices.KAction",
            }
            if clr is not None and clr not in delegate_families:
                self.err(f, path, f"fn.clr={clr!r} is not a supported CIR delegate family")
            if f.endswith(".bir.json") and clr is not None:
                self.err(f, path, "fn.clr is CIR-only and must not appear in kotc BIR")
            # suspendFnType is pre-erasure Kotlin metadata carried for [KotlinSuspendFunctionType];
            # it is not a CLR value/delegate slot and intentionally keeps the pure Kotlin fn shape.
            if f.endswith(".cir.json") and clr is None and "SuspendFnType" not in path and "suspendFnType" not in path:
                self.err(f, path, "ilemit-facing CIR fn is missing required fn.clr delegate family")
        if t == "star" and f.endswith(".cir.json"):
            self.err(f, path, "Kotlin star projection must be lowered by bir2cir before CIR")

    def plan_scope(self, f, o, path, bound):
        """§2.7 NESTING RULE: every `bindRef` resolves OUTWARD to an enclosing plan's binding.

        A plan may nest — a default that is itself a call with defaults, and an inline splice's own bindings wrapped
        around the spliced block that reads its caller's — and `CallEvalLowering` lowers the inner one first, leaving
        an unknown id alone precisely because it belongs to a plan further out. That is only sound while every id
        HAS an enclosing declaration: a `bindRef` naming nothing is a value that will never be evaluated, and the
        chokepoint at the end of the pass would report it as an unlowerable leftover with no way back to the producer.
        Checking it here names the producer's own document instead.

        A binding's own `expr` reads only bindings DECLARED BEFORE IT (Kotlin defaults reference earlier parameters
        only), so each binding is checked against the prefix of its plan, not the whole of it.
        """
        if isinstance(o, dict):
            plan = o.get("bindings") if o.get("k") == "callEval" else o.get("delegationBindings")
            if isinstance(plan, list):
                inner = bound
                for i, b in enumerate(plan):
                    if not isinstance(b, dict):
                        continue
                    self.plan_scope(f, b.get("expr"), f"{path}/bindings[{i}]/expr", inner)
                    if isinstance(b.get("id"), str):
                        inner = inner | {b["id"]}
                for key, val in o.items():
                    if key not in ("bindings", "delegationBindings"):
                        self.plan_scope(f, val, path + "/" + key, inner)
                return
            if o.get("k") == "bindRef":
                if o.get("id") not in bound:
                    self.err(f, path, f"bindRef id={o.get('id')!r} resolves to no enclosing call-evaluation plan "
                                      "(§2.7 nesting rule: a bindRef reads a binding of an ancestor plan)")
                return
            for key, val in o.items():
                self.plan_scope(f, val, path + "/" + key, bound)
        elif isinstance(o, list):
            for i, x in enumerate(o):
                self.plan_scope(f, x, path + f"[{i}]", bound)

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
                if k == "newSuspendLambda":
                    # The physical SM parameter descriptors and the semantic Kotlin function type deliberately use
                    # different shapes: node.params is receiver-first (create arguments/field names), while
                    # funcType.recv owns the extension receiver and funcType.params contains regular params only.
                    # Enforce their one-to-one projection so nobody can reintroduce the former flattened/side-band
                    # receiver encoding without reddening the wire-contract gate.
                    ft = o.get("funcType")
                    ps = o.get("params")
                    arity = o.get("arity")
                    if not isinstance(ft, dict) or ft.get("t") != "fn" or ft.get("suspend") is not True:
                        self.err(f, path, "newSuspendLambda.funcType must be a suspend fn type")
                    elif isinstance(ps, list):
                        semantic = ([] if ft.get("recv") is None else [ft["recv"]]) + (
                            ft.get("params") if isinstance(ft.get("params"), list) else []
                        )
                        physical = [p.get("type") if isinstance(p, dict) else None for p in ps]
                        if physical != semantic:
                            self.err(
                                f, path,
                                "newSuspendLambda params must equal [funcType.recv?] + funcType.params"
                            )
                        if ft.get("ret") != o.get("suspendRet"):
                            self.err(f, path, "newSuspendLambda.suspendRet must equal funcType.ret")
                    else:
                        self.err(f, path, "newSuspendLambda.params must be an array")
                    if not isinstance(arity, int) or not isinstance(ps, list) or arity != len(ps):
                        self.err(f, path, "newSuspendLambda.arity must equal the physical params length")
            # §2.7 PHASE SPLIT. The call-evaluation plan is BIR vocabulary: `callEval`/`bindRef` and the ctor
            # declaration's `delegationBindings` are lowered by CallEvalLowering, so a survivor in CIR means a plan
            # reached ilemit, which has no notion of one. `preStmts` is the CIR form of a delegation's plan and is
            # authored by that same pass, so it must not appear in kotc's BIR.
            if f.endswith(".cir.json"):
                if o.get("k") in ("callEval", "bindRef"):
                    self.err(f, path, f"{o['k']!r} is a BIR call-evaluation plan node and must be lowered before CIR")
                if "delegationBindings" in o:
                    self.err(f, path, "delegationBindings is a BIR call-evaluation plan and must be lowered to preStmts before CIR")
                if o.get("k") == "companionValue":
                    self.err(f, path, "companionValue is a BIR semantic node and must be lowered before CIR")
                if o.get("k") in ("localFun", "callLocal", "localFunRef"):
                    self.err(f, path, f"{o['k']} is a BIR lexical declaration fact and must be lowered before CIR")
                for companion_key in ("kotlinCompanion", "companionCaptureOwner", "externalCompanionOwner",
                                      "companionReceiver", "companionSourceName", "companionMemberKind",
                                      "companionPropertyMutable", "companionSetterVisibility",
                                      "companionStorageReadOnly"):
                    if companion_key in o:
                        self.err(f, path, f"{companion_key} is a BIR companion fact and must be consumed before CIR")
                for ownership_key in ("semanticOwner", "staticSemanticOwner", "outerTypeParamCount", "outerTypeParamOffset", "typeParamDecls", "lexicalOwnerTypeParamCount"):
                    if ownership_key in o:
                        self.err(f, path, f"{ownership_key} is a BIR ownership fact and must be consumed before CIR")
                for member_fact in ("memberVisibility", "memberType", "memberOwnerTypeParams",
                                    "memberMethodTypeParams", "memberReturnType", "memberSignature"):
                    if member_fact in o:
                        self.err(f, path, f"{member_fact} is a BIR frontend access fact and must be consumed before CIR")
            if f.endswith(".bir.json") and "preStmts" in o:
                self.err(f, path, "preStmts is the bir2cir-authored lowering of a delegation plan and must not appear in BIR")
            if f.endswith(".bir.json"):
                if o.get("k") == "newClrStaticDelegate":
                    self.err(f, path, "newClrStaticDelegate is a bir2cir-authored physical node and must not appear in BIR")
                if "capturedTypeParams" in o:
                    self.err(f, path, "capturedTypeParams is a bir2cir-authored nested CLR declaration fact and must not appear in BIR")
                if "nestedIn" in o:
                    self.err(f, path, "nestedIn is a bir2cir-authored physical CLR ownership fact and must not appear in BIR")
            if o.get("k") == "newClrStaticDelegate" and f.endswith(".cir.json"):
                if not isinstance(o.get("memberSig"), list):
                    self.err(f, path, "newClrStaticDelegate.memberSig must be a resolved Type-node array in CIR")
                if not isinstance(o.get("memberOwner"), dict) or "t" not in o["memberOwner"]:
                    self.err(f, path, "newClrStaticDelegate.memberOwner must be a resolved Type node in CIR")
            if isinstance(o.get("mods"), dict):
                for mk in o["mods"]:
                    if mk not in MOD_KEYS:
                        self.err(f, path + "/mods", f"unknown mod key {mk!r}")
            if isinstance(o.get("vis"), str) and o["vis"] not in VIS:
                self.err(f, path, f"unknown vis {o['vis']!r}")
            if o.get("vis") == "protectedInternal" and f.endswith(".bir.json"):
                self.err(f, path, "protectedInternal is a bir2cir-authored CIR visibility and must not appear in BIR")
            clr_owner = o.get("k") in CLR_OWNER_KINDS
            for key, val in o.items():
                p = path + "/" + key
                if isinstance(val, str):
                    if key == "type" and clr_owner:
                        pass  # clr*.type is the call OWNER (owner-FQN island §2.2.1), not a value type
                    elif path.endswith("/kotlinCompanion") and key in ("owner", "visibility"):
                        # A declaration fact, not an owner Type slot. Keep the exception path-scoped so a bare
                        # `owner` elsewhere still reddens the gate.
                        if key == "visibility" and val not in VIS - {"protectedInternal"}:
                            self.err(f, p, f"unknown Kotlin companion visibility {val!r}")
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
        if f.endswith(".bir.json"):
            v.plan_scope(f, d, "", frozenset())
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
