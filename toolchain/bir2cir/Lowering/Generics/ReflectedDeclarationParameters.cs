using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Bounds are source types: taking these from a late CLR generic frame loses e.g. Any?.
sealed class ReflectedDeclarationParameters
{
    internal sealed record Declaration(string Owner, string Identity, BirTypeParameter[] Parameters);
    readonly Dictionary<string, Declaration> methods = new(StringComparer.Ordinal), types = new(StringComparer.Ordinal);
    internal Declaration Method(string id) => id == null ? null : methods.GetValueOrDefault(id);
    internal Declaration Type(string name) => name == null ? null : types.GetValueOrDefault(name);

    internal ReflectedDeclarationParameters(IEnumerable<BirFileDeclaration> roots)
    {
        foreach (var file in roots)
        {
            foreach (var method in file.Methods.OrEmpty()) Add(file.FileClass.OrDefault(), method);
            foreach (var owner in file.Types.OrEmpty())
            {
                if (owner.Generated.OrDefault()) continue; // a captured frame, not a fresh source declaration
                var name = owner.Name.Value;
                if (owner.TypeParams.OrEmpty().Length != 0)
                    types.Add(name, new(name, name, Copy(owner.TypeParams.Value)));
                foreach (var method in owner.Methods.OrEmpty()) Add(name, method);
            }
        }
    }
    void Add(string owner, BirFunctionDeclaration method)
    {
        if (method.Generated.OrDefault() || !method.DeclarationId.HasValue ||
            !method.TypeParams.OrEmpty().Any(parameter => !parameter.Reified.OrDefault())) return;
        var id = method.DeclarationId.Value;
        methods.Add(id, new(owner, id, Copy(method.TypeParams.Value)));
    }
    static BirTypeParameter[] Copy(BirTypeParameter[] parameters) =>
        parameters.Select(p => (BirTypeParameter)p.DeepClone()).ToArray();

    internal sealed record Scope(Declaration Method, NullableRepresentationFrame MethodFrame,
        Declaration Type, NullableRepresentationFrame TypeFrame)
    {
        internal bool IsDeclared(TypeNode.Tv variable)
        {
            var (decl, frame) = variable.Scope == "method" ? (Method, MethodFrame) : (Type, TypeFrame);
            var i = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
            return decl != null && i >= 0 && i < decl.Parameters.Length && !decl.Parameters[i].Reified.OrDefault();
        }

        internal IBirExpression Value(TypeNode.Tv physical, bool nullable, SourceHelperBinder helpers)
        {
            var frame = physical.Scope == "method" ? MethodFrame : TypeFrame;
            var slot = frame?.PhysicalSlot(physical.I);
            var active = new HashSet<(string Scope, int Index)>();
            return Build(new TypeNode.Tv(physical.Scope, slot?.SourceIndex ?? physical.I),
                nullable || slot?.Representation is NullableRepresentationFrame.Role.Nullable or NullableRepresentationFrame.Role.NullableStorage);

            IBirExpression Build(TypeNode.Tv tv, bool marked)
            {
                var decl = tv.Scope == "method" ? Method : Type;
                var p = decl.Parameters[tv.I];
                // Inside a declared bound even a reified R denotes the declaration R,
                // not the argument supplied at this invocation.
                if (!active.Add((tv.Scope, tv.I)))
                    throw new InvalidOperationException($"bir2cir: typeOf: recursive bounds of non-reified type parameter '{p.Name.Value}' are not supported");
                TypeNode[] constraints = p.Constraints.OrEmpty();
                if (constraints.Length == 0) constraints = [new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))];
                var bounds = constraints.Select(bound => ReflectedKotlinType.Value(bound, helpers, Build, TypedCharSequenceIdentity.RewriteType)).ToArray();
                active.Remove((tv.Scope, tv.I));
                return ReflectedKotlinType.Parameter(decl.Owner, decl.Identity, tv.I, p, marked, bounds, helpers);
            }
        }
    }
}
