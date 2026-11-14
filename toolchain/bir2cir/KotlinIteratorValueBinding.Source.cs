using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class KotlinIteratorValueBinding
{
    internal TypeNode.Tv Element(BirGraphValue node)
    {
        if (node is not BirCallInstance call) return null;
        var element = CandidateElement(true, call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault(),
            call.Args.HasValue ? call.Args.Value.Length : null, call.TypeArgs.OrEmpty().Length,
            call.Sty.OrDefault() ?? call.DynRet.OrDefault());
        if (element == null) return null;
        return Operation(call) == null ? null : element;
    }

    static string Operation(BirCallInstance call) => SelectOperation(call.DeclarationId.OrDefault(),
        (call.OwnerType.OrDefault() as TypeNode.Fqn)?.Name, call.Method.OrDefault(), call.Overrides.OrEmpty()
            .Select(edge => new ProtocolOverride(edge.DeclarationId.OrDefault(),
                (edge.Owner.OrDefault() as TypeNode.Fqn)?.Name, edge.Member.OrDefault(),
                edge.Arity.HasValue ? edge.Arity.Value : null)));

    internal BirValueBlock Bind(BirCallInstance read)
    {
        var previous = Operation(read) == "previous";
        var capability = new TypeNode.Fqn("DotKt.Runtime.CompilerServices."
            + (previous ? "KotlinListIteratorValueSlot" : "KotlinIteratorValueSlot"));
        var name = CallEvalLowering.FreshBindingId();
        BirLocal Local() => new() { Name = name };
        var receiver = read.Recv.HasValue ? read.Recv.Value
            : throw new InvalidOperationException("Iterator value read has no receiver");
        // The caller's flow plan still owns nested reads in this exact receiver.
        // Move it into one binding, never clone it into both conditional arms.
        read.Recv = default;
        var direct = (BirCallInstance)read.DeepClone();
        direct.Recv = Local();
        direct.Sty = ObjectType;
        return new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = name, Type = ObjectType,
                Init = OptionalField<IBirExpression>.Of(receiver) } },
            Result = new BirConditional {
                Type = ObjectType, Cond = new BirIsInstance { Type = BirGraphValue.CopyType(capability), E = Local() },
                Then = new BirCallInstance { OwnerType = capability, Virtual = true,
                    Method = previous ? "dotktPreviousValue" : "dotktNextValue", Recv = Local(),
                    Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(),
                    Ret = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) },
                Else = direct,
            },
            Type = ObjectType, Sty = ObjectType,
        };
    }
}
