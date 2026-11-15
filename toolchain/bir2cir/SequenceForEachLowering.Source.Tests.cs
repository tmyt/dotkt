using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SequenceForEachLowering
{
    internal static void SourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Source sequence dispatch: " + message); }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        var integer = new TypeNode.Fqn("kotlin.Int");
        var source = new BirCallStatic { Owner = new TypeNode.Fqn("SequenceUses"), Method = "source",
            Ret = new TypeNode.Fqn("ordinary.Owner"), DynRet = new TypeNode.Fqn("kotlin.sequences.Sequence", new TypeNode[] { integer }),
            Args = Array.Empty<IBirExpression>() };
        var nestedSource = new BirLocal { Name = "nestedSource" };
        var nestedLoop = new BirForEach { Elem = integer, Var = "nested", Src = E(nestedSource),
            Body = Array.Empty<IBirStatement>(), ErasedEnumeration = true };
        var declaration = new BirFunctionDeclaration { Name = "inside", DeclarationId = "inside-sequence",
            Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
        var localFunction = new BirLocalFunction { Id = "inside-sequence", Decl = declaration };
        var loop = new BirForEach { Elem = integer, Var = "item", Src = E(source), Label = "outer",
            Body = new IBirStatement[] { localFunction, nestedLoop } };
        var nativeSource = new BirNewArray { Elem = integer, Elems = Array.Empty<IBirExpression>() };
        var native = new BirForEach { Elem = integer, Var = "exact", Src = E(nativeSource),
            Body = Array.Empty<IBirStatement>(), ErasedEnumeration = false };
        var body = new IBirStatement[] { loop, native };
        var method = new BirFunctionDeclaration { Name = "iterate", Body = body, InlineBir = new byte[] { 0x80 } };
        var file = new BirFileDeclaration { Methods = new[] { method } };
        var before = _tmp;
        Apply(file);
        Need(body[0] is BirBlock { Body.Value: [BirVariable iterator, BirWhile repeat] }
            && iterator.Name.Value == "$seqIt$" + (before + 1)
            && iterator.Init.Value is BirNativeInstance { Method.Value: "GetEnumerator", Recv.Value: BirCast sourceCast }
            && ReferenceEquals(sourceCast.E.Value, source) && repeat.Label.Value == "outer"
            && repeat.Body.Value[0] is BirVariable { Init.Value: BirCast { Type.Value: TypeNode.Fqn { Name: "kotlin.Int" } } }
            && ReferenceEquals(repeat.Body.Value[1], localFunction) && ReferenceEquals(localFunction.Decl.Value, declaration)
            && repeat.Body.Value[2] is BirBlock { Body.Value: [BirVariable nestedIterator, BirWhile] }
            && nestedIterator.Name.Value == "$seqIt$" + (before + 2),
            "outer-first counter, exact conversion, source/body MOVE or loop label changed");
        Need(!loop.Src.IsPresent && !loop.Body.IsPresent && !nestedLoop.Src.IsPresent && !nestedLoop.Body.IsPresent
            && ReferenceEquals(body[1], native) && ReferenceEquals(native.Src.Value, nativeSource)
            && !native.ErasedEnumeration.IsPresent && _tmp == before + 2,
            "old owners retained moved forests or a native typed enumerable changed path");
        var once = file.DeepClone(); Apply(file);
        Need(file.StructurallyEquals(once) && _tmp == before + 2, "completed iteration was rewritten twice");
        Need(IsErasedCast(new TypeNode.Fqn("System.Collections.IEnumerable"))
            && IsErasedCast(new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { new TypeNode.Star() }))
            && !IsSequenceTyped(new BirLocal { Name = "stampOnly", Sty = new TypeNode.Fqn("kotlin.sequences.Sequence") }),
            "erased cast classification changed or sty became a sequence inference");
        Console.WriteLine("[source sequence dispatch] self-test OK (actual source/body MOVE, counter/label order, dynamic sequence fact, native path and idempotence)");
    }
}
