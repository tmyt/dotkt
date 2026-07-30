using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// GitHub #10 — the .NET AWAITABLE PATTERN, resolved from reference metadata (the bir2cir layer owns all CLR-metadata
// reading). A type is awaitable IFF it has a `GetAwaiter()` — a public parameterless instance MEMBER, or a referenced
// `[Extension] static GetAwaiter(this X)` — returning an *awaiter* that has `bool IsCompleted { get; }`, `T GetResult()`,
// and implements `INotifyCompletion` (the `OnCompleted(Action)` the cold-core resume binds). This is the await analog of
// the @ClrIntrinsic/dll2klib "bind by signature/metadata, embed no per-type dialect" philosophy: SuspendColdLowering's
// EmitAwaitPoint consumes an AwaitPlan and emits the SAME awaiter dance for Task / ValueTask / a WinRT IAsyncOperation /
// any custom awaitable — zero hardcoded per-type knowledge.
//
// We bind the awaiter's INotifyCompletion.OnCompleted (NOT ICriticalNotifyCompletion.UnsafeOnCompleted): our cold core
// drives resume through `intercepted().resumeWith` (#7) and relies on OnCompleted to flow the ExecutionContext, exactly
// as the pre-#10 Task path did. OnCompleted is the pattern's MANDATORY member; UnsafeOnCompleted is an optional
// optimization (skips EC flow) that the C# builder uses because IT flows EC — a future refinement, not needed here.

sealed class AwaitPlan
{
    // Default (SynchronizationContext-capturing) path: GetAwaiter on the awaitable itself.
    public string AwaiterDefName;        // e.g. "System.Runtime.CompilerServices.TaskAwaiter" (simple generic: no `arity — ilemit appends it)
    public bool AwaiterGeneric;
    // GetAwaiter entry shape. A member -> clrInstance on the awaitable; an extension -> a static call with the awaitable
    // passed as arg0 (WinRT IAsyncOperation<T>). A GENERIC extension (`GetAwaiter<TResult>(this IAsyncOperation<TResult>)`)
    // is instantiated with the result type arg — the receiver-type-constructor unifies TResult to the concrete arg.
    public bool GetAwaiterExtension;
    public string GetAwaiterExtOwner;    // the [Extension] static class FQN (extension path only)
    public bool GetAwaiterExtGeneric;    // the extension method is itself generic over the result type (WinRT)

    // #3/#64 capture control (`await(captureContext = <bool>)`): the ConfigureAwait awaiter family. Populated ONLY when
    // the awaitable exposes a member `ConfigureAwait(bool)` AND the configured awaitable it returns has a conforming
    // GetAwaiter of its own (Task-like: Task, ValueTask). ONE family for either Boolean value — `ConfigureAwait(true)`
    // and `ConfigureAwait(false)` return the same configured awaitable type — so a runtime Boolean picks no type here.
    public bool SupportsConfigureAwait;
    public string ConfiguredAwaitableDefName;   // ConfigureAwait's return type (the object GetAwaiter is called on)
    public bool ConfiguredAwaitableGeneric;
    public string ConfiguredAwaiterDefName;
    public bool ConfiguredAwaiterGeneric;
    // WHY there is no capture control, in the words the refusal uses. Two different facts, and dll2klib can only see
    // the first: it publishes the one-argument bridge on the `ConfigureAwait(bool)` DECLARATION alone, because the
    // configured awaitable it returns may live in an assembly that projection does not read (dll2klib's
    // `SupportsConfigureAwait` says so). So a type whose ConfigureAwait returns a NON-conforming awaitable reaches
    // this pass with the overload already published, and the refusal must not tell its author the member is missing.
    public string ConfigureAwaitGap;
}

partial class ReferenceMetadataIndex
{
    const string ExtensionAttrFqn = "System.Runtime.CompilerServices.ExtensionAttribute";
    readonly Dictionary<string, AwaitPlan> _awaitPlanCache = new(StringComparer.Ordinal);

