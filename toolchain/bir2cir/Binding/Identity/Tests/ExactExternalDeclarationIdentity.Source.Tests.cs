using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExactExternalDeclarationIdentity
{
    internal static void SourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning external declaration edges: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var element = new TypeNode.Tv("type", 0);
        var sourceEdge = new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { element });
        var exactEdge = new TypeNode.Fqn(typeof(IEnumerable<>).FullName, new TypeNode[] { element });
        var current = new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { element });
        var exactBase = new TypeNode.Fqn(typeof(List<>).FullName, new TypeNode[] { element });
        var descriptor = new BirMethodImpl { Owner = sourceEdge, Member = "GetEnumerator", Params = Array.Empty<TypeNode>(),
            Ret = new TypeNode.Fqn("System.Object") };
        var baseDescriptor = new BirMethodImpl { Owner = current, Member = "ToString", Params = Array.Empty<TypeNode>(),
            Ret = new TypeNode.Fqn("System.String") };
        var unrelated = new BirCallInstance { OwnerType = sourceEdge, Method = "Use", Sig = Array.Empty<TypeNode>(),
            Args = Array.Empty<IBirExpression>(), Recv = OptionalField<IBirExpression>.Of(new BirThis()) };
        var method = new BirFunctionDeclaration { Name = "Bridge", Ret = sourceEdge, Params = Array.Empty<BirParameter>(),
            ClrInterfaceImpls = new[] { descriptor }, ClrBaseImpls = new[] { baseDescriptor },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(unrelated) } } };
        var owner = new BirClassDefinition { Kind = "class", Name = "Local", Base = current,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Interfaces = new TypeNode[] { sourceEdge }, Methods = new[] { method } };
        var iface = new BirInterfaceDefinition { Kind = "interface", Name = "Interface", Interfaces = new TypeNode[] { sourceEdge } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner, iface } };
        var types = file.Types.Value; var interfaces = owner.Interfaces.Value; var methods = owner.Methods.Value;
        Apply(file, refs);
        Need(owner.Base.Value == exactBase && owner.Interfaces.Value.Single() == exactEdge
            && iface.Interfaces.Value.Single() == exactEdge && descriptor.Owner.Value == exactEdge
            && baseDescriptor.Owner.Value == exactBase,
            "current base/interface/attached MethodImpl owners did not use the exact reflected definition");
        Need(ReferenceEquals(file.Types.Value, types) && ReferenceEquals(owner.Interfaces.Value, interfaces)
            && ReferenceEquals(owner.Methods.Value, methods) && ReferenceEquals(method.ClrInterfaceImpls.Value[0], descriptor)
            && ReferenceEquals(method.ClrBaseImpls.Value[0], baseDescriptor)
            && ReferenceEquals(unrelated.OwnerType.Value, sourceEdge) && ReferenceEquals(method.Ret.Value, sourceEdge),
            "physical edge normalization replaced declarations or rewrote unrelated executable/signature facts");
        var stable = owner.Base.Value;
        Apply(file, refs);
        Need(ReferenceEquals(owner.Base.Value, stable), "exact reflected owner normalization is not stable");
        Console.WriteLine("[owning exact external edges] current edges and attached descriptors only; exact identities and record ownership: OK");
    }
}
