// SHARED field-legality oracle: "may a value of this type be stored in an INSTANCE FIELD of an ordinary
// (non-byref-like) CLR type?". Linked into bir2cir (the only tool that mints heap storage for a Kotlin value);
// kept here beside TypeNode/IrSanity because it is a pure fact about the shared Type vocabulary, with the
// concrete `ref struct` set supplied by the caller (bir2cir reads it off the referenced assemblies'
// IsByRefLikeAttribute — see ReferenceMetadataIndex.IsByRefLikeFqn).
//
// Three bir2cir sites mint such storage, and all three consume this file:
//   * the suspend state machine's spilled locals/params/temps (SuspendColdLowering.FieldStorage),
//   * a suspend lambda's captures (the same gate, via FunGen's lambda ctor),
//   * a non-suspend lambda's closure-class captures (ClosureSynthesis).
// A byref-like (`ref struct`) value reaching any of them is a CLR TypeLoadException at class-load time, so it
// is refused at compile time instead, with the message built here so all three read alike.

#nullable enable
using System;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

/// <summary>Why a type cannot be the type of an instance field of an ordinary CLR type.</summary>
public enum FieldRejection
{
    /// <summary>Legal — the value can live in a heap field.</summary>
    None,
    /// <summary>A `ref struct` (byref-like): the CLR forbids it as a field of a non-byref-like type.</summary>
    ByRefLike,
    /// <summary>A managed pointer (`ref T`): never a field type; it is an argument/return slot only.</summary>
    ByRef,
}

public static class FieldLegality
{
    /// <summary>
    /// Classify a type as heap-storable or not. <paramref name="isByRefLikeFqn"/> answers "is this bare FQN a
    /// `ref struct`" for the compilation's reference set. The walk is recursive: a byref-like appearing anywhere
    /// in the type (array element, generic argument) is equally unstorable — and equally unconstructable — so
    /// there is no valid program the recursion can reject.
    /// </summary>
    public static FieldRejection Classify(TypeNode? t, Func<string, bool> isByRefLikeFqn, out string? offendingFqn)
    {
        offendingFqn = null;
        if (t == null) return FieldRejection.None;
        switch (t)
        {
            case TypeNode.ByRef:
                return FieldRejection.ByRef;
            case TypeNode.Fqn f:
                if (isByRefLikeFqn(f.Name)) { offendingFqn = f.Name; return FieldRejection.ByRefLike; }
                if (f.Args != null)
                    foreach (var a in f.Args)
                    {
                        var r = Classify(a, isByRefLikeFqn, out offendingFqn);
                        if (r != FieldRejection.None) return r;
                    }
                return FieldRejection.None;
            case TypeNode.Array a2:
                return Classify(a2.Elem, isByRefLikeFqn, out offendingFqn);
            case TypeNode.Nullable n:
                return Classify(n.Of, isByRefLikeFqn, out offendingFqn);
            case TypeNode.Oblivious ob:
                return Classify(ob.Of, isByRefLikeFqn, out offendingFqn);
            default:
                // tv / star / fn: a type variable is always a heap-storable slot on the CLR (a `ref struct` cannot
                // be a generic argument), `star` is erased before emission, and a function type is a delegate.
                return FieldRejection.None;
        }
    }

    /// <summary>A short human-readable rendering of a type for a diagnostic (`Span&lt;Int&gt;`, `ref Int`).</summary>
    public static string Render(TypeNode? t) => t switch
    {
        null => "<untyped>",
        TypeNode.Fqn f => f.Args == null || f.Args.Length == 0
            ? f.Name
            : f.Name + "<" + string.Join(", ", System.Array.ConvertAll(f.Args, Render)) + ">",
        TypeNode.Array a => "Array<" + Render(a.Elem) + ">",
        TypeNode.Nullable n => Render(n.Of) + "?",
        TypeNode.Oblivious ob => Render(ob.Of) + "!",
        TypeNode.ByRef b => "ref " + Render(b.Of),
        TypeNode.Tv v => (v.Scope == "method" ? "!!" : "!") + v.I,
        TypeNode.Star => "*",
        TypeNode.Fn fn => (fn.Suspend ? "suspend (" : "(") + string.Join(", ", System.Array.ConvertAll(fn.Params, Render)) + ") -> " + Render(fn.Ret),
        _ => t.ToString() ?? "<type>",
    };

