using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class BirGraphFieldOrderTests
{
    internal static void Run()
    {
        var owner = new TypeNode.Fqn("Owner");
        var value = new TypeNode.Fqn("Value");
        var changed = new TypeNode.Fqn("Changed");
        var receiver = new BirLocal { Name = "receiver" };
        var replacement = new BirLocal { Name = "replacement" };
        var donor = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(),
            Body = Array.Empty<IBirStatement>() };
        var arguments = new IBirExpression[] { donor, new BirLocal { Name = "argument" } };
        var call = new BirCallInstance { OwnerType = owner, Recv = receiver,
            Sig = new TypeNode[] { value, value }, TypeArgs = new TypeNode[] { value },
            Ret = value, Args = arguments, Sty = OptionalField<TypeNode>.Null,
            RetSuspendFnTypePre = value, _innerFactoryArguments = true,
            DeclarationId = "old-callee", ShapeTypes = new TypeNode[] { value }, Static = false,
            Overrides = new[] { new BirOverride { Owner = owner, Member = "original" } } };
        var events = new List<string>();
        call.RewriteOwnFieldsInPlace((field, _, type) => {
            events.Add("type:" + field);
            if (field == "sig" && !ReferenceEquals(call.Recv.Value, replacement))
                throw new InvalidOperationException("Joint field traversal moved signature rewriting before the receiver");
            return changed;
        }, (field, _, slot) => {
            events.Add("child:" + field);
            if (field == "recv")
            {
                if (call.OwnerType.Value != changed)
                    throw new InvalidOperationException("Joint field traversal moved the receiver before its owner type");
                slot.Replace(replacement);
            }
            if (field == "args" && call.Ret.Value != changed)
                throw new InvalidOperationException("Joint field traversal moved arguments before the result slot");
        });
        var expected = new[] { "type:ownerType", "child:recv", "type:sig", "type:sig", "type:typeArgs",
            "type:shapeTypes", "type:ret", "child:args", "child:args" };
        if (!events.SequenceEqual(expected) || !ReferenceEquals(call.Args.Value, arguments)
            || !ReferenceEquals(call.Args.Value[0], donor) || !call.Sty.IsNull
            || !ReferenceEquals(call.RetSuspendFnTypePre.Value, value))
            throw new InvalidOperationException("Joint owning field traversal changed order, identity, presence or opaque facts");

        var moved = BirCallConversion.MoveInstanceToStatic(call);
        if (!ReferenceEquals(moved.Args.Value, arguments) || !ReferenceEquals(moved.Args.Value[0], donor)
            || !ReferenceEquals(moved.Sig.Value, call.Sig.Value)
            || !ReferenceEquals(moved.TypeArgs.Value, call.TypeArgs.Value)
            || moved.Ret.Value != changed || !moved.Sty.IsNull || !moved._innerFactoryArguments.Value
            || !ReferenceEquals(moved.RetSuspendFnTypePre.Value, value)
            || moved.DeclarationId.IsPresent || moved.ShapeTypes.IsPresent || moved.OwnerType.IsPresent
            || moved.ToDom().ContainsKey("overrides") || moved.ToDom().ContainsKey("static"))
            throw new InvalidOperationException("Generated call conversion lost common owning facts or retained the consumed target");
        Console.WriteLine("[owning field order] self-test OK (interleaved slots, live writes, vector order, opaque facts and selected-target move)");
    }
}
