using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// MEMBER-STRIP (clrName migration) — the member-level mirror of the @ClrTypeAlias type-strip. Once kotc stops reading
// @ClrIntrinsic it can no longer exclude a bound-stub declaration (the `clrName(it)==null` filters in BirEmitter), so
// those @ClrIntrinsic-bound members/top-level funs get EMITTED (with throwing TODO bodies). This pass DROPS them: the
// call sites are substituted to the BCL member by MemberCallSubstitution, so the stub itself must not survive. Matched
// by FULL SIGNATURE (name + canonical param types) so StringBuilder.append(Char)@ClrIntrinsic is dropped while
// append(CharSequence?) (rule-3, real body) is kept. For an ALIAS-class owner a member that merely OVERRIDES a
// @ClrIntrinsic member is ALSO a bound stub (its call substitutes to the BCL), so it is dropped too (else it over-hoists
// into the rule-3 helper). Runs BEFORE AliasHelperHoist. Never in ref.
static partial class MemberStrip
{
    public static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs,
        Action<BirFunctionDeclaration> retireActual)
    {
        if (root.FileClass.HasValue && root.Methods.HasValue)
            root.Methods = StripFrom(root.Methods.Value, root.FileClass.Value, refs, null, false, retireActual);
        foreach (var declaration in root.Types.OrEmpty())
            if (declaration.Name.HasValue)
            {
                // NEVER strip an INTERFACE's members: a non-alias interface (EnumEntries, MatchGroupCollection) declares
                // the CLR slot (renamed get_Item/get_Count) that implementers bind to — it is not a throwing bound stub.
                // (A @ClrTypeAlias interface is dropped whole by AliasHelperHoist anyway.)
                if (declaration.Kind.OrDefault() == "interface") continue;
                var strippedPropertyAssociations = new HashSet<string>(StringComparer.Ordinal);
                var owner = declaration.Name.Value;
                var isAlias = ReferenceMetadataIndex.BareOwnerFqn(owner) is string bo && refs.Aliases.ContainsKey(bo);
                if (declaration.Methods.HasValue)
                    declaration.Methods = StripFrom(declaration.Methods.Value, owner, refs, strippedPropertyAssociations, isAlias, retireActual);
                if (declaration.Properties.HasValue && strippedPropertyAssociations.Count > 0)
                    declaration.Properties = declaration.Properties.Value.Where(property =>
                        !property.PropertyAssociation.HasValue || !strippedPropertyAssociations.Contains(property.PropertyAssociation.Value)).ToArray();
            }
    }

    static BirFunctionDeclaration[] StripFrom(BirFunctionDeclaration[] methods, string owner, ReferenceMetadataIndex refs,
        HashSet<string> strippedPropertyAssociations, bool alias, Action<BirFunctionDeclaration> retireActual)
    {
        HashSet<BirFunctionDeclaration> removed = null;
        for (var i = methods.Length - 1; i >= 0; i--)
        {
            var method = methods[i];
            if (!method.Name.HasValue) continue;
            var name = method.Name.Value;
            var keys = method.Params.OrEmpty().Select(parameter => parameter.Type.HasValue
                ? ReferenceMetadataIndex.ParamKey(parameter.Type.Value) : null).ToList();
            // MemberStrip is not the declaration-shape validator. Leave an incomplete current parameter untouched so
            // the owning lowering can report the missing required type with its declaration context.
            if (keys.Any(key => key == null)) continue;
            // An alias-class member that overrides a @ClrIntrinsic ancestor is normally a bound stub (its call
            // substitutes to the BCL), so it is dropped. But a GENUINE rule-3 member — concrete + intrinsic-less in
            // the ref.dll (String.compareTo's ordinal body overriding the culture-sensitive Comparable.compareTo@ClrIntrinsic)
            // — carries a REAL Kotlin body that must be PRESERVED and hoisted (else the call would resolve to the
            // semantically-wrong BCL slot). IsRule3Member is exactly that ref.dll signal, so exempt it from the override-drop.
            // An abstract Kotlin class obligation is not an external throwing stub. Its inherited native
            // binding annotation names a slot to implement; only an aliased owner supplies that implementation.
            var abstractKotlinDeclaration = !alias && method.Abstract.OrDefault();
            var drop = !abstractKotlinDeclaration && refs.IsBoundStub(owner, name, keys)
                || (alias && method.Overrides.HasValue
                    && DeclarationRename.ResolveSlot(method, refs) != null
                    && !refs.IsRule3Member(owner, name));
            if (drop)
            {
                if (strippedPropertyAssociations != null
                    && KotlinPropertyAccessors.TryIdentity(method, out _, out _)
                    && method.PropertyAssociation.HasValue)
                    strippedPropertyAssociations.Add(method.PropertyAssociation.Value);
                // Report the exact removed occurrence, including uncaptured
                // prefixes, before detaching it. Captured ordinal vectors stay
                // untouched; retained declarations keep their original order.
                retireActual(method);
                (removed ??= new(ReferenceEqualityComparer.Instance)).Add(method);
            }
        }
        return removed == null ? methods : methods.Where(method => !removed.Contains(method)).ToArray();
    }
}
