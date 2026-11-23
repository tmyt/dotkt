using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static void ApplyNamedSource(SuspendModuleContext module)
    {
        // These two dictionaries are the existing physical declaration handoff,
        // extended at the same clock that allocates the cold declaration ID.
        foreach (var entry in module.NamedEntries)
        {
            if (!entry.Method.DeclarationId.HasValue) continue;
            var identity = entry.Method.DeclarationId.Value;
            if (module.PhysicalParameterIndices?.TryGetValue(identity, out var positions) == true)
                module.PhysicalParameterIndices[identity + "|cold"] = positions;
            if (module.PhysicalDeclarationSources?.TryGetValue(identity, out var source) == true)
                module.PhysicalDeclarationSources[identity + "|cold"] = source + "|cold";
        }
        module.PrepareStage0();
        if (module.NamedEntries.Count == 0) return;
        if (module.AppBuild && !module.ContinuationBaseIsLocal
            && (module.References.ResolveNetType(TaskBclFqn, 0) == null
                || module.References.ResolveNetType(TaskBclFqn + "`1", 1) == null
                || module.References.ResolveNetType(TaskCompletionSourceBclFqn + "`1", 1) == null))
            throw new NotSupportedException("bir2cir: suspend-lowering: the compile-reference set does not provide "
                + "System.Threading.Tasks.Task, Task<T>, and TaskCompletionSource<T>; the public suspend ABI cannot be emitted.");

        var suffixes = new Dictionary<BirFunctionDeclaration, string>(ReferenceEqualityComparer.Instance);
        foreach (var group in module.NamedEntries.GroupBy(entry =>
            (Owner: entry.Owner?.Name.OrDefault(), FileClass: entry.File.FileClass.OrDefault() ?? "Kt", Name: entry.Method.Name.Value)))
        {
            var entries = group.ToArray();
            if (entries.Length == 1) { suffixes[entries[0].Method] = ""; continue; }
            var candidates = entries.Select(entry => SourceParamSimpleNames(entry.Method)).ToArray();
            var unique = candidates.All(name => name.Length > 0)
                && candidates.Distinct(StringComparer.Ordinal).Count() == candidates.Length;
            for (var index = 0; index < entries.Length; index++)
                suffixes[entries[index].Method] = "_" + (unique ? candidates[index] : "ov" + index);
        }

        BirFileDeclaration rootHost = null;
        (BirFileDeclaration Host, TypeNode Result)? textRoot = null;
        foreach (var entry in module.NamedEntries)
        {
            var method = entry.Method;
            var staticMember = entry.Owner != null && method.Static.OrDefault();
            var ownerParameters = entry.Owner?.TypeParams.OrEmpty() ?? Array.Empty<BirTypeParameter>();
            var ownerCount = !staticMember ? ownerParameters.Length : method.LexicalOwnerTypeParamCount.OrDefault();
            if (ownerCount > ownerParameters.Length)
                throw new InvalidOperationException($"suspend member '{entry.Owner?.Name.OrDefault()}.{method.Name.OrDefault()}' "
                    + $"claims {ownerCount} lexical owner slots but its owner declares only {ownerParameters.Length}");
            var frame = ownerParameters.Take(ownerCount).Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            method.LexicalOwnerTypeParamCount = default;
            var generator = new SourceFunGen(module, new SourceFunctionInput.Named(entry.File, entry.Owner,
                method, suffixes[method], frame));
            var output = generator.Build();
            foreach (var generated in output.Methods) MaterializedExecutable.NormalizeSource(generated);
            foreach (var generated in output.Types) MaterializedExecutable.NormalizeSource((BirGraphValue)generated);
            if (generator.ProjectedRootResult is { } projected) textRoot ??= (entry.File, projected);
            else if (output.NeedsRootContinuation) rootHost ??= entry.File;
            var current = entry.Owner?.Methods.OrEmpty() ?? entry.File.Methods.OrEmpty();
            if (!module.ContinuationBaseIsLocal)
            {
                module.Transitions.RetireMethod(entry.File, entry.Owner, method);
                current = current.Where(candidate => !ReferenceEquals(candidate, method)).ToArray();
            }
            var methods = current.Concat(output.Methods).ToArray();
            if (entry.Owner != null) entry.Owner.Methods = methods;
            else entry.File.Methods = methods;
            foreach (var projection in output.MethodProjections)
            {
                if (!ReferenceEquals(projection.Origin, method)
                    || output.Methods.Count(actual => ReferenceEquals(actual, projection.Projection)) != 1)
                    throw new InvalidOperationException("Suspend method projection does not name an actual emitted declaration");
                module.Transitions.ProjectMethod(projection.Origin, projection.Projection);
            }
            if (output.Methods.Count != 0) module.Transitions.PublishMethods(entry.File, entry.Owner, output.Methods);
            if (output.Types.Count != 0)
            {
                entry.File.Types = entry.File.Types.OrEmpty().Concat(output.Types).ToArray();
                module.Transitions.PublishTypes(entry.File, output.Types);
            }
        }
        void PublishRoot(BirFileDeclaration host, TypeNode projected)
        {
            var sink = MaterializedExecutable.NormalizeSource(BuildRootContinuationSource(TaskCompletionSourceBclFqn, projected));
            host.Types = host.Types.OrEmpty().Append(sink).ToArray();
            module.Transitions.PublishTypes(host, new IBirTypeDeclaration[] { sink });
        }
        if (rootHost != null) PublishRoot(rootHost, null);
        if (textRoot is { } restore) PublishRoot(restore.Host, restore.Result);
        if (!module.ContinuationBaseIsLocal && module.ConsumedIntrinsicClosures.Count > 0)
        {
            // The selected intrinsic records give the producer's exact consumed
            // names. Preserve the existing removal policy, reporting the actual
            // removed vector rather than reconstructing declaration identities.
            var names = module.ConsumedIntrinsicClosures.Select(type => type.Name.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var file in module.Roots)
            {
                var removed = file.Types.OrEmpty().Where(type => type.Name.HasValue && names.Contains(type.Name.Value)).ToArray();
                if (removed.Length == 0) continue;
                module.Transitions.RetireTypes(file, removed);
                file.Types = file.Types.OrEmpty().Where(type => !removed.Any(candidate => ReferenceEquals(candidate, type))).ToArray();
            }
        }
    }

    static string SourceParamSimpleNames(BirFunctionDeclaration method) => string.Concat(method.Params.OrEmpty().Select(parameter => {
        var name = (parameter.Type.OrDefault() as TypeNode.Fqn)?.Name ?? "";
        var at = name.LastIndexOf('.');
        if (at >= 0) name = name[(at + 1)..];
        return new string(name.Where(char.IsLetterOrDigit).ToArray());
    }));
}
