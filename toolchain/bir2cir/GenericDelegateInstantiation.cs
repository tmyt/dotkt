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
// receiver's static instantiation via StaticType.Surface (the `$delegate` field/local declared type) and, when
// it instantiates that very owner (`D<String>`), rewrite `ownerType` to the constructed token. Runs BEFORE
// BirTypeLowering, which then lowers `D<kotlin.String>` to the CLR constructed generic consistently with the
// receiver field/local — so the `!0` slot binds to the concrete arg on both sides.
static class GenericDelegateInstantiation
{
    static readonly HashSet<string> Accessors = new(System.StringComparer.Ordinal)
        { "getValue", "setValue", "provideDelegate" };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        foreach (var root in roots)
        {
            StaticType.Refs = refs;
            StaticType.LocalTypes = StaticType.CollectTypes(root);
            switch (root)
            {
                case JsonObject o: WalkObject(o, BirScope.Empty); break;
                case JsonArray a: WalkArray(a, BirScope.Empty); break;
            }
        }
    }

    static void WalkArray(JsonArray arr, BirScope scope)
    {
        var cur = scope;
        foreach (var it in arr)
        {
            switch (it)
            {
                case JsonObject co: WalkObject(co, cur); break;
                case JsonArray ca: WalkArray(ca, cur); break;
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

    static void WalkObject(JsonObject obj, BirScope scope)
    {
        var child = scope.Extend(obj);   // bind params (a method/ctor decl) for the body's scope
        Rewrite(obj, child);
        foreach (var key in obj.Select(kv => kv.Key).ToList())
            switch (obj[key])
            {
                case JsonObject co: WalkObject(co, child); break;
                case JsonArray ca: WalkArray(ca, child); break;
            }
    }

    static void Rewrite(JsonObject o, BirScope scope)
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
        if (StaticType.Surface(recv, scope) is not TypeNode.Fqn { Args: { Length: > 0 } } inst) return;
        if (inst.Name != owner) return;
        o["ownerType"] = TypeJson.Write(inst);
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
