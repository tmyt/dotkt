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
// risk a false reject. Call arity is checked only where a resolved physical target exists. A current `new` node is
// different: bir2cir must materialize every default before CIR and the node carries one use-site `argTypes` entry per
// physical argument; the schema/lowering gates enforce that current contract.
//
// The check set (all provably ilemit-equivalent — each mirrors a place ilemit already throws / miscompiles):
//   1. LOCAL RESOLUTION — every `local`/`setLocal`/`byref{Load,Store}` names a var/param declared in the same scope.
//   2. CFG TARGETS — every `goto`/`brIf` id has a matching `label` node in the body, and no `label` id is declared
//      twice in one body.
//   3. STRUCTURAL — `binOp` has both `lhs`+`rhs`; `cond` has `cond`+`then`+`else`.
//   4. OWNER PRESENCE — fields carry ownerType; owner:null callStatic/newDelegate/newBoundDelegate carry calleeOwner.
//   5. `for` `cmp` ∈ {<=, <, >=} — an unknown cmp silently miscompiles to an infinite loop.
//   7. STAMP AGREEMENT — a node's `sty` must not name a DIFFERENT TYPE than the `ret`/`dynRet` beside it (spec §2.7:
//      a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`). See StampAgrees for the
//      accepted-equivalence set and why it is what it is.
//   8. COLLECTION VIEW COMPLETENESS — a type stating a MUTABLE collection face also states its READ-ONLY sibling
//      (bir-common/CollectionViewFaces.cs). This one is about a TYPE's interface set rather than a body, so it runs
//      once per declaration instead of per scope. It is CIR-only in effect: the relation is over the LOWERED BCL
//      faces, and a document still speaking Kotlin collection names cannot trip it.
//   9. SUSPEND MODIFIER CONSUMED — no method DECLARATION (abstract or concrete) still carries `mods.suspend`: the
//      Kotlin modifier is bir2cir's to consume, and CIR is a physical CLR graph.
//  10. P/INVOKE COMPLETENESS — a `pinvoke` descriptor belongs only to a static, non-generic, bodyless CIR extern;
//      Kotlin's `mods.external` source fact never survives beside it.
//
// SCOPE units mirror ilemit's `_locals`/`_cfgLabels` lifetimes exactly: a method = params ∪ body; a ctor ALSO folds
// in preStmts/thisArgs/baseArgs (emitted in the same frame); the static-field-initializer group shares ONE .cctor `_locals`
// scope across ALL of a type's static inits. Physical CIR walks only typed executable children. Bir2cir's
// SourceStampSanity owns the BIR checkpoint and shares the StampAgrees relation below.

namespace DotKt.Bir;

// A sanity violation, attributed to its declaration (with a #112 Phase-2 `File.kt:line` prefix when the decl carries
// a `pos`). Consumers (ilemit's CirSanityException, bir2cir's catch) format it into their own layer's diagnostic.
public sealed class IrSanityException : Exception
{
    public string Decl { get; }
    public IrSanityException(string decl, string message) : base(message) { Decl = decl; }
}

public static partial class IrSanity
{

    // Check 8 — a type's stated `interfaces` must be closed under the read-only collection view. The read-only face
    // of a mutable collection is not derived by the CLR (`IList<T>` does not inherit `IReadOnlyList<T>`), so a
    // Kotlin value flowing from a mutable type into a read-only slot reaches a castclass that succeeds only if the
    // type declares that face. bir2cir states it; a document arriving without it would emit a type whose read-only
    // view exists in Kotlin and not on the CLR, and the failure would surface as an InvalidCastException in an
    // unrelated caller. Refuse it at the boundary instead.

    static void CheckCollectionViewFaces(string owner, IEnumerable<TypeNode> interfaces)
    {
        var stated = interfaces.OfType<TypeNode.Fqn>().ToList();
        foreach (var face in stated)
        {
            var sibling = CollectionViewFaces.ReadOnlySibling(face);
            if (sibling == null || stated.Contains(sibling)) continue;
            throw new IrSanityException(owner,
                $"type states the mutable collection face '{TypeNode.ToJson(face)}' without its read-only view "
                + $"'{TypeNode.ToJson(sibling)}'; the read-only face is a CLR representation decision bir2cir must "
                + "state (bir-common/CollectionViewFaces.cs), not one the emitter may infer");
        }
    }


