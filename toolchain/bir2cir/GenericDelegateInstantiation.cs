using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

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
//     BirScope, so StaticType.Surface resolves it directly.
//   • a `field`/`staticField` `$delegate` (a MEMBER / TOP-LEVEL property) is a bare read carrying no `sty`/`ret`
//     stamp (#122 dropped the global field-type collect that used to answer it). Its constructed type is the
//     DECLARED field type from the BIR type table (`types[].fields[]` / top-level `fields[]`), a carried
//     frontend fact — NOT a ref.dll re-resolution. When the enclosing class is generic (`class C<T>{var x by D(…)}`),
//     that declared type is `D<!type#0>`; we substitute the enclosing type-vars with the receiver-owner's own
//     actual args (`C<String>` -> `D<String>`).
static class GenericDelegateInstantiation
{
    static readonly HashSet<string> Accessors = new(System.StringComparer.Ordinal)
        { "getValue", "setValue", "provideDelegate" };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // (ownerClass, fieldName) -> the DECLARED field type, read from the BIR type table (a carried frontend fact).
    // The `$delegate` field read itself carries no type stamp, so this is the only source for its `D<…>` type.
    sealed class FieldTypes
    {
        readonly Dictionary<(string, string), TypeNode> _map = new();
        public void Add(string owner, string field, JsonNode type)
        {
            if (owner != null && field != null && TypeJson.Read(type) is TypeNode t)
                _map[(owner, field)] = t;
        }
        public TypeNode Lookup(string owner, string field) =>
            owner != null && field != null && _map.TryGetValue((owner, field), out var t) ? t : null;
    }

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var fields = CollectFieldTypes(roots);
        foreach (var root in roots)
        {
            // #122: StaticType.Surface reads the frontend `sty` stamp via lexical scope — no global
            // Refs/LocalTypes collect setup is needed (that re-inference machinery was removed). A bare
            // `field`/`staticField` `$delegate` read carries no stamp, so its declared type comes from `fields`.
            switch (root)
            {
                case JsonObject o: WalkObject(o, BirScope.Empty, fields); break;
                case JsonArray a: WalkArray(a, BirScope.Empty, fields); break;
            }
        }
    }

    // Index every declared field type: a type's members (`types[].fields[]`, keyed by the type's own name — matching
    // the field-read `ownerType`) and the file's top-level statics (`fields[]`, keyed by the `fileClass` — matching a
    // top-level `staticField` read's `AppKt` owner).
    static FieldTypes CollectFieldTypes(IReadOnlyList<JsonNode> roots)
    {
        var ft = new FieldTypes();
        foreach (var root in roots)
        {
            if (root is not JsonObject ro) continue;
            if (ro["types"] is JsonArray types)
                foreach (var t in types)
                    if (t is JsonObject to && Str(to["name"]) is string tn && to["fields"] is JsonArray tfs)
                        foreach (var f in tfs)
                            if (f is JsonObject fo) ft.Add(tn, Str(fo["name"]), fo["type"]);
            if (Str(ro["fileClass"]) is string fc && ro["fields"] is JsonArray topFields)
                foreach (var f in topFields)
                    if (f is JsonObject fo) ft.Add(fc, Str(fo["name"]), fo["type"]);
        }
        return ft;
    }

    static void WalkArray(JsonArray arr, BirScope scope, FieldTypes fields)
    {
        var cur = scope;
        foreach (var it in arr)
        {
            switch (it)
            {
                case JsonObject co: WalkObject(co, cur, fields); break;
                case JsonArray ca: WalkArray(ca, cur, fields); break;
            }
            // A `var` is in scope for its SUBSEQUENT siblings only (lexical block scoping) — so a `local`
            // receiver (a `val x by D(…)` LOCAL delegated property's `x$delegate` local) resolves.
            if (it is JsonObject vo && Str(vo["k"]) == "var")
            {
                if (ReferenceEquals(cur, scope)) cur = scope.Child();
                cur.Declare(vo);
            }
        }
    }

    static void WalkObject(JsonObject obj, BirScope scope, FieldTypes fields)
    {
        var child = scope.Extend(obj);   // bind params (a method/ctor decl) for the body's scope
        Rewrite(obj, child, fields);
        foreach (var key in obj.Select(kv => kv.Key).ToList())
            switch (obj[key])
            {
                case JsonObject co: WalkObject(co, child, fields); break;
                case JsonArray ca: WalkArray(ca, child, fields); break;
            }
    }

    static void Rewrite(JsonObject o, BirScope scope, FieldTypes fields)
    {
        if (Str(o["k"]) != "callInstance") return;
        if (Str(o["method"]) is not string m || !Accessors.Contains(m)) return;
        if (o["recv"] is not JsonNode recv) return;
        // (a) The `$delegate` field itself, when the ENCLOSING class is generic (`class C<T> { var x by D(…) }`
        //     accessed as `c.x` from outside): kotc emits the field read with a BARE owner (`C`, no args) while the
        //     receiver is the constructed `C<String>`. A bare field owner loads the OPEN field type (`D<!0>`),
        //     mismatching the constructed-`D<String>` getValue call below. Instantiate the field owner from its own
        //     receiver so the load yields `D<String>`.
        if (recv is JsonObject recvObj) InstantiateOwnerFromRecv(recvObj, scope);
        // (b) The delegate accessor call: kotc names the user delegate class with the BARE FQN string owner (`"D"`,
        //     its purity contract). Read the name via OwnerName (accepts the string OR a structured Fqn), then bail
        //     if ALREADY instantiated — the stdlib ReadWriteProperty path kotc emits as a structured generic Fqn.
        if (TypeJson.OwnerName(o["ownerType"]) is not string owner) return;
        if (TypeJson.Read(o["ownerType"]) is TypeNode.Fqn { Args: { Length: > 0 } }) return;
        // The receiver IS the delegate instance; its static type is the constructed `D<…>`. Rewrite only when it
        // instantiates this very owner (name match), so a subtype/unrelated recovery never rewrites the owner.
        if (RecoverDelegateType(recv, scope, fields) is not TypeNode.Fqn { Args: { Length: > 0 } } inst) return;
        if (inst.Name != owner) return;
        o["ownerType"] = TypeJson.Write(inst);
    }

    // The delegate instance receiver's constructed static type. A `field`/`staticField` read has no `sty`/`ret` stamp,
    // so its type is the DECLARED field type (with the enclosing type's type-vars substituted by the receiver-owner's
    // actual args — the field owner was just instantiated by InstantiateOwnerFromRecv). Every other receiver shape
    // (a `local` `$delegate`, a `provideDelegate` temp) resolves through the sty/lexical-scope Surface as before.
    static TypeNode RecoverDelegateType(JsonNode recv, BirScope scope, FieldTypes fields)
    {
        if (recv is JsonObject ro && Str(ro["k"]) is "field" or "staticField"
            && fields.Lookup(TypeJson.OwnerName(ro["ownerType"]), Str(ro["name"])) is TypeNode decl)
        {
            var ownerArgs = (TypeJson.Read(ro["ownerType"]) as TypeNode.Fqn)?.Args;
            return SubstTypeVars(decl, ownerArgs);
        }
        return StaticType.Surface(recv, scope);
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
    static void InstantiateOwnerFromRecv(JsonObject fld, BirScope scope)
    {
        if (Str(fld["k"]) != "field") return;
        if (TypeJson.OwnerName(fld["ownerType"]) is not string owner) return;
        if (TypeJson.Read(fld["ownerType"]) is TypeNode.Fqn { Args: { Length: > 0 } }) return;
        if (fld["recv"] is not JsonNode frecv) return;
        if (StaticType.Surface(frecv, scope) is not TypeNode.Fqn { Args: { Length: > 0 } } inst) return;
        if (inst.Name != owner) return;
        fld["ownerType"] = TypeJson.Write(inst);
    }
}
