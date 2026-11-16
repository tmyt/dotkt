using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Realize faithful Char.code and Function/KFunction.invoke operations before
// native/member/type binding. Both choices consume authored operation facts;
// reference lookup and overload selection belong to the later owning passes.
static partial class CharCodeInvokeLowering
{
    public static BirFileDeclaration Apply(BirFileDeclaration root)
    {
        Walk(root);
        return root;
    }

    static BirGraphValue Walk(BirGraphValue node)
    {
        if (node == null || node is IBirExecutablePayload) return node;
        node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
        return node switch {
            BirCallStatic call => LowerCharCode(call),
            BirCallInstance call => LowerInvoke(call),
            _ => node,
        };
    }

    static BirGraphValue LowerCharCode(BirCallStatic call)
    {
        // Presence is semantic here: owner:null is the top-level operation.
        // Even an explicitly null ownerType field is a different input shape.
        if (call.OwnerType.IsPresent || !call.Owner.IsNull
            || call.Method.OrDefault() != "code" || call.Prop.OrDefault() != "get"
            || call.Args.OrDefault() is not { Length: 1 } args
            || call.Sig.OrDefault() is not { Length: > 0 } signature
            || signature[0] is not TypeNode.Fqn receiver
            || ReferenceMetadataIndex.BareOwnerFqn(receiver.Name) != "kotlin.Char") return call;
        var target = call.Ret.OrDefault() is TypeNode.Fqn result
            && ReferenceMetadataIndex.BareOwnerFqn(result.Name) == "kotlin.Int"
                ? result : new TypeNode.Fqn("kotlin.Int");
        return new BirConvert { To = target, E = OptionalField<IBirExpression>.Of(args[0]) };
    }

    static BirGraphValue LowerInvoke(BirCallInstance call)
    {
        if (call.Method.OrDefault() != "invoke" || !call.Recv.HasValue
            || call.OwnerType.OrDefault() is not TypeNode.Fqn owner) return call;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(owner.Name);
        if (!(bare.StartsWith("kotlin.Function", StringComparison.Ordinal)
            || bare.StartsWith("kotlin.reflect.KFunction", StringComparison.Ordinal))
            || owner.Args is not { Length: > 0 } arguments) return call;
        // Use the selected owner's exact instantiated argument vector. No
        // arity is reconstructed from the classifier spelling or call operands.
        var arity = arguments.Length - 1;
        return new BirDelegateInvoke {
            FuncType = new TypeNode.Fn(false, arguments[arity], arguments.Take(arity).ToArray()),
            Recv = call.Recv, Args = call.Args.OrEmpty(),
        };
    }
}