    static string KindPhrase(FieldRejection why, string? offendingFqn) =>
        why == FieldRejection.ByRef
            ? "a managed pointer (`ref`)"
            : $"byref-like (a `ref struct`{(offendingFqn != null ? $", via `{offendingFqn}`" : "")})";

    /// <summary>
    /// A value that must SURVIVE a suspension: the state machine has to hold it in an instance field, which the
    /// CLR forbids for this type. Mirrors C# CS4007. <paramref name="role"/> names the source-level thing ("local
    /// variable", "awaited value", "evaluation-order temporary", ...); <paramref name="across"/> names the first
    /// suspending callee it lives across.
    /// </summary>
    public static string SuspendMessage(
        string posPrefix, string owner, string role, string name, TypeNode? type,
        string? offendingFqn, FieldRejection why, string? across)
    {
        var span = across != null ? $" and lives across the suspending call to `{across}`" : "";
        var mirror = why == FieldRejection.ByRef ? "" : " (mirrors C# CS4007)";
        return $"{posPrefix}suspend-lowering: in `{owner}`, the {role} `{name}` has type `{Render(type)}`, which is "
             + $"{KindPhrase(why, offendingFqn)}{span}. A suspend function's state machine must hold it in an instance "
             + "field of the generated state-machine class, and the CLR forbids that for this type. Restructure so the "
             + $"value does not span a suspension point (compute it, or re-create it, after the call){mirror}.";
    }

    /// <summary>
    /// A suspend function's own ABI cannot carry this type. Once the body suspends, a parameter and a capture are
    /// fields written by the state machine's constructor and the result crosses the cold entry's `Any?` slot and
    /// the public `Task&lt;R&gt;` bridge, none of which can hold a byref-like value. The rule is stated on the
    /// DECLARATION rather than on the body — as C# states CS4012 — so that adding a suspension to a suspend
    /// function never changes whether its signature was legal.
    /// </summary>
    public static string SuspendAbiMessage(
        string posPrefix, string owner, string role, string name, TypeNode? type,
        string? offendingFqn, FieldRejection why)
    {
        var mirror = why == FieldRejection.ByRef ? "" : " (mirrors C# CS4012)";
        return $"{posPrefix}suspend-lowering: in `{owner}`, the {role} `{name}` has type `{Render(type)}`, which is "
             + $"{KindPhrase(why, offendingFqn)}. A `suspend` declaration cannot mention it: once the body suspends, "
             + $"the {role} is carried by the generated state machine and its cold-entry/Task ABI, and the CLR forbids "
             + "that for this type. The rule is on the declaration, not on the body, so adding a suspension later "
             + $"cannot change it. Take the value as a plain (non-suspend) function's {role} instead{mirror}.";
    }

    /// <summary>
    /// The capture refusal for a synthesized closure-shaped class (mirrors C# CS8352): the class a capturing
    /// lambda becomes, or the fun-interface class a `newSam` carries. A capture is unconditionally heap storage —
    /// the generated class's field — so no liveness question arises.
    /// </summary>
    public static string CaptureMessage(
        string posPrefix, string owner, string closureName, string name, TypeNode? type,
        string? offendingFqn, FieldRejection why)
    {
        return $"{posPrefix}closure-synthesis: in `{owner}`, the value `{name}` of type `{Render(type)}` is captured "
             + $"by a lambda, and it is {KindPhrase(why, offendingFqn)}. A captured value is stored in an instance "
             + $"field of the generated class `{closureName}`, and the CLR forbids that for this type. Pass the "
             + "value as a parameter instead of capturing it (mirrors C# CS8352).";
    }

    /// <summary>
    /// The `File.kt:line: ` decl-source prefix of a BIR/CIR declaration, or "" when it carries no `pos`.
    /// Same format as <c>IrSanity.PosPrefix</c> (that one reads the JsonElement view of the same node).
    /// </summary>
    public static string PosPrefix(JsonNode? decl)
    {
        if (decl is not JsonObject o || o["pos"] is not JsonObject pos) return "";
        if ((pos["f"] as JsonValue)?.TryGetValue<string>(out var path) != true || path == null) return "";
        var file = System.IO.Path.GetFileName(path);
        if (pos["l"] is JsonValue lv && lv.TryGetValue<int>(out var line) && line >= 0) return file + ":" + line + ": ";
        return file + ": ";
    }
}
