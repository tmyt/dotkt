using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    // Freeze actual declarations before representation erasure, including the
    // declarations added by inline and closure synthesis since the early pass.
    internal static void FreezeSuspendResults(IEnumerable<BirFileDeclaration> roots)
    {
        static void Freeze(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
            {
                if (method.SuspendResult.IsPresent || method.Mods.OrDefault()?.Suspend.OrDefault() != true) continue;
                var logical = method.SuspendRet.OrDefault()
                    ?? throw new InvalidOperationException(
                        $"bir2cir: suspend declaration '{method.Name.OrDefault()}' carries no `suspendRet` slot");
                method.SuspendResult = BirGraphValue.CopyType(logical);
            }
        }
        foreach (var root in roots)
        {
            Freeze(root.Methods.OrEmpty());
            foreach (var type in root.Types.OrEmpty())
                Freeze(type switch {
                    BirClassDefinition value => value.Methods.OrEmpty(),
                    BirInterfaceDefinition value => value.Methods.OrEmpty(),
                    BirEnumDefinition value => value.Methods.OrEmpty(),
                    _ => throw new InvalidOperationException("Unaccounted suspend declaration owner"),
                });
        }
    }

    internal static void SourceSuspendResultSelfTest()
    {
        var arguments = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 0)) };
        var source = new BirFunctionDeclaration {
            Name = "source", Mods = new BirModifiers { Suspend = true },
            SuspendRet = new TypeNode.Fqn("kotlin.collections.List", arguments),
        };
        var preserved = source with { SuspendResult = OptionalField<TypeNode>.Null };
        var root = new BirFileDeclaration {
            Methods = new[] { source },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition { Name = "probe.Owner", Methods = new[] { preserved } } },
        };
        FreezeSuspendResults(new[] { root });
        var logical = (TypeNode.Fqn)source.SuspendResult.Value;
        arguments[0] = new TypeNode.Fqn("kotlin.String");
        if (!preserved.SuspendResult.IsNull || ReferenceEquals(logical.Args, arguments)
            || logical.Args![0] is not TypeNode.Nullable { Of: TypeNode.Tv { Scope: "method", I: 0 } })
            throw new InvalidOperationException("Source suspend result lost snapshot independence or explicit-null presence");
        FreezeSuspendResults(new[] { root });
        if (!ReferenceEquals(logical, source.SuspendResult.Value))
            throw new InvalidOperationException("Source suspend result was frozen twice");
        Console.WriteLine("[source suspend result] self-test OK (flat declarations, exact logical frame, presence, independent snapshot)");
    }
}
