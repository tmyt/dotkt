// #370: the constructor an APPLIED EXTERNAL attribute invokes.
//
// An applied attribute is a call — `[Obsolete("x")]` runs `Obsolete(string)` — and when the attribute type
// lives in another assembly that call is an external member reference like any other. It was the last one
// stated with no identity at all: the document carried the attribute TYPE and the declared argument types,
// and left picking the constructor to whoever encoded the blob.
//
// The argument-type vector is a descriptor, not an identity: it says what the call site passes, not which
// declaration answers. Two constructors can accept the same arity while differing in a way the vector does
// not spell, and the encoder would then be choosing. So the constructor is selected HERE, by the same
// structural match every other member goes through, and written down.
//
// A LOCALLY-declared attribute stays as it is: its constructor is a MethodDef being built by this same
// compilation, which has no assembly identity to reference yet.

using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    // The keys an applied attribute list rides on. A RETURN-position attribute lives under its own key, which
    // is how 496 of them went unresolved while the walk looked convincingly complete: `attrs` covers a type, a
    // member and a parameter, and a return is none of those. Naming the carriers is what makes the next one an
    // entry here rather than a silent omission.
    static readonly string[] AttributeCarriers = { "attrs", "retAttrs" };

    /// <summary>
    /// Resolve every applied EXTERNAL attribute's constructor and stamp it. Runs over the whole document
    /// because an attribute rides on any declaration — a type, a member, a parameter, a return.
    /// </summary>
    public static void ResolveAttributeCtors(JsonNode root, ReferenceMetadataIndex refs)
    {
        // One index per run; assigning rather than coalescing, for the reason MemberRefOf's own note gives.
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        WalkAttrs(root);
    }

    static void WalkAttrs(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var carrier in AttributeCarriers)
                    if (obj[carrier] is JsonArray attrs)
                        foreach (var entry in attrs)
                            if (entry is JsonObject attr) ResolveAppliedAttribute(attr);
                foreach (var kv in obj) WalkAttrs(kv.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr) WalkAttrs(item);
                break;
        }
    }

    static void ResolveAppliedAttribute(JsonObject attr)
    {
        if ((attr["attrExternal"] as JsonValue)?.GetValue<bool>() != true) return;
        if (attr.ContainsKey("memberRef")) return;
        if (TypeJson.Read(attr["attr"]) is not TypeNode.Fqn attrFqn) return;
        var assembly = (attr["attrAssembly"] as JsonValue)?.GetValue<string>();
        // The exact declaring scope when one is stated: a compiler-synthesized attribute can share its FQN with
        // a private lookalike, which is why that key exists at all.
        var open = assembly != null
            ? _refs.ResolveRefTypeIn(attrFqn.Name, assembly)
            : ResolveOwnerType(attrFqn);
        // An attribute type outside the reference set is already skipped downstream — a compile-time opt-in
        // marker that need not survive into IL. Nothing to name, so nothing is claimed.
        if (open == null) return;
        // An attribute's declared argument vector admits BOTH spellings CIR allows for a primitive — the
        // metadata-reader shorthand (`int`) and the BCL FQN (`System.Int32`) — because it is written by
        // whichever pass authored the attribute. Canonicalize before matching, or the same constructor is
        // found or missed depending on who wrote the application down.
        var argNodes = (attr["argTypes"] as JsonArray)?
            .Select(t => TypeJson.Read(t) is { } n ? BirTypeLowering.CanonicalPhysicalSlotType(n) : null)
            .ToList() ?? new List<TypeNode>();
        if (argNodes.Any(t => t == null)) return;
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == argNodes.Count).ToList();
        // ONE POLICY for the whole lane. An annotation whose constructor this cannot select is left unnamed,
        // exactly as an annotation whose type does not resolve is — never an abort. The frontend accepted the
        // program, and an attribute is decoration: refusing to compile over one would reject source because a
        // marker could not be encoded. (`PickUnique` throws, which is right where a MEMBER is being called and
        // wrong here, and the difference is the whole reason this uses the quiet form.)
        var win = TryPickUniqueCtor(ctors, argNodes, attrFqn.Args);
        if (win == null) return;
        attr["memberRef"] = MemberRefJson(win, MemberRefNode.Kinds.Ctor, open, attrFqn.Args);
    }
}
