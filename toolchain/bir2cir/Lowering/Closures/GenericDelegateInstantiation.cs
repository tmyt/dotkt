using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// bir2cir — GenericDelegateInstantiation (#191): a delegated property backed by a GENERIC user delegate
// (`var x by D<T>(…)`, whose `operator fun getValue(…): T` / `setValue(…, nv: T)` are generic over T)
// miscompiles — BadImageFormatException at load / ilverify `found 'string' expected '!0'`.
//
// kotc emits the delegate's `getValue`/`setValue` dispatch with the delegate class's BARE FQN identity as the
// `ownerType` (`"D"`, no type args — the kotc-purity contract: it names the type, bir2cir derives the CLR
// resolution) while the backing `$delegate` field/local carries the CONCRETE instantiation `D<String>`. With a
// bare open owner ilemit builds the method reference against the OPEN generic `D`1` (getValue returns `!0`,
// setValue takes `!0`), but the receiver on the stack is the constructed `D`1<string>` — ECMA-335 §II.9.8: the
// effective member signature comes from the CONSTRUCTED declaring type, so an open owner mismatches the
// constructed receiver (ilverify StackUnexpected, load BadImageFormatException). A NON-generic delegate has no
// type args to lose, so it already worked.
//
// Fix (exactly bir2cir's job — the delegated-property analog of GenericSelfInstantiation): for a delegate
// accessor call (`getValue`/`setValue`/`provideDelegate`) whose `ownerType` is a bare generic FQN, recover the
// receiver's static instantiation and, when it instantiates that very owner (`D<String>`), rewrite `ownerType`
// to the constructed token. Runs BEFORE BirTypeLowering, which then lowers `D<kotlin.String>` to the CLR
// constructed generic consistently with the receiver field/local — so the `!0` slot binds to the concrete arg
// on both sides.
//
// Two receiver shapes recover the instantiation differently under #122's sty/lexical-scope model:
//   • a `local` `$delegate` (a `val x by D(…)` LOCAL property) carries its declared `D<…>` via the lexical
//     environment, so BirGraphStaticType.Surface resolves it directly.
//   • a `field`/`staticField` `$delegate` (a MEMBER / TOP-LEVEL property) is a bare read carrying no `sty`/`ret`
//     stamp (#122 dropped the global field-type collect that used to answer it). Its constructed type is the
//     DECLARED field type from the BIR type table (`types[].fields[]` / top-level `fields[]`), a carried
//     frontend fact — NOT a ref.dll re-resolution. When the enclosing class is generic (`class C<T>{var x by D(…)}`),
//     that declared type is `D<!type#0>`; we substitute the enclosing type-vars with the receiver-owner's own
//     actual args (`C<String>` -> `D<String>`).
static partial class GenericDelegateInstantiation
{
    static readonly HashSet<string> Accessors = new(System.StringComparer.Ordinal)
        { "getValue", "setValue", "provideDelegate" };