    // Resolve the awaitable PATTERN for the type constructor `awaitableFqn`<..> (arity = its generic arg count, from the
    // await marker's receiver-param type). Returns null when the type is unresolvable or not awaitable (no conforming
    // GetAwaiter) — the caller then makes it loud (an un-awaitable `.await()` should never have type-checked in kotc).
    public AwaitPlan ResolveAwaitable(string awaitableFqn, int arity)
    {
        var key = awaitableFqn + "`" + arity;
        if (_awaitPlanCache.TryGetValue(key, out var cached)) return cached;
        var plan = ComputeAwaitable(awaitableFqn, arity);
        _awaitPlanCache[key] = plan;
        return plan;
    }

    AwaitPlan ComputeAwaitable(string awaitableFqn, int arity)
    {
        // When arity > 0 the awaitable is a GENERIC type — resolve the arity-qualified name ONLY. A bare `fqn` would
        // match a NON-generic sibling that shares the name (Task/Task`1, ValueTask/ValueTask`1 both exist), yielding the
        // non-generic awaiter (`TaskAwaiter` instead of `TaskAwaiter`1<T>`) — an ilverify StackUnexpected at the field
        // store. No non-generic fallback for arity>0: it would re-open exactly that sibling hazard.
        var awaitable = arity > 0
            ? ResolveNetType(awaitableFqn + "`" + arity, arity)
            : ResolveNetType(awaitableFqn, arity);
        if (awaitable == null) return null;

        // The default-path GetAwaiter: member first, then a referenced [Extension] static.
        Type awaiterRet;
        var plan = new AwaitPlan();
        var member = GetAwaiterMember(awaitable);
        if (member != null)
        {
            awaiterRet = member.ReturnType;
        }
        else
        {
            var ext = FindGetAwaiterExtension(awaitable);
            if (ext == null) return null;
            awaiterRet = ext.ReturnType;
            plan.GetAwaiterExtension = true;
            plan.GetAwaiterExtOwner = ext.DeclaringType?.FullName;
            plan.GetAwaiterExtGeneric = ext.IsGenericMethodDefinition;
        }
        if (!AwaiterConforms(awaiterRet)) return null;
        (plan.AwaiterDefName, plan.AwaiterGeneric) = NetDefName(awaiterRet);

        // #3/#64 ConfigureAwait capture control — only when the awaitable exposes it (Task-like) AND the configured
        // awaitable's own GetAwaiter ALSO conforms (it always does for Task/ValueTask). Each miss records the words
        // the refusal uses, because dll2klib publishes the one-argument bridge on the first fact alone.
        var cfg = ConfigureAwaitBoolMember(awaitable);
        if (cfg == null)
        {
            plan.ConfigureAwaitGap = "the type has no `ConfigureAwait(bool)` member "
                + "(the SynchronizationContext control is Task-like only)";
            return plan;
        }
        var configured = cfg.ReturnType;
        var configuredDef = configured.IsGenericType ? configured.GetGenericTypeDefinition() : configured;
        var cfgGetAwaiter = GetAwaiterMember(configuredDef);
        if (cfgGetAwaiter == null || !AwaiterConforms(cfgGetAwaiter.ReturnType))
        {
            plan.ConfigureAwaitGap = $"its `ConfigureAwait(bool)` returns `{configuredDef.FullName}`, which is not "
                + "itself awaitable (no conforming GetAwaiter), so there is no configured awaiter to store";
            return plan;
        }
        plan.SupportsConfigureAwait = true;
        (plan.ConfiguredAwaitableDefName, plan.ConfiguredAwaitableGeneric) = NetDefName(configured);
        (plan.ConfiguredAwaiterDefName, plan.ConfiguredAwaiterGeneric) = NetDefName(cfgGetAwaiter.ReturnType);
        return plan;
    }

