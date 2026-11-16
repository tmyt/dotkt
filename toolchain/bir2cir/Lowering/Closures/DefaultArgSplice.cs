using System.Threading;

// Cross-module defaults are materialized in the owning typed source preparation,
// before representation selection and executable inline publication. The exact
// selected declaration supplies KotlinDefault payloads or CLR optional constants.
//
// Supplied arguments and omitted values retain their call-evaluation bindings;
// storage and evaluation order remain CallEvalLowering's responsibility. Receiver
// and earlier-parameter tokens read those existing bindings without re-evaluation.
//
// Closed payloads carry their generated helper declarations. Source preparation
// re-homes those helpers and drains them in the consumer file. Capturing closures,
// SAMs and suspend lambdas retain their own synthesis ingredients.
//
// Inline expansion consumes the independently prepared default occurrence after
// representation selection. It clones that value, transfers its semantic owner,
// prebinds its existing frame and substitutes call-value tokens; it does not run
// source preparation or generic-argument substitution for that occurrence again.
static partial class DefaultArgSplice
{
    static int _counter;
    static int FreshDefaultIdentity() => Interlocked.Increment(ref _counter);

    static string DiagnosticPrefix(string context) => context == null ? "bir2cir: " : context + ": bir2cir: ";

    static string UnplannedDefault() =>
        "bir2cir: an omitted cross-module default argument reached the splice outside a call-evaluation " +
        "plan. Its carrier binds this call's own receiver and arguments, so without bindings to read they " +
        "would each be evaluated a second time; the call site must emit an evaluation plan.";

    static string ConstructorDefaultPosition(string owner, int arguments, int parameters) =>
        $"bir2cir: cannot fill an omitted default argument of '{owner}''s constructor — the call emits " +
        $"{arguments} argument(s) for {parameters} parameter(s), so the omitted slot is not identifiable; " +
        "pass the argument explicitly";

    static string AmbiguousDefault(string label, int arity, bool constructorDelegation = false) => constructorDelegation
        ? $"bir2cir: cannot fill an omitted default argument of '{label}' constructor (arity {arity}) — " +
          "that arity is carried by several constructors whose defaults disagree; pass the argument explicitly"
        : $"bir2cir: cannot fill an omitted default argument of '{label}' (arity {arity}) — that name+arity is " +
          "carried by several referenced declarations whose defaults disagree; pass the argument explicitly";

    static string UnrepresentableDefault(string context, string label, int position, string[] names)
    {
        var parameter = names != null && position >= 0 && position < names.Length
            ? $"default of parameter '{names[position]}'" : $"default of parameter at position {position}";
        return DiagnosticPrefix(context) +
            $"cannot fill an omitted default argument for '{label}' ({parameter}) from the selected " +
            "referenced declaration: its optional value could not be obtained or represented. The reference may " +
            "be stale or the value may not be carryable. Pass the argument explicitly.";
    }

    static string MissingDefaultOwner(string method) =>
        $"default argument splice for {method} has no authored semantic use-site owner";

    static string UnavailableDefaultReceiver(string method, int slot, string kind) =>
        $"bir2cir: cannot fill the omitted default argument at slot {slot} of {method}: its default reads the " +
        $"{kind} receiver, but this call shape carries no value for that receiver kind";

}
