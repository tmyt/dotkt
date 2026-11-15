// #370: the members a COLLECTION-LITERAL construction invokes.
//
// `listOf(1, 2)` becomes a `newList` node, and an emitter turns that into `newobj List<int>()` followed by a
// `callvirt Add` per element. Those two members are as external as any other — they live in
// System.Collections — but they were never written down, because the node says what to BUILD rather than what
// to call, and the emitter filled in the rest by name.
//
// Choosing the constructor and the accumulator is a decision about physical CLR representation, which is this
// layer's to make. It is not a re-derivation of what the source meant: the source said `listOf`, and the
// choice of `List`1` with a parameterless constructor and a one-argument `Add` is the shape THIS pass picked
// when it minted the node. So it names them, and the emitter stops asking.
//
// That includes the TYPE the list literal constructs. A reference states its member's declaring type together with
// the instantiation the use site anchors it on, so the constructor named below already says `List<int>` —
// the emitter reads the constructed type back off it and names no BCL type of its own (#400).
//
// The two spellings agree because this pass runs on the ALREADY-LOWERED tree: the reference's declaring
// argument is the node's own `elem` put back through physical type lowering, which is
// idempotent by then. That fixed point is what makes the constructor's declaring type the same type the emitter
// used to build from the node — it is a property of WHERE this pass sits, not a coincidence of the two paths.
//
// The signature below is the declaration's own — `Add(!0)` — matched structurally like
// every other member, so a BCL that grows an overload cannot silently change which one is meant.
//
// A node whose element/key/value type cannot be read is a node this pass cannot complete. It fails here, where
// the missing fact is, rather than travelling to the emitter as a construction with nothing to construct.

using System.Reflection;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    // The list literal and spread roles are authored by the typed template owner.
    static readonly (string Kind, string Owner, int Arity, string Accumulator, string RefKey)[] CollectionTemplates =
    {
        ("newList", "System.Collections.Generic.List", 1, "Add", "addRef"),
    };
    const string SpreadOwner = "System.Collections.Generic.List";
    const string NullableFqn = "System.Nullable";

    static InvalidOperationException Missing(string owner, string member) =>
        new($"bir2cir: '{owner}.{member}' does not resolve to one declaration for spreadConcat");

    // Shared by final native storage binding and already-authored Unit reads.
    static MemberRefNode StaticFieldReference(TypeNode.Fqn owner, string name)
    {
        // A type this compilation emits keeps the local axis (#395) and must not be given an external identity,
        // even though the reference surface can answer for it: the stdlib runtime build compiles against a
        // PREVIOUS build of the assembly it is producing, so kotlin.text.HexFormat+$Companion resolved there and
        // was handed a reference to itself that the emitter's own universe then could not contain.
        if (_localTypes.Contains(owner.Name)) return null;
        var open = ResolveOwnerType(owner);
        if (open == null) return null;
        var field = open.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null && !(field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)) field = null;
        return field == null ? null : FieldRefOf(field, open, owner.Args ?? Array.Empty<TypeNode>());
    }

    static bool IsFunctionShape(TypeNode type) => type switch
    {
        TypeNode.Fn => true,
        TypeNode.Nullable n => IsFunctionShape(n.Of),
        TypeNode.Oblivious o => IsFunctionShape(o.Of),
        _ => false,
    };

}
