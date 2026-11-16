using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

static class InlinePayloadSelection
{
    internal static string ReceiverRole(bool extension, bool isStatic) => extension ? "extensionParam" : !isStatic ? "dispatch" : "none";

    internal static (T Payload, bool SameModule, string Diagnostic) Resolve<T>(string identity, string owner, string name, int pc, int ga,
        TypeNode[] signature, Func<string, T> localIdentity, Func<string, T> referencedIdentity,
        Func<string, string, int, int, IReadOnlyList<T>> localCandidates, Func<string, string, int, int, IReadOnlyList<T>> referencedCandidates,
        Func<string, int, int, IReadOnlyList<T>> localOwnerless, Func<string, int, int, IReadOnlyList<T>> referencedOwnerless,
        Func<T, TypeNode[]> parameters) where T : class
    {
        if (identity != null)
        {
            if (localIdentity(identity) is { } local) return (local, true, null);
            var referenced = referencedIdentity?.Invoke(identity);
            return referenced != null ? (referenced, false, null)
                : (null, false, $"no [KotlinInline] payload for frontend declaration identity '{identity}'");
        }
        (T Hit, int Matches) Select(IReadOnlyList<T> choices)
        {
            var matches = choices?.Where(candidate => SignatureMatches(parameters(candidate), signature)).ToArray() ?? Array.Empty<T>();
            return (matches.Length == 1 ? matches[0] : null, matches.Length);
        }
        if (owner != null)
        {
            var local = localCandidates(owner, name, pc, ga);
            if (local?.Count > 0)
            {
                var chosen = Select(local);
                return chosen.Hit != null ? (chosen.Hit, true, null)
                    : (null, false, $"no unique same-module inline overload for the call's param signature ({local.Count} candidate(s), {chosen.Matches} matched)");
            }
            var referenced = referencedCandidates?.Invoke(owner, name, pc, ga);
            if (referenced?.Count > 0)
            {
                var chosen = Select(referenced);
                return chosen.Hit != null ? (chosen.Hit, false, null)
                    : (null, false, $"no unique cross-module [KotlinInline] overload for the call's param signature ({referenced.Count} candidate(s), {chosen.Matches} matched)");
            }
            return (null, false, "no [KotlinInline] payload found");
        }
        var locals = localOwnerless(name, pc, ga);
        if (locals?.Count > 0)
        {
            var chosen = Select(locals);
            if (chosen.Hit != null) return (chosen.Hit, true, null);
            if (chosen.Matches > 1) return (null, false, $"owner-less callInline: no unique same-module paramSig match among {locals.Count} kotlin.* candidate(s) ({chosen.Matches} matched)");
        }
        var references = referencedOwnerless?.Invoke(name, pc, ga);
        if (references?.Count > 0)
        {
            var chosen = Select(references);
            return chosen.Hit != null ? (chosen.Hit, false, null)
                : (null, false, $"owner-less callInline: no unique paramSig match among {references.Count} kotlin.* candidate(s) ({chosen.Matches} matched)");
        }
        return (null, false, "owner-less callInline: no file class (same-module stash or ref.dll) hosts this inline fn");
    }

    internal static bool SignatureMatches(IReadOnlyList<TypeNode> declared, IReadOnlyList<TypeNode> supplied)
        => declared != null && supplied != null && declared.Count == supplied.Count
            && declared.Select((type, index) => TypeNode.ToJson(Normalize(type)) == TypeNode.ToJson(Normalize(supplied[index]))).All(equal => equal);

    static TypeNode Normalize(TypeNode type) => type switch {
        TypeNode.Fqn named => named with { Name = Name(named.Name), Args = named.Args?.Select(Normalize).ToArray() },
        TypeNode.Nullable value => value with { Of = Normalize(value.Of) }, TypeNode.Oblivious value => value with { Of = Normalize(value.Of) },
        TypeNode.Projection value => value with { Of = Normalize(value.Of) }, TypeNode.ByRef value => value with { Of = Normalize(value.Of) },
        TypeNode.Ptr value => value with { Of = Normalize(value.Of) }, TypeNode.Array value => value with { Elem = Normalize(value.Elem) },
        TypeNode.Mod value => value with { M = Normalize(value.M), Of = Normalize(value.Of) },
        TypeNode.Fn value => value with { Ret = Normalize(value.Ret), Params = value.Params.Select(Normalize).ToArray(),
            Recv = value.Recv == null ? null : Normalize(value.Recv), Ctx = value.Ctx?.Select(Normalize).ToArray() },
        TypeNode.FnPtr value => value with { Ret = Normalize(value.Ret), Params = value.Params.Select(Normalize).ToArray() }, _ => type,
    };
    static string Name(string name)
    {
        var result = name;
        var search = 0;
        while ((search = result.IndexOf('`', search)) >= 0)
        {
            var end = search + 1;
            while (end < result.Length && char.IsDigit(result[end])) end++;
            result = result.Remove(search, end - search);
        }
        return result.Replace('+', '.');
    }
}