    // A public parameterless instance `GetAwaiter()` MEMBER (not the generic-method-definition form).
    static MethodInfo GetAwaiterMember(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetAwaiter" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

    // A public `ConfigureAwait(bool)` instance member (Task/ValueTask expose it; a plain awaitable does not).
    static MethodInfo ConfigureAwaitBoolMember(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ConfigureAwait" && m.GetParameters() is { Length: 1 } ps
                && ps[0].ParameterType.FullName == "System.Boolean");

    // A referenced `[Extension] static GetAwaiter(this <awaitable>)` — the WinRT IAsyncOperation<T> shape
    // (WindowsRuntimeSystemExtensions.GetAwaiter<TResult>) or any 3rd-party/custom extension awaitable. The receiver's
    // type-CONSTRUCTOR must match the awaitable's (a generic extension's open receiver `IAsyncOperation<TResult>`
    // unifies with the concrete `IAsyncOperation<Int>` by generic-type-definition identity). The already-resolved
    // compile set includes both user and framework assemblies; no ambient runtime directory is searched.
    MethodInfo FindGetAwaiterExtension(Type awaitable)
    {
        EnsureNetMlc();
        if (_netMlc == null) return null;
        var awDef = awaitable.IsGenericType ? awaitable.GetGenericTypeDefinition() : awaitable;
        var awName = awDef.FullName;
        foreach (var asm in _netRefAsms)
        {
            foreach (var t in SafeExportedTypes(asm))
            {
                // a C#/VB static class = abstract + sealed
                if (!t.IsAbstract || !t.IsSealed) continue;
                // Per-type guard: a static class whose method signatures reference an MLC-unresolvable type would
                // otherwise abort the scan — skip it (a non-awaitable class is the common `.await()`-free path).
                try
                {
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "GetAwaiter" || !IsExtensionMethod(m)) continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 1) continue;
                        var recv = ps[0].ParameterType;
                        var recvDef = recv.IsGenericType ? recv.GetGenericTypeDefinition() : recv;
                        if (recvDef.FullName == awName) return m;
                    }
                }
                catch { /* unreflectable static class — skip */ }
            }
        }
        return null;
    }

    static IEnumerable<Type> SafeExportedTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    static bool IsExtensionMethod(MethodInfo m)
    {
        try { return m.GetCustomAttributesData().Any(a => a.AttributeType.FullName == ExtensionAttrFqn); }
        catch { return false; }
    }

    // The awaiter conforms iff it has PUBLIC `bool IsCompleted { get; }`, a public parameterless `GetResult()`, and a
    // PUBLIC `OnCompleted(Action)` — the members the lowering binds by direct instance call. We require the public
    // OnCompleted method (not merely an INotifyCompletion impl): an EXPLICIT-interface-only awaiter has no public member
    // for the direct call, so it must be rejected here (dll2klib then injects no `.await()` — an honest frontend miss
    // rather than a loud ilemit failure). The awaiter may be an open/constructed generic (TaskAwaiter<T>).
    static bool AwaiterConforms(Type awaiter)
    {
        if (awaiter == null) return false;
        var def = awaiter.IsGenericType ? awaiter.GetGenericTypeDefinition() : awaiter;
        var hasIsCompleted = def.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name == "IsCompleted" && p.PropertyType.FullName == "System.Boolean" && p.CanRead);
        var hasGetResult = def.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == "GetResult" && m.GetParameters().Length == 0);
        var hasOnCompleted = def.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == "OnCompleted" && m.GetParameters().Length == 1);
        return hasIsCompleted && hasGetResult && hasOnCompleted;
    }

    // The CIR type-token name of a .NET type DEFINITION (open generic def name; instance→ same). A SIMPLE generic type
    // (`TaskAwaiter`1`) drops the trailing `arity so the ilemit ConstructGeneric appends it from the emitted type-args;
    // a NESTED type whose arity rides the OUTER (`ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter`) has no trailing
    // backtick and is kept verbatim (ilemit must NOT append a second `1 — cf. il-cfgawaitgen).
    static (string name, bool generic) NetDefName(Type t)
    {
        var generic = t.IsGenericType;
        var full = (generic ? t.GetGenericTypeDefinition() : t).FullName ?? t.Name;
        var bt = full.LastIndexOf('`');
        if (bt > full.LastIndexOf('+') && bt >= 0 && full.Skip(bt + 1).All(char.IsDigit))
            full = full.Substring(0, bt);
        return (full, generic);
    }
}
