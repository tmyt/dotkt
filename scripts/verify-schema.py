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
#   4. mods keys ⊆ MOD_KEYS, vis ∈ VIS (§2.1), and `mods.suspend` — Kotlin vocabulary bir2cir consumes —
#      appears in BIR only.
#   5. The current rich-enum declaration carrier has its exact frozen shape and is consumed before CIR.
#
# The carrier (§0) — [KotlinInline]/[KotlinSuspendFunctionType] ride as CLR attributes on the
# emitted assembly, not as document nodes; their version is guarded loudly at decode time by
# bir-common BirCarrier.DecodeBody (an unknown version throws NotSupportedException) and is
# exercised end-to-end by the ProjectReference roundtrip NUnit suite. This document validator scopes to document nodes;
# the decoded carrier BODY is itself a node/type that also appears inline in the emitting
# method's body (validated here). See spec §7.
import json, re, sys, glob, os

TYPE_TAGS = {"fqn", "tv", "star", "fn", "nullable", "oblivious", "array", "byRef", "ptr", "mod"}

# #370: the document keys that carry a scalar `memberRef` — ONE complete, already-resolved reference to a
# member of another assembly. Frozen like KINDS/TYPE_TAGS, so a new carrier key is a deliberate vocabulary
# change. `declaringType` is the shape's discriminator (no other document shape has it), which is what
# catches a parallel member-identity spelling invented under some other key.
MEMBER_REF_KEYS = {"memberRef", "baseCtorRef", "clrOverrideRef", "ctorRef", "addRef", "setItemRef", "addRangeRef", "toArrayRef",
                    "enumerableGetRef", "enumerableGetErasedRef", "currentRef", "currentErasedRef", "moveNextRef",
                    "combineRef", "removeRef", "compareExchangeRef", "hasValueRef", "valueRef", "invokeRef", "delegateCtorRef",
                    "fieldRef"}

MEMBER_REF_KINDS = {"method", "ctor", "field", "propertyAccessor", "eventAccessor"}

# What each carrier is allowed to hold. A carrier names a specific ROLE — a base-constructor delegation, an
# override target — and a reference of the wrong kind under it is a complete, well-formed, unusable identity.
MEMBER_REF_KIND_BY_CARRIER = {
    "baseCtorRef": {"ctor"},
    "clrOverrideRef": {"method", "propertyAccessor", "eventAccessor"},
    "ctorRef": {"ctor"},
    "addRef": {"method"},
    "setItemRef": {"method", "propertyAccessor"},
    "addRangeRef": {"method"},
    "toArrayRef": {"method"},
    "enumerableGetRef": {"method"},
    "enumerableGetErasedRef": {"method"},
    "currentRef": {"method", "propertyAccessor"},
    "currentErasedRef": {"method", "propertyAccessor"},
    "moveNextRef": {"method"},
    "combineRef": {"method"},
    "removeRef": {"method"},
    "compareExchangeRef": {"method"},
    "hasValueRef": {"method", "propertyAccessor"},
    "valueRef": {"method", "propertyAccessor"},
    "invokeRef": {"method"},
    "delegateCtorRef": {"ctor"},
    "fieldRef": {"field"},
}

# A collection literal says what to BUILD; these name the members it builds THROUGH. Both are required on such
# a node, because an emitter handed one and not the other is back to filling the gap by name.
COLLECTION_TEMPLATE_REFS = {
    "newList": ("ctorRef", "addRef"),
    "newSet": ("ctorRef", "addRef"),
    "newMap": ("ctorRef", "setItemRef"),
    # A spread argument accumulates into a list and hands over its array: four members, same rule.
    "spreadConcat": ("ctorRef", "addRef", "addRangeRef", "toArrayRef"),
    # An inlined `for` walks the enumerator protocol; both arms are named because which one the emitter can
    # speak is a Reflection.Emit fact, not a choice about which member is meant.
    "forEachInline": ("enumerableGetRef", "enumerableGetErasedRef", "currentRef", "currentErasedRef", "moveNextRef"),
}

# Other operation-local roles whose consumer emits an external operand. These live on the operation rather than
# on a child expression: a child can be a plain local/field value and therefore has no invocation role of its own.
REQUIRED_OPERATION_REFS = {
    "newArrayInit": ("invokeRef",),
    "clrStaticField": ("fieldRef",),
}

# Required fields whose role is owned by the node discriminator. In particular, `to` cannot be a global type key:
# conv.to is a Type while for/forRange.to is an expression. Keep these checks kind-directed for the same reason.
REQUIRED_NODE_FIELDS = {
    "conv": ("e", "to"),
    "enumBits": ("e", "type", "underlying"),
    "new": ("type", "args"),
    "constrainedCall": ("args",),
}

# The per-document table of fixed BCL members a Kotlin operation expands into (#370). Keyed by ROLE.
#
# The role set is FROZEN, for the reason every other key here is: a table that accepts any name accepts a typo,
# and a typo'd role resolves to nothing at emit time with the producer long out of the picture. It is CIR-only —
# nothing resolves members before bir2cir runs — and every value is a complete reference.
WELL_KNOWN_TABLE = "wellKnownRefs"

# Every slot of every external interface a type declares, named on the type. CIR-only, like every resolved
# identity: nothing resolves a member before bir2cir runs.
INTERFACE_SLOTS = "interfaceSlotRefs"

WELL_KNOWN_ROLES = frozenset({
    "String.ConcatArray", "Type.FromHandle", "Object.GetType", "Object.ToString", "Object.GetHashCode",
    "Object.Equals", "Enum.GetValues", "Enum.Parse",
    "Array.IndexOf",
    "Object.ctor",
    "IndexOutOfRangeException.ctor",
    "NullableT.ctor", "SpanT.ctorPointer",
})

# Pass-to-pass facts owned exclusively by bir2cir. They are deliberately not part of either serialized phase:
# seeing one in an input BIR or output CIR means an internal resolution step leaked across a layer boundary.
BIR2CIR_INTERNAL_MEMBER_FACTS = (
    "resolvedMemberParams", "resolvedMemberReturn",
    "pendingOverrideOwner", "pendingOverrideMember", "pendingOverrideReturn",
)

# Temporary facts used while bir2cir is reshaping generic declarations. They may be represented in the shared
# in-memory codec, but neither serialized input BIR nor emitted CIR owns them.
BIR2CIR_INTERNAL_TYPE_FACTS = ("erasedInnerConstraints",)


def in_member_ref(path):
    """True when `path` points inside ANY member-reference carrier (derived, never a literal key name)."""
    return (any(("/" + key) in path for key in MEMBER_REF_KEYS)
            or "/" + WELL_KNOWN_TABLE + "/" in path
            or ("/" + INTERFACE_SLOTS + "[" in path and "/slots[" in path))

# HASTHIS, plus whether the signature is vararg — a vararg member is a DIFFERENT member from its fixed-arity
# neighbour, so the convention states it rather than the producer refusing to describe it.
MEMBER_REF_CONVENTIONS = {"static", "instance", "varargStatic", "varargInstance"}


