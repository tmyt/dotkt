using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEventSubscriptionBinding
{
    // Class delegation and its users may be in different source files. This snapshot
    // owns the selected CLR event owner independently of subsequent declaration moves.
    public static IReadOnlyDictionary<(string Owner, string Event), TypeNode> CollectForwardedOwners(IReadOnlyList<BirFileDeclaration> roots)
    {
        var definitions = roots.SelectMany(root => root.Types.OrEmpty()).Where(type => type.Name.HasValue)
            .GroupBy(type => type.Name.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var direct = new Dictionary<(string Owner, string Event), TypeNode>();
        var eventNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in definitions.Values)
        {
            if (type is not BirClassDefinition declaration) continue;
            foreach (var forwarder in declaration.ClrEventForwarders.OrEmpty())
            {
                if (!forwarder.Name.HasValue || !forwarder.OwnerType.HasValue) continue;
                direct[(type.Name.Value, forwarder.Name.Value)] = BirGraphValue.CopyType(forwarder.OwnerType.Value);
                eventNames.Add(forwarder.Name.Value);
            }
        }
        var result = new Dictionary<(string Owner, string Event), TypeNode>(direct);
        TypeNode Resolve(string owner, string eventName, HashSet<string> visiting)
        {
            if (result.TryGetValue((owner, eventName), out var found)) return found;
            if (!visiting.Add(owner) || !definitions.TryGetValue(owner, out var definition)
                || definition.Base.OrDefault() is not TypeNode.Fqn baseType || !definitions.ContainsKey(baseType.Name)) return null;
            var inherited = Resolve(baseType.Name, eventName, visiting);
            visiting.Remove(owner);
            if (inherited == null) return null;
            var closed = SubstituteTypeVariables(inherited, baseType.Args);
            // The inherited application must not share mutable type vectors with its source declaration.
            result[(owner, eventName)] = BirGraphValue.CopyType(closed);
            return result[(owner, eventName)];
        }
        foreach (var owner in definitions.Keys)
            foreach (var eventName in eventNames) Resolve(owner, eventName, new HashSet<string>(StringComparer.Ordinal));
        return result;
    }
}