    // (ownerClass, fieldName) -> the DECLARED field type, read from the BIR type table (a carried frontend fact).
    // The `$delegate` field read itself carries no type stamp, so this is the only source for its `D<…>` type.
    sealed class FieldTypes
    {
        readonly Dictionary<(string, string), TypeNode> _map = new();
        public void Add(string owner, string field, TypeNode type)
        {
            if (owner != null && field != null && type != null) _map[(owner, field)] = type;
        }
        public TypeNode Lookup(string owner, string field) =>
            owner != null && field != null && _map.TryGetValue((owner, field), out var t) ? t : null;
    }

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        var fields = CollectFieldTypes(roots);
        foreach (var root in roots)
        {
            // BirGraphStaticType.Surface reads the frontend `sty` stamp via lexical scope — no global
            // Refs/LocalTypes collect setup is needed (that re-inference machinery was removed). A bare
            // `field`/`staticField` `$delegate` read carries no stamp, so its declared type comes from `fields`.
            Walk(root, ImmutableDictionary.Create<string,TypeNode>(System.StringComparer.Ordinal), fields);
        }
    }

    // Index every declared field type: a type's members (`types[].fields[]`, keyed by the type's own name — matching
    // the field-read `ownerType`) and the file's top-level statics (`fields[]`, keyed by the `fileClass` — matching a
    // top-level `staticField` read's `AppKt` owner).
    static FieldTypes CollectFieldTypes(IReadOnlyList<BirFileDeclaration> roots)
    {
        var ft = new FieldTypes();
        foreach (var root in roots)
        {
            foreach (var type in root.Types.OrEmpty())
                foreach (var field in type.Fields.OrEmpty())
                    ft.Add(type.Name.OrDefault(), field.Name.OrDefault(), field.Type.OrDefault());
            foreach (var field in root.Fields.OrEmpty())
                ft.Add(root.FileClass.OrDefault(), field.Name.OrDefault(), field.Type.OrDefault());
        }
        return ft;
    }

    static void Walk(BirGraphValue node, ImmutableDictionary<string,TypeNode> scope, FieldTypes fields)
    {
        if (node == null || node is IBirExecutablePayload) return;
        var child = scope;
        node.VisitSourceParameters((name, type) => child = child.SetItem(name, type));
        // Preserve preorder: this accessor first closes its direct field receiver, then its own owner.
        if (node is BirCallInstance call) Rewrite(call, child, fields);
        node.VisitSourceChildren(slot => Walk(slot.Value, child, fields), region => {
            var current = child;
            foreach (var statement in region.Statements)
            {
                Walk((BirGraphValue)statement, current, fields);
                // A declaration enters only its subsequent siblings, never its own initializer or another vector.
                if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                    current = current.SetItem(variable.Name.Value, variable.Type.Value);
            }
            // The old walk gave sibling result fields the incoming scope, not the completed statement-vector scope.
            if (region.Result is { } result) Walk(result.Value, child, fields);
        });
    }

    static void Rewrite(BirCallInstance call, IReadOnlyDictionary<string,TypeNode> scope, FieldTypes fields)
    {
        if (!call.Method.HasValue || !Accessors.Contains(call.Method.Value) || !call.Recv.HasValue) return;
        var recv = call.Recv.Value;
        // (a) The `$delegate` field itself, when the ENCLOSING class is generic (`class C<T> { var x by D(…) }`
        //     accessed as `c.x` from outside): kotc emits the field read with a BARE owner (`C`, no args) while the
        //     receiver is the constructed `C<String>`. A bare field owner loads the OPEN field type (`D<!0>`),
        //     mismatching the constructed-`D<String>` getValue call below. Instantiate the field owner from its own
        //     receiver so the load yields `D<String>`.
        if (recv is BirFieldGet field) InstantiateOwnerFromRecv(field, scope);
        // (b) The delegate accessor names the bare classifier. An already constructed owner, such as the
        //     stdlib ReadWriteProperty application, states its own frame and must stay untouched.
        if (call.OwnerType.OrDefault() is not TypeNode.Fqn owner) return;
        if (owner.Args is { Length: > 0 }) return;
        // The receiver IS the delegate instance; its static type is the constructed `D<…>`. Rewrite only when it
        // instantiates this very owner (name match), so a subtype/unrelated recovery never rewrites the owner.
        if (RecoverDelegateType(recv, scope, fields) is not TypeNode.Fqn { Args: { Length: > 0 } } inst) return;
        if (inst.Name != owner.Name) return;
        call.OwnerType = BirGraphValue.CopyType(inst);
    }

    // The delegate instance receiver's constructed static type. A `field`/`staticField` read has no `sty`/`ret` stamp,
    // so its type is the DECLARED field type (with the enclosing type's type-vars substituted by the receiver-owner's
    // actual args — the field owner was just instantiated by InstantiateOwnerFromRecv). Every other receiver shape
    // (a `local` `$delegate`, a `provideDelegate` temp) resolves through the sty/lexical-scope Surface as before.
    static TypeNode RecoverDelegateType(IBirExpression recv, IReadOnlyDictionary<string,TypeNode> scope, FieldTypes fields)
    {
        var (owner, name) = recv switch {
            BirFieldGet field => (field.OwnerType.OrDefault(), field.Name.OrDefault()),
            BirStaticFieldGet field => (field.OwnerType.OrDefault(), field.Name.OrDefault()),
            _ => (null, null),
        };
        if (owner is TypeNode.Fqn fqn && fields.Lookup(fqn.Name, name) is TypeNode declared)
            return SubstTypeVars(declared, fqn.Args);
        return BirGraphStaticType.Surface(recv, scope);
    }

    // Substitute an enclosing TYPE's type-vars (`tv scope=type i`) with the receiver-owner's actual generic args.
    // A non-generic owner (no args) leaves an already-fully-constructed declared type (`D<String>`) untouched.
    // When `ownerArgs` is null (an accessor-internal `this.x$delegate` read whose receiver static type is unknown) a
    // `type`-scope tv is left VERBATIM — correct there because the accessor body shares the enclosing type's `!i`
    // space, so the un-substituted `D<!type#0>` owner already matches the call site. A method-scope tv can never
    // appear in a declared field type, so it is never rewritten. (Latent: a NESTED enclosing generic
    // `Outer<A>.Inner<B>` uses a FLATTENED `tv type#i` whose index need not align 1:1 with the constructed Inner
    // receiver's Args — not exercised by any delegated-property shape today.)
    static TypeNode SubstTypeVars(TypeNode t, TypeNode[] ownerArgs)
    {
        switch (t)
        {
            case TypeNode.Tv { Scope: "type" } tv when ownerArgs != null && tv.I >= 0 && tv.I < ownerArgs.Length:
                return ownerArgs[tv.I];
            case TypeNode.Fqn { Args: { Length: > 0 } } f:
                return new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstTypeVars(a, ownerArgs)).ToArray());
            case TypeNode.Nullable n: return new TypeNode.Nullable(SubstTypeVars(n.Of, ownerArgs));
            case TypeNode.Oblivious ob: return new TypeNode.Oblivious(SubstTypeVars(ob.Of, ownerArgs));
            case TypeNode.Array a: return new TypeNode.Array(SubstTypeVars(a.Elem, ownerArgs));
            case TypeNode.ByRef b: return new TypeNode.ByRef(SubstTypeVars(b.Of, ownerArgs));
            case TypeNode.Fn fn:
                return new TypeNode.Fn(fn.Suspend, SubstTypeVars(fn.Ret, ownerArgs),
                    fn.Params.Select(p => SubstTypeVars(p, ownerArgs)).ToArray(),
                    fn.Recv == null ? null : SubstTypeVars(fn.Recv, ownerArgs));
            default: return t;
        }
    }

    // Instantiate a BARE generic owner on a `field` read from its OWN receiver's static type (`x$delegate` on a bare
    // `C` -> `C<String>` when the receiver `c` is `C<String>`). Scoped to a delegate accessor's receiver; the same
    // name-match fail-safe as the call rewrite (only the receiver's own instantiation, never a foreign one).
    static void InstantiateOwnerFromRecv(BirFieldGet field, IReadOnlyDictionary<string,TypeNode> scope)
    {
        if (field.OwnerType.OrDefault() is not TypeNode.Fqn owner || owner.Args is { Length: > 0 }
            || !field.Recv.HasValue) return;
        if (BirGraphStaticType.Surface(field.Recv.Value, scope) is not TypeNode.Fqn { Args: { Length: > 0 } } inst) return;
        if (inst.Name != owner.Name) return;
        field.OwnerType = BirGraphValue.CopyType(inst);
    }
}
