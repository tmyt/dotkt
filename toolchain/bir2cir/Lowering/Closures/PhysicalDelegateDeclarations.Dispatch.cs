using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class PhysicalDelegateDeclarations
{
    readonly Dictionary<BirFileDeclaration, Dictionary<TypeNode.Fn, CirTypeDeclaration>> dispatchDelegates =
        new(ReferenceEqualityComparer.Instance);

    // A managed reference or native pointer cannot be a generic argument to
    // Func/Action. Keep its signature shape on Invoke; instantiate only the
    // ordinary leaf types. No delegate is invoked to identify the target.
    internal TypeNode.Fqn DispatchDelegate(BirFileDeclaration file, TypeNode[] parameters, TypeNode result)
    {
        var actuals = new List<TypeNode>();
        TypeNode Leaf(TypeNode value)
        {
            var variable = new TypeNode.Tv("type", actuals.Count);
            actuals.Add(value);
            return variable;
        }
        TypeNode Shape(TypeNode value) => value switch {
            TypeNode.ByRef b => new TypeNode.ByRef(Shape(b.Of)),
            TypeNode.Ptr p => new TypeNode.Ptr(Shape(p.Of)),
            TypeNode.Mod m => m with { M = TypedExecutableFrames.RewriteType(m.M, Leaf), Of = Shape(m.Of) },
            TypeNode.FnPtr f => f with { Ret = Shape(f.Ret), Params = f.Params.Select(Shape).ToArray() },
            TypeNode.Fqn { Name: "void" or "System.Void" } => new TypeNode.Fqn("System.Void"),
            _ => Leaf(value),
        };
        var inputs = parameters.Select(Shape).ToArray();
        var signature = new TypeNode.Fn(false, Shape(result), inputs);
        if (!dispatchDelegates.TryGetValue(file, out var declarations)) dispatchDelegates[file] = declarations = new();
        if (!declarations.TryGetValue(signature, out var definition))
        {
            var name = FreshAdapterName(file.FileClass.OrDefault(), "VirtualTarget")
                + (actuals.Count == 0 ? "" : "`" + actuals.Count);
            declarations[signature] = definition = new CirTypeDeclaration {
                Name = name, Kind = CirTypeKind.Delegate, Vis = "internal", Generated = true,
                TypeParams = actuals.Select((_, i) => new CirTypeParameter { Name = "T" + i, SpecialConstraints = new[] { "allowsRefStruct" } }).ToArray(),
                Params = inputs.Select((p,i) => new CirParameter { Name = "p" + i, Type = p }).ToArray(), Ret = signature.Ret,
                Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<CirField>(), Ctors = Array.Empty<CirConstructor>(), Methods = Array.Empty<CirMethod>(),
            };
        }
        return new(definition.Name, actuals.Count != 0 ? actuals.ToArray() : null);
    }

    internal CirFileDeclaration IncludeDispatchDelegates(BirFileDeclaration source, CirFileDeclaration output) =>
        dispatchDelegates.TryGetValue(source, out var declarations)
            ? output with { Types = output.Types.Concat(declarations.Values).ToArray() } : output;
}
