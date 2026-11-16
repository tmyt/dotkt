using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    string RehoistPayloadDelegates(BirInlinePayload payload, BirValueBlock body)
    {
        if (!payload.Lifted.HasValue) return "[KotlinInline] payload is missing its required lifted-method set";
        if (payload.Lifted.Value.Length == 0) { LexicalDeclarationIds.Freshen(body); return null; }
        if (!payload.FileClass.HasValue) return "[KotlinInline] payload carries lifted methods but has no origin file-class identity";
        var destination = Pass.File.FileClass.OrDefault();
        if (destination == null) return "cannot re-hoist a closed [KotlinInline] delegate target because the consuming BIR file has no fileClass";
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var methods = new List<BirFunctionDeclaration>();
        foreach (var method in payload.Lifted.Value)
        {
            if (!method.Name.HasValue || !method.Generated.OrDefault() || !method.Static.OrDefault()
                || !method.Params.HasValue || !method.Ret.HasValue || !method.Body.HasValue)
                return "[KotlinInline] payload contains a malformed or non-generated lifted method";
            if (!names.TryAdd(method.Name.Value, "__inl$lift$" + Pass.NextSpliceIdentity()))
                return $"[KotlinInline] payload carries duplicate lifted method '{method.Name.Value}'";
            methods.Add((BirFunctionDeclaration)method.DeepClone());
        }
        RewriteCarriedDelegateEdges(body, payload.FileClass.Value, destination, names);
        foreach (var method in methods)
        {
            method.Name = names[method.Name.Value];
            method.SemanticOwner = default;
            RewriteCarriedDelegateEdges(method, payload.FileClass.Value, destination, names);
        }
        LexicalDeclarationIds.Freshen(new[] { (BirGraphValue)body }.Concat(methods).ToArray());
        Pass.PendingHelpers.AddRange(methods);
        return null;
    }

    static void RewriteCarriedDelegateEdges(BirGraphValue root, string origin, string destination,
        IReadOnlyDictionary<string, string> names) => root.Visit(node => {
        if (node is BirNewDelegate target && target.CalleeOwner.OrDefault() is TypeNode.Fqn owner && owner.Name == origin
            && target.Method.HasValue && names.TryGetValue(target.Method.Value, out var next))
        {
            target.Method = next;
            target.CalleeOwner = new TypeNode.Fqn(destination);
        }
    });
}
