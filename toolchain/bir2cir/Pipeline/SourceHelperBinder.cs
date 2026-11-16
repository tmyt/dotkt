using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Only declaration facts cross this boundary. Helper selection and argument
// canonicalization are shared by owning source producers and remaining DOM
// consumers; executable arguments never change representation for binding.
sealed class SourceHelperBinder
{
    sealed record Declaration(string Owner, string Name, bool Static, int Arity,
        TypeNode[] Parameters, string Identity);
    readonly Declaration[] declarations;
    readonly ReferenceMetadataIndex references;

    internal SourceHelperBinder(IEnumerable<BirFileDeclaration> roots, ReferenceMetadataIndex references)
        : this(roots.SelectMany(root => root.Methods.OrEmpty().Select(method => new Declaration(
            root.FileClass.OrDefault(), method.Name.OrDefault(), method.Static.OrDefault(),
            method.TypeParams.OrEmpty().Length, method.Params.HasValue
                ? method.Params.Value.Select(parameter => parameter.Type.OrDefault()).ToArray() : null,
            method.DeclarationId.OrDefault()))), references) { }

    SourceHelperBinder(IEnumerable<Declaration> declarations, ReferenceMetadataIndex references)
    {
        this.declarations = declarations.ToArray();
        this.references = references;
    }

    internal string Select(string owner, string name, int arity, TypeNode[] parameters)
    {
        var matches = declarations.Where(method => method.Owner == owner && method.Name == name
            && method.Static && method.Arity == arity && method.Parameters is { } declared
            && declared.Length == parameters.Length
            && declared.Select((parameter, index) =>
                ReferenceMetadataIndex.SourceDeclarationDescribesCall(parameter, parameters[index])).All(match => match)).ToArray();
        if (matches.Length == 1)
            return matches[0].Identity ?? throw new InvalidOperationException("Collection helper has no declaration identity");
        if (matches.Length > 1)
            throw new InvalidOperationException("Ambiguous source collection helper declaration: " + owner + "." + name);
        return references?.AuthoredKotlinHelper(owner, name, arity, parameters).DeclarationId
            ?? throw new InvalidOperationException("Missing source collection helper declaration: " + owner + "." + name);
    }

    internal static TypeNode SourceArgument(TypeNode argument) => argument switch {
        TypeNode.Projection projection => SourceArgument(projection.Of),
        TypeNode.Star => new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
        _ => argument,
    };

    internal BirCallStatic Bind(BirCallStatic call) => call with {
        DeclarationId = Select(((TypeNode.Fqn)call.Owner.Value).Name, call.Method.Value,
            call.TypeArgs.HasValue ? call.TypeArgs.Value.Length : 0, call.Sig.Value),
        TypeArgs = call.TypeArgs.HasValue ? call.TypeArgs.Value.Select(SourceArgument).ToArray() : call.TypeArgs,
    };
}
