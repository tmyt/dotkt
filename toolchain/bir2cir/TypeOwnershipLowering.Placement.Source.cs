using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class TypeOwnershipLowering
{
    // Run at the final ownership boundary, after every coroutine and bridge has
    // appended declarations. Earlier source-frame consumers still need the
    // unsplit physical parameter vector and semantic owner facts.
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        var declaredTypes = new HashSet<string>(StringComparer.Ordinal);
        var fileClasses = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new List<IBirTypeDeclaration>();
        var typesByName = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (root.FileClass.OrDefault() is string fileClass) fileClasses.Add(fileClass);
            foreach (var type in root.Types.OrEmpty())
            {
                if (type.Name.OrDefault() is string name)
                {
                    declaredTypes.Add(name);
                    typesByName[name] = type;
                }
                declarations.Add(type);
            }
        }

        foreach (var root in roots)
        {
            if (!root.Methods.HasValue) continue;
            List<BirFunctionDeclaration> remaining = null;
            var methods = root.Methods.Value;
            for (var index = methods.Length - 1; index >= 0; index--)
            {
                var method = methods[index];
                if (method.SemanticOwner.OrDefault() is not string owner) continue;
                method.SemanticOwner = default;
                if (owner == root.FileClass.OrDefault()) continue;
                if (!typesByName.TryGetValue(owner, out var ownerType))
                    throw MissingMethodOwner(method.Name.OrDefault(), owner);
                (remaining ??= methods.ToList()).RemoveAt(index);
                ownerType.Methods = ownerType.Methods.OrEmpty().Append(method).ToArray();
            }
            if (remaining != null) root.Methods = remaining.ToArray();
        }

        foreach (var type in declarations)
        {
            if (type.SemanticOwner.OrDefault() is string owner)
            {
                ValidatePlacementOwner(type.Name.OrDefault(), owner, declaredTypes, fileClasses);
                if (!type.NestedIn.HasValue) type.NestedIn = owner;
                type.SemanticOwner = default;
            }

            // Only class declarations carry this current phase's capture fact;
            // closure ingredients have already become real class declarations.
            if (type is not BirClassDefinition capturedType || !capturedType.OuterTypeParamCount.HasValue) continue;
            var count = capturedType.OuterTypeParamCount.Value;
            capturedType.OuterTypeParamOffset = default;
            capturedType.OuterTypeParamCount = default;
            if (count == 0) continue;
            ValidateCapturedParameters(type.Name.OrDefault(), count,
                type.TypeParams.HasValue ? type.TypeParams.Value.Length : null, type.NestedIn.HasValue);
            var parameters = type.TypeParams.Value;
            var usedNames = parameters.Select(parameter => parameter?.Name.OrDefault())
                .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
            var captured = new List<BirTypeParameter>();
            for (var index = 0; index < count; index++)
            {
                var parameter = parameters[index] == null ? throw MissingCapturedParameter(type.Name.OrDefault(), index)
                    : (BirTypeParameter)parameters[index].DeepClone();
                parameter.Name = CapturedParameterName(index, usedNames);
                captured.Add(parameter);
            }
            type.CapturedTypeParams = captured.ToArray();
            var own = parameters.Skip(count).ToArray();
            type.TypeParams = own.Length == 0 ? default : OptionalField<BirTypeParameter[]>.Of(own);
        }

        // Only direct materialized local functions carry this handoff. Bodies
        // and unrelated records do not participate in its lifetime.
        foreach (var type in declarations)
            foreach (var method in type.Methods.OrEmpty()) method.LexicalOwnerTypeParamCount = default;
    }

    static InvalidOperationException MissingMethodOwner(string method, string owner) => new(
        $"Kotlin method '{method}' has missing semantic owner '{owner}' in this emission unit");

    static void ValidatePlacementOwner(string name, string owner, IReadOnlySet<string> declarations, IReadOnlySet<string> fileClasses)
    {
        name ??= "<unnamed>";
        if (owner == name) throw new InvalidOperationException($"Kotlin type '{name}' cannot own itself");
        if (!declarations.Contains(owner) && !fileClasses.Contains(owner))
            throw new InvalidOperationException($"Kotlin type '{name}' has missing semantic owner '{owner}' in this emission unit");
    }

    static void ValidateCapturedParameters(string name, int count, int? arity, bool hasOwner)
    {
        if (arity == null || count > arity)
            throw new InvalidOperationException($"Kotlin inner type '{name}' declares outerTypeParamCount={count} " +
                $"but has only {arity ?? 0} type parameter declarations");
        if (!hasOwner)
            throw new InvalidOperationException($"Kotlin inner type '{name}' has captured outer parameters but no CLR owner");
    }

    static InvalidOperationException MissingCapturedParameter(string name, int index) => new(
        $"Kotlin inner type '{name}' has a missing captured type parameter at slot {index}");

    // Slot identity and constraints stay intact when Kotlin's shadowed generic
    // names share the flattened CLR namespace. dll2klib drops this prefix using
    // KotlinInnerAttribute, not these physical names.
    static string CapturedParameterName(int index, ISet<string> usedNames)
    {
        var name = $"dotkt$outer{index}";
        while (!usedNames.Add(name)) name += "$";
        return name;
    }
}
