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
        BirGraphValue Lower(BirFileDeclaration file)
        {
            var original = Call(file);
            var receiver = original.Recv.Value;
            var arguments = original.Args.Value;
            Apply(file, refs);
            var result = (BirGraphValue)((BirReturn)file.Methods.Value[0].Body.Value[0]).Value.Value;
            var (recv, args, member, ret, sty) = result switch {
                BirConstrainedCall value => (value.Recv.Value, value.Args.Value, value.MemberRef.Value, value.Ret.Value, value.Sty.Value),
                BirNativeInstance value when value.Dispatch.Value == "callvirt"
                    => (value.Recv.Value, value.Args.Value, value.MemberRef.Value, value.Ret.Value, value.Sty.Value),
                _ => throw new InvalidOperationException("Selected external receiver has no explicit physical dispatch"),
            };
            if (!ReferenceEquals(recv, receiver) || !ReferenceEquals(args, arguments)
                || member.Name.Value != "CompareTo"
                || member.ParameterTypes.Value is not [TypeNode.Tv { Scope: "type", I: 0 }]
                || ret != intType || sty != intType)
                throw new InvalidOperationException("Selected external member lost its open declaration, results or actual operands");
            ClrMemberResolution.Apply(file, refs, null);
            var after = (BirGraphValue)((BirReturn)file.Methods.Value[0].Body.Value[0]).Value.Value;
            if (!ReferenceEquals(result, after) || result is BirNativeInstance native &&
                (!ReferenceEquals(native.MemberRef.Value, member) || native.ArgTypes.IsPresent || native.ResolvedMemberParams.IsPresent))
                throw new InvalidOperationException("Native resolution reselected an authored external target");
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
            {
                var result = Lower(copy);
                var selectedOwner = result switch {
                    BirConstrainedCall value when argument == intType => value.Iface.Value,
                    BirNativeInstance value when argument == stringType => value.Type.Value,
                    _ => throw new InvalidOperationException("Reference/value receiver dispatch changed"),
                };
                if (selectedOwner != new TypeNode.Fqn("System.IComparable`1", new[] { argument }))
                    throw new InvalidOperationException("Selected external construction collapsed");
            }
        }
        var generic = new TypeNode.Fqn(typeof(ExternalGeneric<,>).FullName!, new TypeNode[] { stringType, intType });
        var declared = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { new TypeNode.Tv("type", 1) });
        var mapped = (BirNativeInstance)Lower(File(Selected(generic, declared)));
        if (mapped.Type.Value is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "System.Collections.Generic.List`1", Args: [TypeNode.Fqn { Name: "System.Int32" }] }] }
            || mapped.MemberRef.Value.ParameterTypes.Value is not [TypeNode.Tv { Scope: "type", I: 0 }])
            throw new InvalidOperationException("Selected external member used the caller's frame instead of the declaring owner's frame");
        var pending = Selected(stringType, stringType);
        var rejected = false;
        try { BirCallConversion.MoveInstanceToStatic(pending); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || !pending.ExternalMember.HasValue)
            throw new InvalidOperationException("Static-call authoring silently discarded a selected external declaration");
    }
}