def arity_of_name(full_name):
    """The generic arity encoded by a non-nested metadata FullName."""
    return sum(int(n) for n in re.findall(r"`(\d+)", full_name))

# #370: the CIR node kinds that EXIST only because a member of another assembly was resolved. bir2cir mints
# them nowhere else (its own ResolvedOnlyKinds), and ilemit already refuses one that arrives without a
# resolved owner — so for these, a reference is not merely expected, it is what the node is made of. Requiring
# it unconditionally puts the failure at the layer that dropped the identity instead of several stages later,
# and keeps the rule independent of any incidental fields on the operation node.
#
# The set GROWS one authoring step at a time; a kind absent from it is simply not migrated yet.
MEMBER_REF_REQUIRED_KINDS = {
    "newClr", "clrStatic", "clrInstance", "clrGenericStatic", "clrGenericInstance",
    "newBoundClrDelegate", "newClrStaticDelegate",
    "clrPropGet", "clrPropSet", "clrEventAdd", "clrEventRemove",
}

# Kinds that are external only SOMETIMES — a field access or a construction whose owner may equally be a type
# this compilation is emitting, which has no assembly identity yet and correctly carries no reference.
MEMBER_REF_CONDITIONAL_KEYS = {
    "new": "memberRef",
    "field": "memberRef",
    "setField": "memberRef",
    "setFieldExpr": "memberRef",
    "staticField": "fieldRef",
    "staticFieldSet": "fieldRef",
}

# Keys that legitimately hold a bare STRING scalar: format vocabulary (k/t tags, enums),
# object-language NAME payloads, and the documented owner/member/attribute reference
# strings (spec §2.2.1 — a type IDENTITY used as a resolution key, not a document value-type
# slot). A bare string at any OTHER key = a type node that regressed to a string.
STR_OK = {
    "k", "t",                                   # node-kind / type-tag (validated vs frozen sets)
    "name",                                     # decl/local/var/field names AND fqn.name (the type identity string)
    "scope",                                    # tv.scope enum
    "op", "cmp", "clrFlagsOperation",           # operator / compiler-carried CLR [Flags] semantic role
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
    "field", "ordinal", "values", "valueOf",      # rich-enum round-trip carrier's exact physical member names;
                                                    # declaration identities, not value-type slots
    "accessor",                                  # W1-S3 (#46/#121): the ref.dll-resolved get_/set_/add_/remove_ accessor
                                                # METHOD NAME ilemit links (clrPropGet/Set, clrEvent*, external field) — a
                                                # bir2cir resolution decision, NOT a type slot (paired with `member`+`dispatch`)
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
    "assembly", "callingConvention",            # #370 memberRef: the PHYSICAL defining-assembly simple name the
                                                # emitted reference must be scoped to, and the HASTHIS bit. A metadata
                                                # scope and an enum — validated structurally in member_ref.
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
    "propertyName", "propertyAccessor", "propertyAssociation", # #397: BIR-only Kotlin property identity,
                                                 # explicit get/set role, and file-local Property/accessor association.
                                                 # bir2cir consumes these after every semantic property pass has run.
    "declarationId", "declarationSourceName",    # #395: frontend declaration fingerprint + original Kotlin callable name;
                                                 # consumed by physical member allocation / round-trip metadata.
    "explicitClrName",                           # #402: BIR-only source-authored MethodDef name from @ClrName/@JvmName;
                                                 # bir2cir consumes it during physical allocation.
    "companionGetterExplicitClrName", "companionSetterExplicitClrName",
    "companionGetterDeclarationId", "companionSetterDeclarationId", # #402: BIR-only default companion-accessor
                                                 # naming facts transferred from field storage to synthesized MethodDefs.
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
    # emitted as bare strings), `accessOwner`, and clr* `type`. bir2cir reads them via TypeJson.Read/OwnerName
    # and ilemit via SlotName/ParseOwnerSlot/ClrRef — both node-native. No owner-FQN string slot remains.
}
# On these CLR-lowered kinds the `type` field is the call's OWNER (not a value type) — the owner-FQN island
# (§2.2.1). Every OTHER kind's `type` is a value type and stays enforced. Their argTypes/ret/typeArgs remain
# enforced value/type-arg slots.
CLR_OWNER_KINDS = {
    "clrStatic", "clrInstance", "clrGenericStatic", "clrGenericInstance",
    "clrPropGet", "clrPropSet", "clrStaticField", "clrEventGet", "clrEventAdd", "clrEventRemove", "constrainedCall",
    "clrEventRaise",   # §4.3: the raise handle read — its `type` is the receiver's owner FQN (BIR-only; bir2cir -> callInstance)
}
# Keys that legitimately hold an ARRAY containing bare strings: only the type-PARAMETER
# name-declaration shorthand (typeParams may be ["T"] instead of [{name:"T"}]). A type-param
# DECLARATION names a variable; references to it use positional tv{scope,i} nodes (§1), so this
# is a decl-name list, NOT a type-usage slot. bir2cir's internal resolved-member parameter vector is structured
# TypeNode data and never crosses either serialized phase boundary; the retired lossy `shapes` island is gone.
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
    "kotlinAccessors",                          # #397: BIR-only Property declaration roles (get/set), not Type usages.
}

