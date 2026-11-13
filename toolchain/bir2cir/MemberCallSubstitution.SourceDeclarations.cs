using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    public static IReadOnlyDictionary<string, LocalFactory> CollectLocalFactories(IReadOnlyList<BirFileDeclaration> roots)
    {
        var result = new Dictionary<string, LocalFactory>(StringComparer.Ordinal);
        void Collect(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
            {
                if (method.DeclarationId.OrDefault() is not string id || !method.Attrs.HasValue) continue;
                foreach (var attribute in method.Attrs.Value)
                {
                    var marker = (attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name;
                    if (marker is not ("kotlin.clr.ClrCollectionFactory" or "kotlin.clr.ClrArrayFactory")) continue;
                    var argument = attribute.Args.OrEmpty().FirstOrDefault();
                    var literal = argument switch { BirConstant value => value.Value.OrDefault(),
                        BirAttributeLiteral value => value.Value.OrDefault(), _ => null };
                    var kind = (literal as BirLiteral.TextLiteral)?.Value;
                    var varargs = method.Params.Value.Select((parameter, index) => (parameter, index))
                        .Where(pair => pair.parameter.Mods.OrDefault()?.Vararg.OrDefault() == true).Select(pair => pair.index).ToArray();
                    var isArray = marker == "kotlin.clr.ClrArrayFactory";
                    var elementHint = ArrayConstructionLowering.ArrayElementOf(method.Ret.OrDefault()) is TypeNode.Fqn element ? element.Name : null;
                    // Frames are immutable declaration-owned maps; their constructor copies and wraps all vectors.
                    result.Add(id, new LocalFactory(isArray ? null : kind, isArray ? kind : null,
                        elementHint, varargs, method.NullableRepresentationFrame.OrDefault()));
                }
            }
        }
        foreach (var root in roots)
        {
            Collect(root.Methods.OrEmpty());
            foreach (var type in root.Types.OrEmpty()) Collect(type.Methods.OrEmpty());
        }
        return result;
    }

    sealed class LocalPropertyCollector
    {
        readonly Dictionary<LocalPropertyAccessorKey, List<LocalPropertyAccessor>> candidates = new();

        internal static bool IsOwner(string name, bool hasKind) => name != null && hasKind;

        internal void Add(string owner, bool physicalBridge, string property, string kind, string physicalName,
            int methodArity, int parameterCount, Func<TypeNode[]> readParameters)
        {
            // Preserve the declaration validation order: a bridge is not a Kotlin candidate at all.
            if (physicalBridge || !KotlinPropertyAccessors.HasIdentity(property, kind)) return;
            if (physicalName == null) return;
            var parameterTypes = readParameters();
            if (parameterTypes == null || parameterTypes.Length != parameterCount
                || parameterTypes.Any(type => type == null)) return;
            var key = new LocalPropertyAccessorKey(owner, property, kind, methodArity, parameterCount);
            if (!candidates.TryGetValue(key, out var values)) candidates[key] = values = new();
            // Placement and later representation mutate live declaration graphs; snapshots own their type vectors.
            values.Add(new LocalPropertyAccessor(physicalName, parameterTypes.Select(BirGraphValue.CopyType).ToArray()));
        }

        internal IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> Finish()
            => candidates.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LocalPropertyAccessor>)pair.Value);
    }

    public static IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>
        CollectLocalPropertyAccessors(IReadOnlyList<BirFileDeclaration> roots)
    {
        var collector = new LocalPropertyCollector();
        void Walk(BirGraphValue node)
        {
            if (node is IBirTypeDeclaration type && LocalPropertyCollector.IsOwner(type.Name.OrDefault(), type.Kind.HasValue))
                foreach (var method in type.Methods.OrEmpty())
                    collector.Add(type.Name.Value, method.PhysicalSlotBridge.OrDefault(), method.PropertyName.OrDefault(),
                        method.PropertyAccessor.OrDefault(), method.Name.OrDefault(), method.TypeParams.OrEmpty().Length,
                        method.Params.OrEmpty().Length, () => method.Params.HasValue ? method.Params.Value.Select(parameter => parameter.Type.OrDefault()).ToArray() : null);
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
        return collector.Finish();
    }
}
