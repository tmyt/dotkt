// #370: the FIXED BCL members ilemit expands a Kotlin operation into.
//
// `enumValues()` becomes `Enum.GetValues`, string `+` becomes `String.Concat`, a `clrDynInstance` dispatch becomes
// `GetType`/`GetMethod`/`Invoke`, an emitted enumerator's slots are `IEnumerator`'s. The source wrote none of them —
// but "did the source write it" is not the question. The question is whether ilemit encodes an EXTERNAL member as a
// CIL operand, and it does, in every one of these.
//
// None of them varies: no type arguments, no overload chosen per call site, the same member every time. So they do
// not need a carrier per node — one table per document, keyed by role, resolved here like everything else. The
// expansion stays in the emitter, which is a question about which layer owns the SHAPE; the member it emits arrives
// named, which is the question this issue is about. The two are separable, and separating them is what lets this
// land without waiting on the intrinsic-binding programme.

using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    // role -> (owner, member, parameter types). A role names what the emitter needs it FOR, so a reader of the
    // emitter can find the entry without knowing the BCL signature by heart.
    static readonly (string Role, string Owner, string Name, string[] Params)[] WellKnownMembers =
    {
        ("String.Concat2",        "System.String",   "Concat",            new[] { "System.String", "System.String" }),
        ("Type.FromHandle",       "System.Type",     "GetTypeFromHandle", new[] { "System.RuntimeTypeHandle" }),
        ("Object.GetType",        "System.Object",   "GetType",           new string[0]),
        ("Object.ToString",       "System.Object",   "ToString",          new string[0]),
        ("Object.GetHashCode",    "System.Object",   "GetHashCode",       new string[0]),
        ("Object.Equals",         "System.Object",   "Equals",            new[] { "System.Object" }),
        ("Enum.GetValues",        "System.Enum",     "GetValues",         new[] { "System.Type" }),
        ("Enum.Parse",            "System.Enum",     "Parse",             new[] { "System.Type", "System.String" }),
        ("Type.GetMethod",        "System.Type",     "GetMethod",         new[] { "System.String" }),
        ("MethodInfo.Invoke",     "System.Reflection.MethodBase", "Invoke", new[] { "System.Object", "System.Object[]" }),
        ("Enumerable.GetEnumerator", "System.Collections.IEnumerable", "GetEnumerator", new string[0]),
        ("Enumerator.MoveNext",   "System.Collections.IEnumerator", "MoveNext",    new string[0]),
        ("Enumerator.Current",    "System.Collections.IEnumerator", "get_Current", new string[0]),
        ("Enumerator.Reset",      "System.Collections.IEnumerator", "Reset",       new string[0]),
        ("Disposable.Dispose",    "System.IDisposable", "Dispose",        new string[0]),
    };

    /// <summary>Stamp the fixed-member table on a document root. Every entry resolves or the build stops.</summary>
    public static void ResolveWellKnown(JsonNode root, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        if (root is not JsonObject document) return;
        var table = new JsonObject();
        foreach (var (role, owner, name, parameters) in WellKnownMembers)
        {
            var open = ResolveOwnerType(new TypeNode.Fqn(owner));
            if (open == null) continue;   // a build that cannot see this assembly cannot need the member either
            var wanted = parameters.Select(ParseWellKnownParam).ToList();
            var cands = open.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == name && m.GetParameters().Length == wanted.Count
                    && !m.IsGenericMethodDefinition).ToList();
            var win = TryPickUnique(cands, wanted, Array.Empty<TypeNode>());
            if (win == null) continue;
            table[role] = MemberRefJson(win, MemberRefNode.Kinds.Method, open, Array.Empty<TypeNode>());
        }
        if (table.Count > 0) document["wellKnownRefs"] = table;
    }

    static TypeNode ParseWellKnownParam(string spec) =>
        spec.EndsWith("[]", StringComparison.Ordinal)
            ? new TypeNode.Array(new TypeNode.Fqn(spec[..^2]))
            : new TypeNode.Fqn(spec);
}