MOD_KEYS = {
    "inline", "infix", "operator", "tailrec", "external", "ext", "override", "abstract",
    "open", "suspend", "data", "sealed", "inner", "enum", "fun", "annotation", "value",
    "object", "const", "lateinit", "vararg", "noinline", "crossinline",
    "inlineOnly",                                # #98: @InlineOnly → [MethodImpl(AggressiveInlining)] (ilemit reads mods.inlineOnly)
    "context",                                   # a Kotlin CONTEXT parameter (a param-only mod; bir2cir turns it into
                                                 # the [KotlinContextParameter] marker projected into reference KLIBs)
    "extensionReceiver",                         # a Kotlin extension-receiver parameter; bir2cir turns it into the
                                                 # trusted [KotlinExtensionReceiver] slot-role marker
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
    "enumValue", "enumBits", "enumName", "enumValues", "enumParse", "enumOrdinal", "default", "defaultArg", "classRef", "console",
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

    def rich_enum_decl(self, f, path, o, is_type_decl):
        """Validate kotc's exact current rich-enum type-declaration facts."""
        has_marker = "enumRich" in o
        has_carrier = "richEnum" in o
        if not has_marker and not has_carrier:
            return

        if not is_type_decl:
            self.err(f, path, "enumRich/richEnum may appear only on a root types[] declaration")

        if f.endswith(".cir.json"):
            if has_marker:
                self.err(f, path + "/enumRich", "enumRich is a BIR declaration fact and must be consumed before CIR")
            if has_carrier:
                self.err(f, path + "/richEnum", "richEnum is a BIR declaration fact and must be consumed before CIR")

        if has_marker and o.get("enumRich") is not True:
            self.err(f, path + "/enumRich", "enumRich must be true when present")
        if has_marker != has_carrier:
            self.err(f, path, "enumRich=true and richEnum must be present together")
        if o.get("kind") != "class":
            self.err(f, path, "rich-enum declaration facts may appear only on a class type declaration")
        if not has_carrier:
            return

        carrier = o.get("richEnum")
        where = path + "/richEnum"
        if not isinstance(carrier, dict):
            self.err(f, where, "richEnum must be an object")
            return
        required = {"entries", "name", "ordinal", "values", "valueOf"}
        if set(carrier) != required:
            self.err(f, where, "richEnum must contain exactly entries/name/ordinal/values/valueOf")
        for key in ("name", "ordinal", "values", "valueOf"):
            if not isinstance(carrier.get(key), str) or not carrier[key]:
                self.err(f, where + "/" + key, f"richEnum.{key} must be a non-empty physical member name")
        entries = carrier.get("entries")
        if not isinstance(entries, list):
            self.err(f, where + "/entries", "richEnum.entries must be an array")
            return
        for i, entry in enumerate(entries):
            entry_where = where + f"/entries[{i}]"
            if not isinstance(entry, dict) or set(entry) != {"name", "field"}:
                self.err(f, entry_where, "richEnum entry must contain exactly name/field")
                continue
            for key in ("name", "field"):
                if not isinstance(entry.get(key), str) or not entry[key]:
                    self.err(f, entry_where + "/" + key, f"richEnum entry {key} must be a non-empty string")

    def basic_enum_decl(self, f, path, o, is_type_decl):
        """Validate #526's source and physical explicit-basic-enum declaration facts."""
        has_source = "clrEnum" in o
        has_carrier = "basicEnum" in o
        has_underlying = is_type_decl and o.get("kind") == "enum" and "underlying" in o
        if not has_source and not has_carrier and not has_underlying:
            return
        if not is_type_decl or o.get("kind") != "enum":
            self.err(f, path, "clrEnum/basicEnum/enum underlying may appear only on a root enum declaration")
        if f.endswith(".bir.json"):
            if has_underlying or has_carrier:
                self.err(f, path, "physical explicit-enum facts must not appear in BIR")
            fact = o.get("clrEnum")
            if not isinstance(fact, dict) or set(fact) != {"underlying"}:
                self.err(f, path + "/clrEnum", "clrEnum must contain exactly the Kotlin underlying type")
            entries = o.get("entries")
            if not isinstance(entries, list):
                self.err(f, path + "/entries", "explicit BIR enum entries must be an ordered array")
            else:
                for ordinal, entry in enumerate(entries):
                    where = path + f"/entries[{ordinal}]"
                    if not isinstance(entry, dict) or set(entry) != {"name", "ordinal", "value"}:
                        self.err(f, where, "explicit BIR enum entry must contain exactly name/ordinal/value")
                    elif (not isinstance(entry.get("name"), str) or not entry["name"] or
                          entry.get("ordinal") != ordinal or
                          not isinstance(entry.get("value"), str) or not entry["value"]):
                        self.err(f, where, "explicit BIR enum entry must carry its name, declaration ordinal, and constant text")
        if f.endswith(".cir.json"):
            if has_source:
                self.err(f, path + "/clrEnum", "clrEnum is a BIR declaration fact and must be consumed before CIR")
            if has_carrier:
                self.err(f, path + "/basicEnum", "basicEnum must be consumed into trusted metadata before CIR")
            if not has_underlying:
                self.err(f, path + "/underlying", "an explicit CIR enum requires its exact CLR underlying type")
            entries = o.get("entries")
            if not isinstance(entries, list):
                self.err(f, path + "/entries", "explicit CIR enum entries must be an ordered array")
            else:
                for ordinal, entry in enumerate(entries):
                    where = path + f"/entries[{ordinal}]"
                    if not isinstance(entry, dict) or not set(entry).issubset(
                            {"name", "ordinal", "underlying", "physicalValue", "attrs"}) or not {
                                "name", "ordinal", "underlying", "physicalValue"
                            }.issubset(entry):
                        self.err(f, where, "explicit CIR enum entry must contain name/ordinal/underlying/physicalValue and optional attrs")
                    elif (not isinstance(entry.get("name"), str) or not entry["name"] or
                          entry.get("ordinal") != ordinal or
                          not isinstance(entry.get("underlying"), str) or not entry["underlying"] or
                          not isinstance(entry.get("physicalValue"), str) or not entry["physicalValue"] or
                          ("attrs" in entry and not isinstance(entry["attrs"], list))):
                        self.err(f, where, "explicit CIR enum entry must carry its name, declaration ordinal, underlying type, and physical value")

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
            "ptr": ["of"], "mod": ["req", "m", "of"],
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
        if t == "array" and "rank" in o:
            # A stated rank names the GENERAL array; the vector omits it. Rank 1 is meaningful here — it is
            # `T[*]`, which ECMA keeps distinct from `T[]` — so the check is that a rank exists at all and is
            # in range, not that it is above one.
            if not isinstance(o["rank"], int) or isinstance(o["rank"], bool) or not 1 <= o["rank"] <= 32:
                self.err(f, path, f"array.rank must be an integer between 1 and 32, got {o['rank']!r}")
        if t == "mod" and not isinstance(o.get("req"), bool):
            self.err(f, path, f"mod.req must be bool (true=modreq, false=modopt), got {o.get('req')!r}")
        if t == "star" and f.endswith(".cir.json"):
            self.err(f, path, "Kotlin star projection must be lowered by bir2cir before CIR")
        # The three ECMA signature carriers are bir2cir-authored CIR facts: Kotlin source cannot spell a
        # pointer, a multi-dimensional array or a custom modifier, so one in kotc's BIR is a producer defect.
        if f.endswith(".bir.json"):
            if t in ("ptr", "mod"):
                self.err(f, path, f"type {t!r} is a CIR-only ECMA signature carrier and must not appear in kotc BIR")
            if t == "array" and "rank" in o:
                self.err(f, path, "array.rank is a CIR-only ECMA signature carrier and must not appear in kotc BIR")
        # …and they belong to a MEMBER REFERENCE, nowhere else. Ordinary type slots are rewritten by many
        # lowering passes that reconstruct an array as a vector and know nothing of pointers or modifiers, so
        # one of these outside a reference would be silently flattened — re-creating the very collisions the
        # carriers exist to prevent. Confining them to the reference keeps those passes correct by construction.
        if (t in ("ptr", "mod") or (t == "array" and "rank" in o)) and not in_member_ref(path):
            carrier = "array.rank" if t == "array" else t
            self.err(f, path, f"type {carrier} may only appear inside a memberRef signature, not in an ordinary type slot")
        # The other direction: a member reference is a PHYSICAL identity, so the Kotlin type-system facts have
        # no place in one. `oblivious` is a nullability annotation the CLR signature does not carry, and `star`
        # is a Kotlin projection; either inside a signature would be a second spelling of a physical shape, and
        # two spellings of one member are two members to a consumer that compares them exactly.
        if in_member_ref(path) and t in ("oblivious", "star"):
            self.err(f, path, f"type {t!r} is a Kotlin type-system fact and has no place in a physical member signature")

    def member_ref_carrier(self, f, path, key, val):
        """A frozen memberRef carrier key holds ONE reference — never a list of them.

        A candidate SET reaching a consumer is the failure mode this whole shape exists to remove: whoever
        received it would have to choose, and choosing is the decision that belongs to the producer. Each
        element of such a list validates perfectly well on its own, so nothing else here would notice.
        """
        if not isinstance(val, dict):
            self.err(f, path, f"{key} must be ONE member reference object, got {type(val).__name__} "
                              "(a candidate set is member selection deferred downstream)")
            return
        # NOTE for a later step: a carrier that legitimately holds SEVERAL references (a MethodImpl list, say)
        # is a different shape and needs its own arm here — each element one reference, the list itself not a
        # candidate set. Until such a carrier is registered, a list under any carrier is the smuggling above.
        self.member_ref(f, path, val)
        allowed = MEMBER_REF_KIND_BY_CARRIER.get(key)
        if allowed is not None and val.get("kind") not in allowed:
            self.err(f, path, f"{key} must hold a reference of kind {sorted(allowed)}, got {val.get('kind')!r}")
        if f.endswith(".bir.json"):
            self.err(f, path, "memberRef is a bir2cir-authored resolved member identity and must not appear in kotc BIR")

    def member_ref(self, f, path, o):
        """#370: a memberRef must be a COMPLETE member identity — the point of the shape (spec §2.2.2).

        Every field here exists because some consumer would otherwise have to reconstruct it, and
        reconstruction is member SELECTION. So an incomplete reference is refused at the wire rather than
        left to fail as a lookup that found nothing (or, worse, found the wrong member).
        """
        kind = o.get("kind")
        if kind not in MEMBER_REF_KINDS:
            self.err(f, path, f"memberRef.kind={kind!r} is not in {sorted(MEMBER_REF_KINDS)}")
        for required in ("kind", "assembly", "declaringType", "name", "genericArity", "returnType"):
            if required not in o:
                self.err(f, path, f"memberRef missing required field {required!r}")
        if kind != "ctor" and o.get("name") == ".ctor":
            self.err(f, path, f"memberRef.name `.ctor` names a constructor, but kind is {kind!r}")
        allowed = {"kind", "assembly", "declaringType", "name", "genericArity", "returnType",
                   "callingConvention", "parameterTypes"}
        for extra in set(o) - allowed:
            self.err(f, path, f"memberRef carries unknown field {extra!r}")
        if not isinstance(o.get("assembly"), str) or not o["assembly"]:
            self.err(f, path, "memberRef.assembly must be a non-empty simple assembly name")
        declaring = o.get("declaringType")
        if not isinstance(declaring, dict) or declaring.get("t") != "fqn":
            self.err(f, path, "memberRef.declaringType must be an fqn Type node")
        elif "args" in declaring and not declaring["args"]:
            # One spelling per shape: a non-generic declarer OMITS args. An empty list would be a second way
            # to say the same thing, and two spellings of one identity are two identities to a consumer that
            # compares them.
            self.err(f, path, "memberRef.declaringType must omit `args` when the declarer is non-generic, not carry an empty list")
        elif isinstance(declaring.get("name"), str):
            # A non-nested name states its arity. A nested name cannot state whether the inner declaration captures
            # enclosing parameters; bir2cir validates it against the resolved TypeDef while authoring the reference.
            want, got = arity_of_name(declaring["name"]), len(declaring.get("args") or [])
            if "+" not in declaring["name"] and want != got:
                self.err(f, path, f"memberRef.declaringType `{declaring['name']}` declares {want} generic parameter(s) but carries {got} argument(s)")
        if not isinstance(o.get("name"), str) or not o["name"]:
            self.err(f, path, "memberRef.name must be a non-empty metadata member name")
        arity = o.get("genericArity")
        if not isinstance(arity, int) or isinstance(arity, bool) or arity < 0:
            self.err(f, path, f"memberRef.genericArity must be a non-negative integer, got {arity!r}")
        elif arity > 0 and kind != "method":
            self.err(f, path, f"memberRef.genericArity must be 0 for kind {kind!r} (only a method has its own generic parameters)")
        if not isinstance(o.get("returnType"), dict) or "t" not in o["returnType"]:
            self.err(f, path, "memberRef.returnType must be a Type node")
        if kind == "field":
            for absent in ("callingConvention", "parameterTypes"):
                if absent in o:
                    self.err(f, path, f"memberRef.{absent} must be absent for a field")
        elif kind in MEMBER_REF_KINDS:
            if o.get("callingConvention") not in MEMBER_REF_CONVENTIONS:
                self.err(f, path, f"memberRef.callingConvention={o.get('callingConvention')!r} must be one of {sorted(MEMBER_REF_CONVENTIONS)}")
            if not isinstance(o.get("parameterTypes"), list):
                self.err(f, path, f"memberRef.parameterTypes must be a Type-node array for kind {kind!r}")
        if kind == "ctor":
            if o.get("name") != ".ctor":
                self.err(f, path, f"memberRef.name for a ctor must be `.ctor`, got {o.get('name')!r}")
            if o.get("callingConvention") not in ("instance", "varargInstance"):
                self.err(f, path, "a ctor memberRef must be an instance convention")
            if o.get("returnType") != {"t": "fqn", "name": "void"}:
                self.err(f, path, "a ctor memberRef must return void")

    def check_whole_assembly_member_refs(self, files):
        """Check member-reference rules that require the complete emitted-type set.

        A member reference must never name a type THIS compilation emits (#15 local-over-ref, #370).

        A reference carries an assembly, so naming a locally-emitted type points the call at some other
        assembly's copy of it — the precedence bug #15 exists to prevent, and the shape a regression fixture in
        this repo was written to catch. It stayed invisible while the mis-binding produced only a signature;
        once it produces a named member, an emitter that resolves the reference exactly emits the wrong call.

        This is a WHOLE-ASSEMBLY question, so it runs over the file set rather than per document: a type
        declared in one file is local to a call in any other file of the same output directory.
        """
        from collections import defaultdict
        by_dir = defaultdict(list)
        for f in files:
            if f.endswith(".cir.json"):
                by_dir[os.path.dirname(f)].append(f)
        for directory, group in by_dir.items():
            docs = []
            local = set()
            for f in group:
                try:
                    d = json.load(open(f))
                except Exception:
                    continue
                docs.append((f, d))
                # A file class is emitted by this compilation as surely as a declared type; it simply has no
                # row in `types`, which is exactly how it went missing from the producer's own local set.
                if isinstance(d.get("fileClass"), str):
                    local.add(d["fileClass"])
                self.collect_declared(d.get("types"), local)
            for f, d in docs:
                self.walk_local_refs(f, d, "", local)
                self.walk_external_base_ctor_refs(f, d, "", local)
                self.walk_external_member_nodes(f, d, "", local)
                self.walk_external_base_method_impls(f, d, "", local)

    def walk_external_member_nodes(self, f, o, path, local):
        """Require identity from an owner fact that cannot disappear with memberRef."""
        if isinstance(o, dict):
            kind = o.get("k")
            owner = o.get("type") if kind == "new" else o.get("ownerType")
            reference_key = MEMBER_REF_CONDITIONAL_KEYS.get(kind)
            if (reference_key is not None and isinstance(owner, dict)
                    and owner.get("t") == "fqn" and isinstance(owner.get("name"), str)
                    and owner["name"] not in local and not isinstance(o.get(reference_key), dict)):
                self.err(
                    f, path,
                    f"{kind} targeting external owner {owner['name']!r} must carry a resolved {reference_key}"
                )
            for key, val in o.items():
                self.walk_external_member_nodes(f, val, path + "/" + key, local)
        elif isinstance(o, list):
            for i, x in enumerate(o):
                self.walk_external_member_nodes(f, x, path + f"[{i}]", local)

    def walk_external_base_method_impls(self, f, o, path, local):
        if isinstance(o, dict):
            descriptors = o.get("clrBaseImpls")
            if isinstance(descriptors, list):
                for i, descriptor in enumerate(descriptors):
                    owner = descriptor.get("owner") if isinstance(descriptor, dict) else None
                    if (isinstance(owner, dict) and owner.get("t") == "fqn"
                            and isinstance(owner.get("name"), str) and owner["name"] not in local
                            and not isinstance(descriptor.get("memberRef"), dict)):
                        self.err(
                            f, path + f"/clrBaseImpls[{i}]",
                            f"external base MethodImpl {owner['name']!r} must carry its resolved memberRef"
                        )
            for key, val in o.items():
                self.walk_external_base_method_impls(f, val, path + "/" + key, local)
        elif isinstance(o, list):
            for i, x in enumerate(o):
                self.walk_external_base_method_impls(f, x, path + f"[{i}]", local)

    def walk_external_base_ctor_refs(self, f, o, path, local):
        """Every direct constructor delegation to a non-local base carries its selected constructor.

        The type's lowered base plus the whole compilation's declared-type set is the durable discriminator;
        it does not duplicate the constructor identity and cannot disappear together with baseCtorRef in the
        pass that authors the reference.
        """
        if isinstance(o, dict):
            base = o.get("base")
            ctors = o.get("ctors")
            if (isinstance(o.get("kind"), str) and isinstance(base, dict)
                    and base.get("t") == "fqn" and isinstance(base.get("name"), str)
                    and base["name"] not in local and isinstance(ctors, list)):
                for i, ctor in enumerate(ctors):
                    if (isinstance(ctor, dict) and not isinstance(ctor.get("thisArgs"), list)
                            and not isinstance(ctor.get("baseCtorRef"), dict)):
                        self.err(
                            f, f"{path}/ctors[{i}]",
                            f"constructor delegating to external base {base['name']!r} must carry baseCtorRef"
                        )
            for key, val in o.items():
                self.walk_external_base_ctor_refs(f, val, path + "/" + key, local)
        elif isinstance(o, list):
            for i, x in enumerate(o):
                self.walk_external_base_ctor_refs(f, x, path + f"[{i}]", local)

    def collect_declared(self, node, into):
        if isinstance(node, dict):
            if isinstance(node.get("name"), str) and "kind" in node:
                into.add(node["name"])
            for v in node.values():
                self.collect_declared(v, into)
        elif isinstance(node, list):
            for x in node:
                self.collect_declared(x, into)

    def walk_local_refs(self, f, o, path, local):
        if isinstance(o, dict):
            for key in MEMBER_REF_KEYS & set(o):
                ref = o[key]
                if isinstance(ref, dict) and isinstance(ref.get("declaringType"), dict):
                    name = ref["declaringType"].get("name")
                    if name in local:
                        self.err(f, path + "/" + key,
                                 f"{key} names `{name}`, which this compilation emits — a reference scoped to "
                                 f"`{ref.get('assembly')}` would call another assembly's copy of it")
            for key, val in o.items():
                self.walk_local_refs(f, val, path + "/" + key, local)
        elif isinstance(o, list):
            for i, x in enumerate(o):
                self.walk_local_refs(f, x, path + f"[{i}]", local)

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
                                      "(a bindRef reads a binding of an ancestor plan)")
                return
            for key, val in o.items():
                self.plan_scope(f, val, path + "/" + key, bound)
        elif isinstance(o, list):
            for i, x in enumerate(o):
                self.plan_scope(f, x, path + f"[{i}]", bound)

    def walk(self, f, o, path, is_type_decl=False):
        if isinstance(o, dict):
            self.rich_enum_decl(f, path, o, is_type_decl)
            self.basic_enum_decl(f, path, o, is_type_decl)
            for internal in BIR2CIR_INTERNAL_MEMBER_FACTS:
                if internal in o:
                    self.err(
                        f, path,
                        f"{internal} is a bir2cir pass-to-pass member-resolution fact and must not be serialized"
                    )
            for internal in BIR2CIR_INTERNAL_TYPE_FACTS:
                if internal in o:
                    self.err(
                        f, path,
                        f"{internal} is a bir2cir pass-to-pass generic-shape fact and must not be serialized"
                    )
            if o.get("kind") == "interface" and isinstance(o.get("methods"), list):
                for i, method in enumerate(o["methods"]):
                    if (not isinstance(method, dict)
                            or (method.get("static") is not True and not isinstance(method.get("abstract"), bool))):
                        self.err(
                            f, f"{path}/methods[{i}]",
                            "interface method must carry the explicit frontend/bir2cir abstract modality fact"
                        )
            if "t" in o and "k" in o:
                # disjoint structural roles (Codex-confirmed blind spot): a type node has `t`, an IR node has `k`;
                # an object carrying BOTH is ill-formed and must not slip past as either.
                self.err(f, path, f"object carries BOTH k={o.get('k')!r} and t={o.get('t')!r} (node/type roles are disjoint)")
            elif "t" in o:
                self.type_node(f, path, o)
            # #370, two independent triggers, because either one alone leaves a hole. The KEY is authoritative:
            # whatever sits under a frozen carrier is a member reference and is checked as one, so a reference
            # that dropped a required field cannot escape validation by no longer looking like one. And
            # `declaringType` — which no other document shape has — catches a resolved identity smuggled in
            # under a key nobody registered, i.e. a second member-identity vocabulary growing beside this one.
            if INTERFACE_SLOTS in o:
                slots = o[INTERFACE_SLOTS]
                if not f.endswith(".cir.json"):
                    self.err(f, path, f"{INTERFACE_SLOTS} is a CIR fact: nothing resolves a member before bir2cir runs")
                elif not isinstance(slots, list):
                    self.err(f, path, f"{INTERFACE_SLOTS} must be an array of owner-scoped slot sets")
                else:
                    owner_keys = set()
                    for i, slot_set in enumerate(slots):
                        where = f"{path}/{INTERFACE_SLOTS}[{i}]"
                        if not isinstance(slot_set, dict):
                            self.err(f, where, "an interface slot set must be an object")
                            continue
                        unknown = set(slot_set) - {"owner", "assembly", "slots"}
                        if unknown:
                            self.err(f, where, f"interface slot set has unknown keys: {sorted(unknown)}")
                        owner = slot_set.get("owner")
                        assembly = slot_set.get("assembly")
                        refs = slot_set.get("slots")
                        if not isinstance(owner, dict) or owner.get("t") != "fqn":
                            self.err(f, where + "/owner", "interface slot set owner must be an fqn type")
                        else:
                            self.type_node(f, where + "/owner", owner)
                            owner_key = json.dumps(owner, sort_keys=True, separators=(",", ":"))
                            if owner_key in owner_keys:
                                self.err(f, where + "/owner", "interface slot set owner is duplicated")
                            owner_keys.add(owner_key)
                        if not isinstance(assembly, str) or not assembly:
                            self.err(f, where + "/assembly", "interface slot set assembly must be a non-empty string")
                        if not isinstance(refs, list):
                            self.err(f, where + "/slots", "interface slot set slots must be an array")
                            continue
                        for j, ref in enumerate(refs):
                            ref_where = f"{where}/slots[{j}]"
                            if not isinstance(ref, dict):
                                self.err(f, ref_where, "an interface slot must be a resolved memberRef")
                            elif ref.get("kind") != "method":
                                self.err(f, ref_where, "an interface slot must name a method")
                            else:
                                if isinstance(assembly, str) and ref.get("assembly") != assembly:
                                    self.err(f, ref_where, "interface slot assembly must match its owner-scoped set")
                                declaring = ref.get("declaringType")
                                if isinstance(owner, dict) and isinstance(declaring, dict):
                                    owner_name = re.sub(r"`\d+", "", owner.get("name", ""))
                                    declaring_name = re.sub(r"`\d+", "", declaring.get("name", ""))
                                    if (owner_name != declaring_name
                                            or owner.get("args") != declaring.get("args")):
                                        self.err(f, ref_where,
                                                 "interface slot declaringType must match its constructed owner")
                                self.member_ref(f, ref_where, ref)
            if WELL_KNOWN_TABLE in o:
                table = o[WELL_KNOWN_TABLE]
                if not f.endswith(".cir.json"):
                    self.err(f, path, f"{WELL_KNOWN_TABLE} is a CIR fact: nothing resolves a member before bir2cir runs")
                elif not isinstance(table, dict):
                    self.err(f, path, f"{WELL_KNOWN_TABLE} must be an object mapping a frozen role to its resolved member")
                else:
                    for role, ref in table.items():
                        where = path + "/" + WELL_KNOWN_TABLE + "/" + role
                        if role not in WELL_KNOWN_ROLES:
                            self.err(f, where, f"{role!r} is not a frozen fixed-member role; a role nothing asks for "
                                               "resolves to nothing at emit time")
                        elif not isinstance(ref, dict):
                            self.err(f, where, f"the {role!r} entry must be a resolved memberRef")
                        else:
                            self.member_ref(f, where, ref)
            for carrier_key in MEMBER_REF_KEYS & set(o):
                self.member_ref_carrier(f, path + "/" + carrier_key, carrier_key, o[carrier_key])
            if "declaringType" in o:
                carrier = path.rsplit("/", 1)[-1].split("[")[0]
                # The fixed-member table is keyed by ROLE, not by carrier: its whole point is that one container
                # says every member an expansion needs, so its keys are what the emitter asks for rather than
                # names the contract froze. Its VALUES are still full references and are checked as such.
                in_table = ("/" + WELL_KNOWN_TABLE + "/" in path + "/"
                            or ("/" + INTERFACE_SLOTS + "[" in path and "/slots[" in path))
                if in_table:
                    self.member_ref(f, path, o)
                elif carrier not in MEMBER_REF_KEYS:
                    self.member_ref(f, path, o)
                    self.err(f, path, f"a resolved member identity must ride on a frozen memberRef carrier key, not {carrier!r}")
                    if f.endswith(".bir.json"):
                        self.err(f, path, "memberRef is a bir2cir-authored resolved member identity and must not appear in kotc BIR")
            if "clrFlagsOperation" in o:
                role = o.get("clrFlagsOperation")
                if f.endswith(".cir.json"):
                    self.err(f, path + "/clrFlagsOperation",
                             "clrFlagsOperation is a BIR semantic carrier and must be consumed before CIR")
                if o.get("k") != "callInstance" or role not in {"or", "and", "xor", "inv", "contains"}:
                    self.err(f, path + "/clrFlagsOperation",
                             "clrFlagsOperation must carry a known role on a callInstance")
                required = ("ownerType", "recv", "ret", "args", "argTypes")
                for key in required:
                    if key not in o:
                        self.err(f, path, f"clrFlagsOperation call is missing required field {key!r}")
                args = o.get("args")
                arg_types = o.get("argTypes")
                expected = 0 if role == "inv" else 1
                if not isinstance(args, list) or len(args) != expected:
                    self.err(f, path + "/args",
                             f"clrFlagsOperation {role!r} requires exactly {expected} argument nodes")
                elif any(argument is None for argument in args):
                    self.err(f, path + "/args", "clrFlagsOperation arguments must be expression nodes, not null")
                if not isinstance(arg_types, list) or len(arg_types) != expected:
                    self.err(f, path + "/argTypes",
                             f"clrFlagsOperation {role!r} requires exactly {expected} argument Type nodes")
                elif any(not isinstance(t, dict) or not isinstance(t.get("t"), str) for t in arg_types):
                    self.err(f, path + "/argTypes", "clrFlagsOperation argTypes must be structured Type nodes")
                for key in ("ownerType", "ret"):
                    value = o.get(key)
                    if not isinstance(value, dict) or not isinstance(value.get("t"), str):
                        self.err(f, path + "/" + key,
                                 f"clrFlagsOperation {key} must be a structured Type node")
            if isinstance(o.get("k"), str):
                k = o["k"]
                self.kinds_seen.add(k)
                if k not in KINDS:
                    self.err(f, path, f"unknown node kind k={k!r}")
                for required in REQUIRED_NODE_FIELDS.get(k, ()):
                    if required not in o:
                        self.err(f, path, f"{k} is missing required field {required!r}")
                if k == "conv" and "to" in o:
                    target = o["to"]
                    if not isinstance(target, dict) or not isinstance(target.get("t"), str):
                        self.err(f, path + "/to", "conv.to must be a structured Type node")
                if k == "enumBits":
                    if not f.endswith(".cir.json"):
                        self.err(f, path, "enumBits is a CIR-only physical representation node")
                    for key in ("type", "underlying"):
                        value = o.get(key)
                        if not isinstance(value, dict) or not isinstance(value.get("t"), str):
                            self.err(f, path + "/" + key,
                                     f"enumBits.{key} must be a structured Type node")
                if k == "constrainedCall" and "args" in o and not isinstance(o["args"], list):
                    self.err(f, path + "/args", "constrainedCall.args must be an array")
                if k == "new":
                    # kotc BIR and same-unit CIR constructions retain the aligned use-site vector. External CIR
                    # constructions consume it into memberRef.parameterTypes, so requiring argTypes there would
                    # duplicate the resolved declaration identity ilemit is required to trust.
                    needs_arg_types = f.endswith(".bir.json") or "memberRef" not in o
                    arg_types = o.get("argTypes")
                    args = o.get("args")
                    if needs_arg_types and not isinstance(arg_types, list):
                        self.err(f, path + "/argTypes", "new.argTypes must be an array of structured Type nodes")
                    elif isinstance(arg_types, list) and isinstance(args, list) and len(arg_types) != len(args):
                        self.err(f, path, "new.argTypes must contain one entry per new.args value")
                    if isinstance(arg_types, list):
                        for i, arg_type in enumerate(arg_types):
                            if not isinstance(arg_type, dict) or not isinstance(arg_type.get("t"), str):
                                self.err(f, path + f"/argTypes[{i}]", "new.argTypes entries must be structured Type nodes")
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
                                      "companionStorageReadOnly", "companionGetterExplicitClrName",
                                      "companionSetterExplicitClrName", "companionGetterDeclarationId",
                                      "companionSetterDeclarationId"):
                    if companion_key in o:
                        self.err(f, path, f"{companion_key} is a BIR companion fact and must be consumed before CIR")
                for property_key in ("propertyName", "propertyAccessor", "propertyAssociation", "kotlinAccessors",
                                     "kotlinPropertyAccessorCarrier", "physicalSlotBridge",
                                     "inheritedImplementation", "inheritedDefaultAccessors",
                                     "inheritedDefaultMethods"):
                    if property_key in o:
                        self.err(f, path, f"{property_key} is a BIR property-accessor fact and must be consumed before CIR")
                for declaration_key in ("declarationId", "declarationSourceName", "explicitClrName"):
                    if declaration_key in o:
                        self.err(f, path, f"{declaration_key} is a BIR declaration-identity fact and must be consumed before CIR")
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
                for property_descriptor in ("getSig", "setSig", "getMethodArity", "setMethodArity"):
                    if property_descriptor in o:
                        self.err(
                            f, path,
                            f"{property_descriptor} is a bir2cir-authored physical Property descriptor and must not appear in BIR"
                        )
                if "inheritedImplementation" in o:
                    implementation = o["inheritedImplementation"]
                    required = {"owner", "member", "kind", "arity", "typeParams"}
                    if not isinstance(implementation, dict) or set(implementation) != required:
                        self.err(
                            f, path + "/inheritedImplementation",
                            "inheritedImplementation must contain exact owner/member/kind/arity/typeParams facts"
                        )
                    else:
                        if (not isinstance(implementation.get("owner"), dict)
                                or implementation["owner"].get("t") != "fqn"):
                            self.err(f, path + "/inheritedImplementation/owner", "inheritedImplementation owner must be an fqn Type node")
                        if not isinstance(implementation.get("member"), str) or not implementation["member"]:
                            self.err(f, path + "/inheritedImplementation/member", "inheritedImplementation member must be a non-empty string")
                        if implementation.get("kind") not in ("method", "getter", "setter"):
                            self.err(f, path + "/inheritedImplementation/kind", "inheritedImplementation kind must be method/getter/setter")
                        if not isinstance(implementation.get("arity"), int) or implementation["arity"] < 0:
                            self.err(f, path + "/inheritedImplementation/arity", "inheritedImplementation arity must be non-negative")
                        if (not isinstance(implementation.get("typeParams"), list)
                                or len(implementation["typeParams"]) != implementation.get("arity")):
                            self.err(
                                f, path + "/inheritedImplementation/typeParams",
                                "inheritedImplementation typeParams must match its generic arity"
                            )
                if o.get("k") == "newClrStaticDelegate":
                    self.err(f, path, "newClrStaticDelegate is a bir2cir-authored physical node and must not appear in BIR")
                if "capturedTypeParams" in o:
                    self.err(f, path, "capturedTypeParams is a bir2cir-authored nested CLR declaration fact and must not appear in BIR")
                if "nestedIn" in o:
                    self.err(f, path, "nestedIn is a bir2cir-authored physical CLR ownership fact and must not appear in BIR")
                for method_impl_key in ("clrInterfaceImpls", "clrBaseImpls"):
                    if method_impl_key in o:
                        self.err(f, path, f"{method_impl_key} is a bir2cir-authored MethodImpl fact and must not appear in BIR")
            for method_impl_key in ("clrInterfaceImpls", "clrBaseImpls"):
                if method_impl_key not in o:
                    continue
                descriptors = o[method_impl_key]
                if not isinstance(descriptors, list):
                    self.err(f, path, f"{method_impl_key} must be an array of exact MethodImpl descriptors")
                    continue
                for index, descriptor in enumerate(descriptors):
                    descriptor_path = path + f"/{method_impl_key}[{index}]"
                    if not isinstance(descriptor, dict):
                        self.err(f, descriptor_path, "MethodImpl descriptor must be an object")
                        continue
                    required = {"owner", "member", "arity", "params", "ret"}
                    allowed = required | {"typeParams", "memberRef"}
                    if not required.issubset(descriptor) or not set(descriptor).issubset(allowed):
                        self.err(f, descriptor_path, "MethodImpl descriptor must contain owner/member/arity/params/ret and optional typeParams/memberRef")
                    if not isinstance(descriptor.get("owner"), dict) or descriptor["owner"].get("t") != "fqn":
                        self.err(f, descriptor_path, "MethodImpl descriptor owner must be an fqn Type node")
                    if not isinstance(descriptor.get("member"), str) or not descriptor["member"]:
                        self.err(f, descriptor_path, "MethodImpl descriptor member must be a non-empty string")
                    if not isinstance(descriptor.get("arity"), int) or descriptor["arity"] < 0:
                        self.err(f, descriptor_path, "MethodImpl descriptor arity must be a non-negative integer")
                    if not isinstance(descriptor.get("params"), list):
                        self.err(f, descriptor_path, "MethodImpl descriptor params must be a Type-node array")
                    if not isinstance(descriptor.get("ret"), dict) or "t" not in descriptor["ret"]:
                        self.err(f, descriptor_path, "MethodImpl descriptor ret must be a Type node")
                    if "typeParams" in descriptor:
                        type_params = descriptor["typeParams"]
                        if not isinstance(type_params, list) or len(type_params) != descriptor.get("arity"):
                            self.err(f, descriptor_path, "MethodImpl descriptor typeParams must match its generic arity")
                    if ("memberRef" in descriptor and (not isinstance(descriptor.get("memberRef"), dict)
                            or descriptor["memberRef"].get("kind") != "method")):
                        self.err(f, descriptor_path, "MethodImpl descriptor memberRef must name a method")
            if o.get("k") == "newClrStaticDelegate" and f.endswith(".cir.json"):
                if not isinstance(o.get("memberRef"), dict):
                    self.err(f, path, "newClrStaticDelegate must carry the resolved memberRef of the method it binds")
            if f.endswith(".cir.json"):
                requires_override = o.get("requiresClrOverride")
                has_override_ref = "clrOverrideRef" in o
                if requires_override is not None and requires_override is not True:
                    self.err(f, path, "requiresClrOverride must be true when present")
                if (requires_override is True) != has_override_ref:
                    self.err(
                        f, path,
                        "requiresClrOverride:true and clrOverrideRef must be present together: the instruction requires its exact MethodImpl operand"
                    )
                # An APPLIED EXTERNAL attribute is a call into the assembly that declares it, and `attrExternal`
                # is a durable bir2cir fact rather than an identity descriptor. It is also the rule that would have
                # caught 496 return-position attributes going unresolved while the walk that resolves them
                # looked complete.
                if o.get("attrExternal") is True:
                    ref = o.get("memberRef")
                    if not isinstance(ref, dict):
                        self.err(f, path, "an external applied attribute must carry the resolved memberRef of the constructor it invokes")
                    else:
                        if ref.get("kind") != "ctor":
                            self.err(f, path, f"an applied attribute's memberRef must be a ctor, got {ref.get('kind')!r}")
                        attr_type = o.get("attr")
                        if (isinstance(attr_type, dict) and isinstance(ref.get("declaringType"), dict)
                                and attr_type.get("name") != ref["declaringType"].get("name")):
                            self.err(f, path, "an applied attribute's memberRef must be declared by the attribute type itself")
                        declared = o.get("argTypes")
                        if isinstance(declared, list) and len(declared) != len(ref.get("parameterTypes") or []):
                            self.err(f, path, "an applied attribute's memberRef takes a different number of arguments than the application states")
            if f.endswith(".cir.json"):
                for required_key in COLLECTION_TEMPLATE_REFS.get(o.get("k"), ()):
                    if required_key not in o:
                        self.err(f, path, f"{o['k']} must carry {required_key}: a collection literal names the members it builds through")
                for required_key in REQUIRED_OPERATION_REFS.get(o.get("k"), ()):
                    if required_key not in o:
                        self.err(f, path, f"{o['k']} must carry {required_key}: the operation emits that external member operand")
                kind = o.get("k")
                if kind in ("clrEventAdd", "clrEventRemove") and "localAccessor" in o:
                    if o.get("localAccessor") is not True:
                        self.err(f, path, f"{kind}.localAccessor must be true when present")
                    else:
                        # A same-emission-unit event accessor has no assembly identity and therefore no memberRef.
                        # Its scalar linkage is the emitted owner plus the exact declaration signature; requiring
                        # these together prevents a consumer from selecting an inherited/name-only MethodDef.
                        if "memberRef" in o:
                            self.err(f, path, f"{kind} cannot carry both localAccessor and an external memberRef")
                        if not isinstance(o.get("accessorOwner"), dict) or o["accessorOwner"].get("t") != "fqn":
                            self.err(f, path, f"{kind} localAccessor must carry an fqn accessorOwner")
                        if not isinstance(o.get("accessor"), str) or not o["accessor"]:
                            self.err(f, path, f"{kind} localAccessor must carry a non-empty accessor name")
                        if not isinstance(o.get("delegateType"), dict) or "t" not in o["delegateType"]:
                            self.err(f, path, f"{kind} localAccessor must carry its delegateType")
                        if not isinstance(o.get("sig"), list) or len(o["sig"]) != 1:
                            self.err(f, path, f"{kind} localAccessor must carry its one-parameter declaration sig")
                        if o.get("dispatch") not in ("call", "callvirt", "constrained"):
                            self.err(f, path, f"{kind} localAccessor must carry call|callvirt|constrained dispatch")
            if f.endswith(".cir.json") and "memberRef" not in o:
                kind = o.get("k")
                if kind in MEMBER_REF_REQUIRED_KINDS:
                    # A node of this kind IS a reference to another assembly's member. One without a resolved
                    # identity has nothing for a consumer to link, which is why ilemit refuses it — and refusing
                    # it here names the layer that dropped it rather than the one that noticed.
                    if not (kind in ("clrEventAdd", "clrEventRemove") and o.get("localAccessor") is True):
                        self.err(f, path, f"{kind} is an external member reference and must carry a resolved memberRef")
                elif MEMBER_REF_CONDITIONAL_KEYS.get(kind) == "memberRef":
                    # Sometimes external, sometimes a member of the assembly being built. The durable `member`
                    # discriminator says WHICH this node is for a property or field, carrying a
                    # kind marker rather than a signature.
                    if o.get("member") in ("accessor", "field"):
                        self.err(f, path, f"{kind} carries member={o['member']!r} without the resolved memberRef beside it")
            elif "requiresClrOverride" in o:
                self.err(f, path, "requiresClrOverride is a bir2cir-authored CIR instruction and must not appear in BIR")
            if isinstance(o.get("mods"), dict):
                for mk in o["mods"]:
                    if mk not in MOD_KEYS:
                        self.err(f, path + "/mods", f"unknown mod key {mk!r}")
                # `suspend` is Kotlin frontend vocabulary with no CLR meaning, and every consumer of it is a
                # bir2cir pass: the cold-core lowering that turns a suspend declaration into a state machine, a
                # cold entry and a Task bridge, and the [KotlinFunction(Suspend)] metadata stamp. One in CIR means
                # a declaration reached the emitter with its Kotlin coroutine body intact.
                if f.endswith(".cir.json") and o["mods"].get("suspend") is True:
                    self.err(f, path + "/mods",
                             "mods.suspend is consumed by bir2cir and must not appear in CIR")
                if f.endswith(".cir.json") and o["mods"].get("extensionReceiver") is True:
                    self.err(f, path + "/mods",
                             "mods.extensionReceiver is consumed by bir2cir and must not appear in CIR")
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
                    children_are_type_decls = path == "" and key == "types"
                    for i, x in enumerate(val):
                        if isinstance(x, str) and key not in STRARR_OK:
                            self.err(f, p + f"[{i}]", f"bare STRING in type-array {key!r}: {x!r} (must be a {{t:...}} node)")
                        else:
                            self.walk(f, x, p + f"[{i}]", children_are_type_decls)
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
    v.check_whole_assembly_member_refs(files)
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