    static void CheckSuspendDecl(string label, bool suspend)
    {
        if (suspend) throw new IrSanityException(label,
            "declaration still carries 'mods.suspend': the Kotlin suspend modifier is consumed by bir2cir's "
            + "cold-core lowering and must not reach CIR (every suspend declaration becomes a state machine, a "
            + "cold entry and a Task bridge; one the stdlib self-build retains keeps only its physical stub body)");
    }

    static void CheckPInvokeOwner(string label, bool malformed)
    {
        if (malformed) throw new IrSanityException(label,
            "a CIR 'pinvoke' descriptor requires extern:true, static:true, no type parameters, an empty body, " +
            "and no surviving mods.external source fact");
    }

    static void CheckPInvokeComplete(string label, bool complete)
    {
        if (!complete) throw new IrSanityException(label,
            "a CIR 'pinvoke' descriptor must state the complete normalized MethodImport instruction");
    }


    static void CheckPInvokeFields(string label, string name, HashSet<string> fields, string entryPoint, string charSet,
        bool exactSpelling, bool setLastError, bool preserveSig, bool bestFitMapping, bool throwOnUnmappableChar)
    {
        if (!fields.Contains("CallingConvention") || entryPoint != name && !fields.Contains("EntryPoint") ||
            charSet != "none" && !fields.Contains("CharSet") ||
            exactSpelling && !fields.Contains("ExactSpelling") ||
            setLastError && !fields.Contains("SetLastError") ||
            !preserveSig && !fields.Contains("PreserveSig") ||
            bestFitMapping && !fields.Contains("BestFitMapping") ||
            throwOnUnmappableChar && !fields.Contains("ThrowOnUnmappableChar"))
            throw new IrSanityException(label,
                "a CIR 'pinvoke' descriptor has normalized values not represented by its pseudoFields instruction");
    }


    static void CheckDuplicateLabel(string declaration, int id, HashSet<int> seen)
    {
        if (!seen.Add(id)) throw new IrSanityException(declaration, $"duplicate CFG label id {id} in the same body");
    }

    static void CheckKnownLocal(string declaration, string kind, string name, HashSet<string> declared)
    {
        if (!declared.Contains(name))
            throw new IrSanityException(declaration, $"'{kind}' references undeclared local '{name}' (no matching var/param in scope)");
    }

    static void CheckKnownLabel(string declaration, string kind, int id, HashSet<int> labels)
    {
        if (!labels.Contains(id))
            throw new IrSanityException(declaration, $"'{kind}' targets CFG label id {id} with no matching 'label' node in the body");
    }

