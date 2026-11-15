using System.Buffers;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NetInteropBinding
{
    sealed class ExternalGeneric<A, B> : IComparable<List<B>>
    {
        public int CompareTo(List<B> other) => 0;
    }

    static void SelectedExternalMemberSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var stringType = new TypeNode.Fqn("System.String");
        var intType = new TypeNode.Fqn("System.Int32");
        BirCallInstance Selected(TypeNode.Fqn declarationOwner, TypeNode argument) => new() {
            OwnerType = declarationOwner, Method = "compareTo", Ret = intType, Sty = intType,
            Recv = new BirLocal { Name = "actualReceiver" }, Args = new IBirExpression[] { new BirLocal { Name = "actualArgument" } },
            ExternalMember = new BirExternalMemberBinding { Member = "CompareTo",
                Owner = SourceDeclarationType.Create(new TypeNode.Fqn("System.IComparable`1", new[] { argument })) },
        };
        BirFileDeclaration File(BirCallInstance call) => new() { FileClass = "SelectedExternal",
            Methods = new[] { new BirFunctionDeclaration { Name = "test", Body = new IBirStatement[] { new BirReturn { Value = call } } } } };
        BirCallInstance Call(BirFileDeclaration file) => (BirCallInstance)((BirReturn)file.Methods.Value[0].Body.Value[0]).Value.Value;
        BirConstrainedCall Lower(BirFileDeclaration file)
        {
            var original = Call(file);
            var receiver = original.Recv.Value;
            var arguments = original.Args.Value;
            Apply(file, refs);
            var result = (BirConstrainedCall)((BirReturn)file.Methods.Value[0].Body.Value[0]).Value.Value;
            if (!ReferenceEquals(result.Recv.Value, receiver) || !ReferenceEquals(result.Args.Value, arguments)
                || result.MemberRef.Value.Name.Value != "CompareTo"
                || result.MemberRef.Value.ParameterTypes.Value is not [TypeNode.Tv { Scope: "type", I: 0 }]
                || result.Ret.Value != intType || result.Sty.Value != intType)
                throw new InvalidOperationException("Selected external member lost its open declaration, results or actual operands");
            return result;
        }
        foreach (var argument in new[] { stringType, intType })
        {
            // Both native classes select the SAME generic-definition MethodDef;
            // the selected construction, not an argument-overload guess, differs.
            var file = File(Selected(argument, argument));
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer)) file.WriteJson(writer);
            var json = BirGraphJson.ReadFile(buffer.WrittenSpan.ToArray());
            var binary = BirFileCodec.Decode(BirFileCodec.Encode(file));
            if (Call(json).ExternalMember.Value.Owner.Value.Type != Call(binary).ExternalMember.Value.Owner.Value.Type)
                throw new InvalidOperationException("JSON and MessagePack changed a selected constructed owner");
            foreach (var copy in new[] { file, json, binary })
                if (Lower(copy).Iface.Value != new TypeNode.Fqn("System.IComparable`1", new[] { argument }))
                    throw new InvalidOperationException("Selected external construction collapsed");
        }
        var generic = new TypeNode.Fqn(typeof(ExternalGeneric<,>).FullName!, new TypeNode[] { stringType, intType });
        var declared = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { new TypeNode.Tv("type", 1) });
        var mapped = Lower(File(Selected(generic, declared)));
        if (mapped.Iface.Value is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "System.Collections.Generic.List`1", Args: [TypeNode.Fqn { Name: "System.Int32" }] }] }
            || mapped.RecvType.Value != generic || mapped.CalleeParams.Value is not [TypeNode.Tv { Scope: "type", I: 0 }])
            throw new InvalidOperationException("Selected external member used the caller's frame instead of the declaring owner's frame");
        var pending = Selected(stringType, stringType);
        var rejected = false;
        try { BirCallConversion.MoveInstanceToStatic(pending); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || !pending.ExternalMember.HasValue)
            throw new InvalidOperationException("Static-call authoring silently discarded a selected external declaration");
    }
}
