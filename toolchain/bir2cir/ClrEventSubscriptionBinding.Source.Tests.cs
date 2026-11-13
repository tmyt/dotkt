using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEventSubscriptionBinding
{
    static void ForwardedOwnersSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var number = new TypeNode.Fqn("kotlin.Int");
        var sourceArguments = new TypeNode[] { new TypeNode.Tv("type", 1) };
        var source = new TypeNode.Fqn("Native.Source", sourceArguments);
        var wrapper = new BirClassDefinition { Kind = "class", Name = "Wrapper",
            TypeParams = new[] { new BirTypeParameter { Name = "A" }, new BirTypeParameter { Name = "B" } }, ClrEventForwarders = new[] {
            new BirClrEventForwarder { Name = "Changed", OwnerType = source },
        } };
        var baseArguments = new TypeNode[] { text, new TypeNode.Tv("type", 0) };
        var derived = new BirClassDefinition { Kind = "class", Name = "Derived", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Base = new TypeNode.Fqn("Wrapper", baseArguments) };
        var closed = new BirClassDefinition { Kind = "class", Name = "Closed", Base = new TypeNode.Fqn("Derived", new TypeNode[] { number }) };
        var own = new BirClassDefinition { Kind = "class", Name = "Own", Base = new TypeNode.Fqn("Derived", new TypeNode[] { text }),
            ClrEventForwarders = new[] { new BirClrEventForwarder { Name = "Changed", OwnerType = new TypeNode.Fqn("Own.Source") } } };
        var roots = new[] {
            new BirFileDeclaration { FileClass = "Definition", Types = new IBirTypeDeclaration[] { wrapper } },
            new BirFileDeclaration { FileClass = "Users", Types = new IBirTypeDeclaration[] { derived, closed, own } },
        };
        var snapshot = CollectForwardedOwners(roots);
        if (snapshot.Count != 4 || !TypeNodeDataEquality.Equal(snapshot[("Wrapper", "Changed")], source)
            || !TypeNodeDataEquality.Equal(snapshot[("Derived", "Changed")], new TypeNode.Fqn("Native.Source", new TypeNode[] { new TypeNode.Tv("type", 0) }))
            || !TypeNodeDataEquality.Equal(snapshot[("Closed", "Changed")], new TypeNode.Fqn("Native.Source", new TypeNode[] { number }))
            || !TypeNodeDataEquality.Equal(snapshot[("Own", "Changed")], new TypeNode.Fqn("Own.Source")))
            throw new InvalidOperationException("Forwarded event inventory lost direct precedence or inherited generic substitution");
        sourceArguments[0] = text;
        baseArguments[1] = text;
        if (snapshot[("Wrapper", "Changed")] is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 1 }] }
            || snapshot[("Derived", "Changed")] is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] })
            throw new InvalidOperationException("Forwarded event snapshot retained mutable source type arguments");
        var handler = new TypeNode.Fqn("System.Action`1", new TypeNode[] { number });
        var eventType = new TypeNode.Fqn("kotlin.clr.ClrEvent", new TypeNode[] { handler });
        var read = new BirEventGet { Name = "Changed", Type = new TypeNode.Fqn("Derived", new TypeNode[] { number }),
            Sty = eventType, Recv = new BirLocal { Name = "receiver" } };
        var consumer = new BirFileDeclaration { FileClass = "Consumer", Methods = new[] { new BirFunctionDeclaration {
            Name = "read", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirReturn { Value = read } },
        } } };
        var references = ReferenceMetadataIndex.Build(Array.Empty<string>());
        Apply(consumer.ToDom(), references, snapshot, out var materialized);
        if (materialized.Count != 1) throw new InvalidOperationException("Event inventory fixture did not reach the live handle binder");
        foreach (var callback in materialized[0]["result"]["args"].AsArray())
            if (!TypeNodeDataEquality.Equal(TypeJson.Read(callback["synthClass"]["body"][0]["expr"]["type"]),
                new TypeNode.Fqn("Native.Source", new TypeNode[] { number })))
                throw new InvalidOperationException("Live event binding disagrees with the inherited forwarding inventory");
        references.DisposeNet();
    }
}
