using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableGenericOverloadCollision
{
    // Transient scalar rows describe this exact actual-graph checkpoint, never
    // the runtime collision shadow or an earlier declaration catalog.
    sealed record CollisionParameter(TypeNode Physical, TypeNode Pre, bool Nullable);
    sealed record CollisionCallable(string Name, string Identity, List<string> TypeParameters, CollisionParameter[] Parameters);

    internal static void Check(BirFileDeclaration root, string file)
    {
        CheckRows(root.Methods.OrEmpty().Select(Method).ToArray(), root.FileClass.OrDefault() ?? "<file>", new(), file, constructors: false);
        foreach (var type in root.Types.OrEmpty())
        {
            var owner = type.Name.OrDefault() ?? "<type>";
            var parameters = type.TypeParams.OrEmpty().Select(parameter => parameter.Name.OrDefault()).ToList();
            CheckRows(type.Methods.OrEmpty().Select(Method).ToArray(), owner, parameters, file, constructors: false);
            CheckRows(type.Ctors.OrEmpty().Select(constructor => new CollisionCallable(null, constructor.DeclarationId.OrDefault(),
                new(), constructor.Params.OrEmpty().Select(Parameter).ToArray())).ToArray(), owner, parameters, file, constructors: true);
        }
    }

    static CollisionCallable Method(BirFunctionDeclaration method) => new(method.Name.OrDefault(), method.DeclarationId.OrDefault(),
        method.TypeParams.OrEmpty().Select(parameter => parameter.Name.OrDefault()).ToList(), method.Params.OrEmpty().Select(Parameter).ToArray());
    static CollisionParameter Parameter(BirParameter parameter) => new(parameter.Type.OrDefault(), parameter.NullableGeneric.OrDefault(),
        parameter.NullableFlags.HasValue && parameter.NullableFlags.Value.Length > 0 && parameter.NullableFlags.Value[0] == 2);

    static void CheckRows(IReadOnlyList<CollisionCallable> declarations, string owner, List<string> ownerParameters, string file, bool constructors)
    {
        var seen = new Dictionary<string, CollisionCallable>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (!constructors && declaration.Name == null) continue;
            var erased = ErasedVector(declaration);
            var key = constructors ? erased : declaration.Name + "|" + declaration.TypeParameters.Count + "|" + erased;
            if (seen.TryGetValue(key, out var prior))
            {
                if (SourceKey(prior) == SourceKey(declaration)) continue;
                var distinct = constructors
                    ? prior.Identity != null && declaration.Identity != null && prior.Identity != declaration.Identity
                    : !string.IsNullOrEmpty(prior.Identity) && !string.IsNullOrEmpty(declaration.Identity)
                        && !StringComparer.Ordinal.Equals(prior.Identity, declaration.Identity);
                if (distinct) { seen[key] = declaration; continue; }
                if (constructors)
                    throw new InvalidOperationException(
                        $"bir2cir: {file}: {owner}: two constructors erase to one CLR signature. "
                        + $"A nullable generic argument is emitted as System.Object, so "
                        + $"'{owner}({SourceVector(prior, ownerParameters)})' and '{owner}({SourceVector(declaration, ownerParameters)})' "
                        + $"both become '.ctor({key})' and only one can be called. "
                        + "Give one of them a different parameter count, or route it through a named factory function.");
                throw new InvalidOperationException(
                    $"bir2cir: {file}: {owner}: two declarations of '{declaration.Name}' erase to one CLR signature. "
                    + $"A nullable generic 'T?' is emitted as System.Object, so '{Render(prior, ownerParameters)}' "
                    + $"and '{Render(declaration, ownerParameters)}' both become '{declaration.Name}({erased})' and only one can be "
                    + "called. Give one of them a different name or a different parameter count.");
            }
            seen[key] = declaration;
        }
    }

    static string ErasedVector(CollisionCallable declaration) => string.Join(", ", declaration.Parameters
        .Select(parameter => parameter.Physical is { } type ? Normalize(Render(type, NoTps, source: false)) : "?"));
    // Differential keys use only structural scope/index, never declared type
    // variable names or ordinary reference-nullability annotations.
    static string SourceKey(CollisionCallable declaration) => string.Join(", ", declaration.Parameters.Select(parameter => SourceType(parameter, NoTps, nrt: false)));
    static string SourceVector(CollisionCallable declaration, List<string> ownerParameters) => string.Join(", ", declaration.Parameters
        .Select(parameter => SourceType(parameter, Scopes(ownerParameters, declaration.TypeParameters), nrt: true)));
    static string SourceType(CollisionParameter parameter, (List<string> type, List<string> method) parameters, bool nrt)
    {
        // The retained nullable-generic source is the one authoritative
        // pre-erasure channel. Outer NRT is restored for diagnostics only,
        // never to pretend this erasure caused an older reference-only collapse.
        if (parameter.Pre != null) return Render(parameter.Pre, parameters, source: true);
        var type = parameter.Physical;
        if (type == null) return "?";
        if (nrt && type is not TypeNode.Nullable && parameter.Nullable) type = new TypeNode.Nullable(type);
        return Render(type, parameters, source: true);
    }
    static string Render(CollisionCallable declaration, List<string> ownerParameters) => declaration.Name
        + (declaration.TypeParameters.Count > 0 ? "<" + string.Join(", ", declaration.TypeParameters) + ">" : "")
        + "(" + SourceVector(declaration, ownerParameters) + ")";
}
