using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    enum DerivedResultAction { Keep, Stamp, Return, Cast }
    readonly record struct DerivedResult(TypeNode Type, DerivedResultAction Action, bool Dynamic = false);

    // Both graph renderers obey the same ownership and checked reference-array
    // result projection rule. A return stamp is a current physical claim.
    static DerivedResult SelectDerivedResult(TypeNode derived, TypeNode stamped, bool erasureApplied,
        bool ownershipKnown, bool companion)
    {
        if (derived == null) return new(stamped, DerivedResultAction.Keep);
        if (companion) return new(derived, DerivedResultAction.Return, true);
        if (stamped == null) return erasureApplied
            ? new(derived, DerivedResultAction.Return) : new(null, DerivedResultAction.Keep);
        if (derived.Equals(stamped)) return new(stamped,
            erasureApplied ? DerivedResultAction.Stamp : DerivedResultAction.Keep);
        if (!IsObjectErasureOf(derived, stamped) || ownershipKnown && !erasureApplied)
            return new(stamped, DerivedResultAction.Keep);
        return CanNarrowReferenceArrayProjection(derived, stamped)
            ? new(stamped, DerivedResultAction.Cast) : new(derived, DerivedResultAction.Return, true);
    }

    // A selected use's scalar declaration facts. Neither representation invents
    // a declaration or rebuilds a graph to ask the common lookup policy.
    sealed record UseSelection(string Identity, string PropertyName, string AccessorKind,
        string CalleeOwner, TypeNode[] Signature)
    {
        internal bool Property => PropertyName != null;
    }

    static UseSelection ReadUseSelection(JsonObject call)
    {
        KotlinPropertyAccessors.TryCallIdentity(call, out var property, out var accessor);
        var signature = call["shapeTypes"] as JsonArray ?? call["sig"] as JsonArray ?? call["argTypes"] as JsonArray;
        return new(Str(call[DeclarationIdentityBinding.Key]), property, accessor,
            TypeJson.OwnerName(call["calleeOwner"]), signature?.Select(TypeJson.Read).ToArray());
    }

    static UseSelection ReadUseSelection(BirGraphValue node)
    {
        var (name, role, method, prop, owner, signature) = node switch {
            BirCallStatic call => (call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Method.OrDefault(),
                call.Prop.OrDefault(), call.CalleeOwner.OrDefault(), call.ShapeTypes.OrDefault() ?? call.Sig.OrDefault() ?? call.ArgTypes.OrDefault()),
            BirCallInstance call => (call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Method.OrDefault(),
                call.Prop.OrDefault(), (TypeNode)null, call.ShapeTypes.OrDefault() ?? call.Sig.OrDefault() ?? call.ArgTypes.OrDefault()),
            _ => ((string)null, null, null, null, (TypeNode)null, (TypeNode[])null),
        };
        KotlinPropertyAccessors.TryCallIdentity(name, role, method, prop, out var property, out var accessor);
        return new(node.DeclarationIdentityFact.OrDefault(), property, accessor,
            (owner as TypeNode.Fqn)?.Name, signature);
    }

    static TypeNode SourceOwnType(BirGraphValue node, string field)
    {
        TypeNode result = null;
        node.VisitOwnTypeFields((name, value) => { if (name == field) result = value; });
        return result;
    }

    static void SourceRestamp(BirGraphValue node, TypeNode type) =>
        node.RewriteOwnTypeFieldsInPlace((field, _, current) => field == "sty" ? BirGraphValue.CopyType(type) : current);

    static void SourceWriteReturn(BirGraphValue node, TypeNode type)
    {
        type = BirGraphValue.CopyType(type);
        switch (node) {
            case BirCallStatic call: call.Ret = type; break;
            case BirCallInstance call: call.Ret = type; break;
            case BirNativeStatic call: call.Ret = type; break;
            case BirNativeInstance call: call.Ret = type; break;
            case BirGenericStatic call: call.Ret = type; break;
            case BirGenericInstance call: call.Ret = type; break;
            default: throw new InvalidOperationException("Nullable use cannot stamp this call's return");
        }
    }

    static void SourceWriteDynamicReturn(BirGraphValue node, TypeNode type) =>
        node.RewriteOwnTypeFieldsInPlace((field, _, current) => field == "dynRet" ? BirGraphValue.CopyType(type) : current);

    static IBirExpression[] SourceArguments(BirGraphValue node) => node switch {
        BirCallStatic call => call.Args.OrDefault(), BirCallInstance call => call.Args.OrDefault(),
        BirNew call => call.Args.OrDefault(), BirNewClr call => call.Args.OrDefault(),
        BirNativeStatic call => call.Args.OrDefault(), BirNativeInstance call => call.Args.OrDefault(),
        BirGenericStatic call => call.Args.OrDefault(), BirGenericInstance call => call.Args.OrDefault(),
        BirDelegateInvoke call => call.Args.OrDefault(), _ => null,
    };

    // This descriptor precedence is specific to nullable-use argument flow; it
    // is not the final emitter/memberRef parameter selection policy.
    static TypeNode[] SourceDescriptor(BirGraphValue node) => node switch {
        BirCallStatic call => call.Sig.OrDefault() ?? call.ArgTypes.OrDefault() ?? call.ResolvedMemberParams.OrDefault(),
        BirCallInstance call => call.Sig.OrDefault() ?? call.ArgTypes.OrDefault() ?? call.ResolvedMemberParams.OrDefault(),
        BirNew call => call.ArgTypes.OrDefault() ?? call.ResolvedMemberParams.OrDefault(),
        BirNewClr call => call.ArgTypes.OrDefault() ?? call.ResolvedMemberParams.OrDefault(),
        BirNativeStatic call => call.Sig.OrDefault() ?? call.ArgTypes.OrDefault() ?? call.ResolvedMemberParams.OrDefault(),
        BirNativeInstance call => call.Sig.OrDefault() ?? call.ArgTypes.OrDefault() ?? call.ResolvedMemberParams.OrDefault(),
        BirGenericStatic call => call.ResolvedMemberParams.OrDefault(),
        BirGenericInstance call => call.ResolvedMemberParams.OrDefault(),
        _ => null,
    };

    static TypeNode SourceStaticOwner(BirCallStatic call) =>
        call.OwnerType.OrDefault() ?? call.Owner.OrDefault() ?? call.CalleeOwner.OrDefault();

    static TypeNode.Fqn SourceClrOwner(BirGraphValue node) => ClrOwner(node switch {
        BirNativeStatic call => call.Type.OrDefault(), BirNativeInstance call => call.Type.OrDefault(),
        BirGenericStatic call => call.Type.OrDefault(), BirGenericInstance call => call.Type.OrDefault(),
        BirNewClr call => call.Type.OrDefault(), BirBoundClrDelegate call => call.ClrType.OrDefault(),
        BirClrStaticDelegate call => call.ClrType.OrDefault(), BirPropertyGet call => call.Type.OrDefault(),
        BirPropertySet call => call.Type.OrDefault(), BirEventAdd call => call.Type.OrDefault(),
        BirEventRemove call => call.Type.OrDefault(), _ => null,
    });
}
