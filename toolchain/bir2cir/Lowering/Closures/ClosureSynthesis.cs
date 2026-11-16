using System.Collections.Generic;
using DotKt.Bir;

// A capturing lambda needs a CLR class with capture fields, a field-initializing
// constructor and an invoke method. That representation belongs in bir2cir, not kotc.
// ClosureSynthesis.Source.cs handles the owning file; TypedMaterialized.cs handles
// exact newly authored work items. Both consume synthClass ingredients bottom-up
// and append unique physical types before suspend reconstruction needs them.
// Reference builds synthesize the same classes; RefBodySquash later owns their bodies.
static partial class ClosureSynthesis
{
    // A CAPTURE is unconditionally HEAP storage: it becomes an instance field of the synthesized class. A
    // byref-like (`ref struct`) value therefore cannot be captured — the CLR rejects the class at load time with a
    // TypeLoadException. The refusal is worded by the same oracle the suspend state machine's storage gate uses
    // (FieldLegality is the single one for all three minting sites), and there is no liveness question: unlike an
    // SM spill, a capture is never demotable to a local.
    //
    // But it is RECORDED here and REPORTED later. A class assembled here does not necessarily reach the emitted
    // assembly: the cold suspend lowering reconstructs a `suspendCoroutine { c -> … }` block INLINE and prunes the
    // closure class it came from, substituting each capture back into the enclosing frame — where an ordinary
    // local, byref-like or not, is exactly what it becomes. Refusing at synthesis time would reject
    // `suspendCoroutine { c -> c.resume(span.Length) }`, which emits no closure class at all. So the verdict waits
    // for AssertSurvivingCapturesLegal, run once the passes that can delete a class have run. Keyed by CLASS NAME
    // (unique per compilation), which is also how the pruning identifies its victims — never by predicting who
    // will consume what.
    static readonly Dictionary<string, string> _pendingCaptureRefusals = new(StringComparer.Ordinal);

    // Owning synthesis entries feed the same deferred capture registry.
    internal static void RecordCaptureLegality(string typeName, IEnumerable<(string Name, TypeNode Type)> fields,
        string position, string owner, ReferenceMetadataIndex refs)
    {
        if (refs == null || typeName == null) return;
        foreach (var (name, type) in fields)
        {
            var why = FieldLegality.Classify(type, refs.IsByRefLikeFqn, out var offending);
            if (why == FieldRejection.None) continue;
            _pendingCaptureRefusals[typeName] = FieldLegality.CaptureMessage(
                position, owner, typeName, name, type, offending, why);
            return;
        }
    }
}