    static void CheckLoopComparison(string declaration, string comparison)
    {
        if (comparison is not ("<=" or "<" or ">="))
            throw new IrSanityException(declaration, $"'for' loop has unsupported 'cmp' operator '{comparison}' (expected '<=', '<', or '>=')");
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
    //       declared type (`Sequence<!0>` against an instantiated `Sequence<!1>`, `object` against a `!!0`). The
    //       generic-frame pairs in the calibration set fall in this class; an already-instantiated nested result is
    //       no longer re-substituted and does not rely on this equivalence (#328).
    //   (b) The three spellings of one CLR type — `kotlin.Boolean` / `bool` / `System.Boolean`, `kotlin.Unit` /
    //       `void` / `System.Void`, `kotlin.Any` / `object`, … — are one canonical token. bir2cir lowers a type token
    //       when its pass runs, so a node retyped by an early pass and one retyped by a late pass carry the same type
    //       in different vocabularies (8 of the 58: `kotlin.Boolean` vs `bool`, `kotlin.Unit` vs `System.Void`).
    //   (c) `kotlin.Nothing` matches anything — the bottom type inhabits every slot, and it also erases to `object`.
    //   (d) `nullable`/`oblivious` wrappers are stripped on both sides before comparing: nullability is an annotation
    //       axis that DeclNullableFlags/ReferenceNullableStrip move off the type at their own point in the pipeline,
    //       not a difference of which type the node produces. (Stripping does not ADD acceptance — it makes the type
    //       UNDER the wrapper comparable, so it is what lets `String?` vs `Int?` be refuted at all.)
    //   (e) `{t:array,elem:E}` and the name-keyed `kotlin.Array<E>` are the same type in two spellings (spec §2.7
    //       *One deriver, two layers* — `StaticType.Surface` mints the name-keyed one on purpose). Like (d), this
    //       makes the ELEMENTS comparable rather than accepting more.
    //   (f) Anything not structurally comparable — two different `t` discriminators, two `fqn`s of the same name with
    //       DIFFERENT arities, two `fn`s of different arity — is ACCEPTED. An erasure that drops or adds a generic
    //       argument is a shape this check declines to judge; the check exists for the class that bit in FU-⑧, which
    //       is a same-shape, same-arity pair whose ARGUMENT NAMES a different type.
    //
    // What is left is exactly the refutation: two `fqn`s whose canonical names differ, or whose same-arity arguments
    // recursively refute (likewise two `fn`s). Note which arms therefore do WORK: (d) and (e) are what make a
    // refutation REACHABLE through a wrapper or across the two array spellings — without them `String?` vs `Int?`
    // and `Array<String>` vs `int[]` would fall to the catch-all and be accepted — while (a) restates, for a reader,
    // an acceptance the catch-all already gives (a `tv` is neither `Fqn` nor `Fn`, so nothing refutes it). Physical
    // existential names deliberately have no semantic meaning here: the lowering that introduces one must rewrite or
    // drop the stamp, and trusted [KotlinType] metadata alone records its source relation. The `tests/ir/selftest`
    // fixtures pin (b), (c), the arity guard in (f), and the two refutations (d)/(e) unlock.
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
                _ => true,                                                                                // (f)
            };
        if (b is TypeNode.Array bb)
            return a is TypeNode.Fqn { Name: "kotlin.Array", Args: { Length: 1 } ag } ? StampAgrees(ag[0], bb.Elem) : true;
        if (a is TypeNode.Fqn fa && b is TypeNode.Fqn fb)
        {
            if (fa.Name == Bottom || fb.Name == Bottom) return true;                                       // (c)
            if (Canon(fa.Name) != Canon(fb.Name)) return false;                                            // REFUTED
            if (fa.Args == null || fb.Args == null || fa.Args.Length != fb.Args.Length) return true;        // (f)
            for (var i = 0; i < fa.Args.Length; i++)
                if (!StampAgrees(fa.Args[i], fb.Args[i])) return false;
            return true;
        }
        if (a is TypeNode.FnPtr ap && b is TypeNode.FnPtr bp)
        {
            if (ap.CallingConvention != bp.CallingConvention || ap.GenericArity != bp.GenericArity
                || ap.RequiredParams != bp.RequiredParams || ap.Params.Length != bp.Params.Length
                || !StampAgrees(ap.Ret, bp.Ret)) return false;
            for (var i = 0; i < ap.Params.Length; i++)
                if (!StampAgrees(ap.Params[i], bp.Params[i])) return false;
            return true;
        }
        if (a is TypeNode.Fn na && b is TypeNode.Fn nb)
        {
            if (!StampAgrees(na.Ret, nb.Ret)) return false;
            var pa = na.DelegateParams;
            var pb = nb.DelegateParams;
            if (pa.Length != pb.Length) return true;                                                       // (f)
            for (var i = 0; i < pa.Length; i++)
                if (!StampAgrees(pa[i], pb[i])) return false;
            return true;
        }
        return true;                                                                                       // (f)
    }

    static string Canon(string name) => CanonName.TryGetValue(name, out var c) ? c : name;
}
