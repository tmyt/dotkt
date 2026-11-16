using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinHashStorageLowering
{
    internal static void CompleteSourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning hash completion: " + message); }
        var text = new TypeNode.Fqn("kotlin.String");
        var key = new TypeNode.Fqn("Key", new TypeNode[] { text });
        var value = new TypeNode.Fqn("Value");
        var storage = new TypeNode.Fqn("System.Collections.Generic.Dictionary", new TypeNode[] { key, value });
        var receiver = new BirLocal { Name = "receiver" };
        var operand = new BirLocal { Name = "operand" };
        var nested = new BirNativeInstance { Type = storage, Method = "Read", Recv = receiver,
            Args = new IBirExpression[] { operand }, ArgTypes = new TypeNode[] { new TypeNode.Fqn("old") },
            Ret = value, KotlinHashStorageSignature = false };
        var allocation = new BirNewClr { Type = storage, KotlinHashStorageSignature = OptionalField<bool>.Null,
            Args = new IBirExpression[] { nested }, ArgTypes = new TypeNode[] { value } };
        var unmarked = new BirNewClr { Type = storage, Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>() };
        var absentArgs = unmarked.Args.Value;
        var file = new BirFileDeclaration { Fields = new[] { new BirFieldDeclaration { Init = allocation },
            new BirFieldDeclaration { Init = unmarked } } };
        Complete(file);
        Need(!allocation.KotlinHashStorageSignature.IsPresent && !nested.KotlinHashStorageSignature.IsPresent,
            "false/null marker presence was not consumed");
        Need(allocation.Args.Value.Length == 2 && allocation.ArgTypes.Value.Length == 2
            && allocation.Args.Value[1] is BirCallStatic {
                Owner.Value: TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinHashComparerKt" },
                Method.Value: "kotlinHashComparer", Sig.Value.Length: 0, Args.Value.Length: 0 } comparer
            && TypeNodeDataEquality.Equal(comparer.TypeArgs.Value[0], key)
            && TypeNodeDataEquality.Equal(comparer.Ret.Value, allocation.ArgTypes.Value[1]),
            "comparer expression/signature append or final key selection changed");
        Need(nested.ArgTypes.Value.Length == 2 && TypeNodeDataEquality.Equal(nested.ArgTypes.Value[0], key)
            && TypeNodeDataEquality.Equal(nested.ArgTypes.Value[1], value),
            "nested native signature did not close from its current owner before allocation completion");
        Need(ReferenceEquals(allocation.Args.Value[0], nested) && ReferenceEquals(nested.Recv.Value, receiver)
            && ReferenceEquals(nested.Args.Value[0], operand) && ReferenceEquals(unmarked.Args.Value, absentArgs),
            "existing operands were copied, reevaluated or an unmarked construction changed");
        Need(!ReferenceEquals(nested.ArgTypes.Value[0], key), "new native signature shares mutable type argument storage");
        var completedArgs = allocation.Args.Value; var completedSignature = allocation.ArgTypes.Value;
        Complete(file);
        Need(ReferenceEquals(allocation.Args.Value, completedArgs) && ReferenceEquals(allocation.ArgTypes.Value, completedSignature),
            "completion is not idempotent after marker consumption");
        Console.WriteLine("[owning hash signature completion] self-test OK (postorder, presence, comparer, operand identity and idempotence)");
    }
}
