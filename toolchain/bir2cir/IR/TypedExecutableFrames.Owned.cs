using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypedExecutableFrames
{
    // Normalize the declaration supplied by the producer, not the declarations
    // of nested suspend values. Their construction edges still belong here.
    internal static void RewriteDeclarationFrameInPlace(BirGraphValue root, Func<TypeNode, TypeNode> rewrite)
    {
        Walk(root, true);
        void Walk(BirGraphValue node, bool declarationRoot)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (!declarationRoot && node is BirNewSuspendLambda nested)
            {
                if (nested.TypeArgs.HasValue) nested.TypeArgs = nested.TypeArgs.Value.Select(rewrite).ToArray();
                if (nested.CapValues.HasValue)
                    foreach (var value in nested.CapValues.Value) Walk(value as BirGraphValue, false);
                if (nested.FuncType.HasValue) nested.FuncType = rewrite(nested.FuncType.Value);
                if (nested.Sty.HasValue) nested.Sty = rewrite(nested.Sty.Value);
                return;
            }
            node.RewriteGenericFrameInPlace((role, type) => Substitutes(role) ? rewrite(type) : type,
                (role, child) => {
                    if (Substitutes(role) && role != BirGenericFrameRole.SyntheticDeclaration) Walk(child, false);
                });
        }
    }

    // A producer has already reported the independent clone (if any). Generic
    // instantiation must retain every registered declaration occurrence inside it.
    internal static void SubstituteInPlace(BirGraphValue root, TypeNode[] methodArgs, int arity,
        TypeNode[] ownerArgs = null, bool typeScope = true)
    {
        if (root == null || root is IBirExecutablePayload) return;
        methodArgs ??= Array.Empty<TypeNode>();
        if (root is BirNewSuspendLambda suspend)
        {
            var dense = suspend.TypeFrame.OrDefault() == "dense" && !suspend._suspendDeclarationTypeArgs.HasValue;
            if (!dense && !suspend._suspendDeclarationTypeArgs.HasValue && suspend.TypeArgs.HasValue)
                suspend._suspendDeclarationTypeArgs = suspend.TypeArgs.Value.Select(CopyType).ToArray();
            // An unspliced dense declaration already owns its body, parameters
            // and result frame. Its capture descriptors, like its construction
            // values, still name the enclosing frame being instantiated.
            if (dense)
                foreach (var capture in suspend.Captures.OrEmpty())
                    if (capture.Type.HasValue)
                        capture.Type = SubstituteType(capture.Type.Value, methodArgs, arity, ownerArgs, typeScope);
            if (suspend.TypeArgs.HasValue)
                suspend.TypeArgs = suspend.TypeArgs.Value.Select(type => SubstituteType(type, methodArgs, arity, ownerArgs, typeScope)).ToArray();
            if (suspend.CapValues.HasValue)
                foreach (var value in suspend.CapValues.Value)
                    SubstituteInPlace(value as BirGraphValue, methodArgs, arity, ownerArgs, typeScope);
            if (suspend.FuncType.HasValue)
                suspend.FuncType = SubstituteType(suspend.FuncType.Value, methodArgs, arity, ownerArgs, typeScope);
            if (suspend.Sty.HasValue)
                suspend.Sty = SubstituteType(suspend.Sty.Value, methodArgs, arity, ownerArgs, typeScope);
            return;
        }
        root.RewriteGenericFrameInPlace(
            (role, type) => Substitutes(role) ? SubstituteType(type, methodArgs, arity, ownerArgs, typeScope) : type,
            (role, child) => {
                if (Substitutes(role)) SubstituteInPlace(child, methodArgs, arity, ownerArgs,
                    typeScope && role != BirGenericFrameRole.SyntheticDeclaration);
            });
    }
}
