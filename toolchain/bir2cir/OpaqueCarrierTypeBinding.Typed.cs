using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OpaqueCarrierTypeBinding
{
    // The real codec returns a fresh owning occurrence. Only executable
    // structured type slots are rewritten; frozen source facts/carrier bytes
    // remain opaque until their explicitly owned metadata boundary.
    static void BindExecutablePayload(BirGraphValue payload, IReadOnlyDictionary<string, string> physicalBySemantic)
    {
        var carried = new HashSet<string>(StringComparer.Ordinal);
        payload.Visit(node => {
            var name = node switch {
                BirNewClosure { SynthClass.HasValue: true } closure => closure.SynthClass.Value.Name.OrDefault(),
                BirNewSam { SynthClass.HasValue: true } sam => sam.SynthClass.Value.Name.OrDefault(),
                _ => null,
            };
            if (name != null) carried.Add(name);
        });
        var parameters = payload is BirInlinePayload inline
            ? inline.Params.OrEmpty().ToHashSet(ReferenceEqualityComparer.Instance) : null;
        Walk(payload);

        void Walk(BirGraphValue node)
        {
            node.RewriteTypeFieldsInPlace(type => SourceDeclarationSignature.RewriteType(type, classifier =>
                !carried.Contains(classifier.Name) && physicalBySemantic.TryGetValue(classifier.Name, out var physical)
                    ? classifier with { Name = physical } : classifier));
            node.VisitChildren(child => {
                // Preserve the complete root parameter vector: signature,
                // attributes, defaults and all attached source facts. It is the
                // current consumer's semantic overload key, not executable body.
                if (ReferenceEquals(node, payload) && child is BirParameter parameter
                    && parameters?.Contains(parameter) == true) return;
                Walk(child);
            });
        }
    }
}
