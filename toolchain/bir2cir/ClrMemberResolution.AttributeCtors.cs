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
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    /// <summary>
    /// Resolve every applied EXTERNAL attribute's constructor and stamp it. Runs over the whole document
    /// because an attribute rides on any declaration — a type, a member, a parameter, a return.
    /// </summary>
    public static void ResolveAttributeCtors(BirFileDeclaration root, ReferenceMetadataIndex refs, string file)
    {
        // One index per run; assigning rather than coalescing, for the reason MemberRefOf's own note gives.
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        TypedAttributeBinding.Visit(root, attribute => {
            ResolveAppliedAttribute(attribute);
            // This lane follows the owning frontier. The earlier DOM crossing
            // check can no longer inspect a constructor first selected here.
            if (attribute.MemberRef.HasValue)
                ForeignNullableGenericCrossing.CheckParameters(attribute.MemberRef.Value.ParameterTypes.OrEmpty(),
                    file, "<unknown>", ".ctor");
        });
    }

    static void ResolveAppliedAttribute(BirAttribute attr)
    {
        if (!attr.AttrExternal.OrDefault() || attr.MemberRef.IsPresent) return;
        if (attr.Attr.OrDefault() is not TypeNode.Fqn attrFqn) return;
        var assembly = attr.AttrAssembly.OrDefault();
        var arguments = attr.ArgTypes.OrEmpty();
        if (ResolveAppliedAttributeCtor(attrFqn, assembly, arguments) is { } selected)
            attr.MemberRef = BirMemberReference.FromPhysical(selected);
    }

    internal static AttributeApplication ResolveAttributeCtor(AttributeApplication attribute, ReferenceMetadataIndex refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        if (attribute.AttrExternal != true || attribute.MemberRef != null || attribute.Attr is not TypeNode.Fqn type)
            return attribute;
        return attribute with { MemberRef = ResolveAppliedAttributeCtor(type, attribute.AttrAssembly, attribute.ArgTypes) };
    }

    static MemberRefNode ResolveAppliedAttributeCtor(TypeNode.Fqn attrFqn, string assembly, IReadOnlyList<TypeNode> arguments)
    {
        // The exact declaring scope when one is stated: a compiler-synthesized attribute can share its FQN with
        // a private lookalike, which is why that key exists at all.
        var open = assembly != null
            ? _refs.ResolveRefTypeIn(attrFqn.Name, assembly)
            : ResolveOwnerType(attrFqn);
        // An attribute type outside the reference set is already skipped downstream — a compile-time opt-in
        // marker that need not survive into IL. Nothing to name, so nothing is claimed.
        if (open == null) return null;
        // An attribute's declared argument vector admits BOTH spellings CIR allows for a primitive — the
        // metadata-reader shorthand (`int`) and the BCL FQN (`System.Int32`) — because it is written by
        // whichever pass authored the attribute. Canonicalize before matching, or the same constructor is
        // found or missed depending on who wrote the application down.
        var argNodes = arguments.Select(type => type is { } ? BirTypeLowering.CanonicalPhysicalSlotType(type) : null).ToList();
        if (argNodes.Any(t => t == null)) return null;
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == argNodes.Count).ToList();
        // ONE POLICY for the whole lane. An annotation whose constructor this cannot select is left unnamed,
        // exactly as an annotation whose type does not resolve is — never an abort. The frontend accepted the
        // program, and an attribute is decoration: refusing to compile over one would reject source because a
        // marker could not be encoded. (`PickUnique` throws, which is right where a MEMBER is being called and
        // wrong here, and the difference is the whole reason this uses the quiet form.)
        var win = TryPickUniqueCtor(ctors, argNodes, attrFqn.Args);
        return win == null ? null : MemberRefOf(win, MemberRefNode.Kinds.Ctor, open, attrFqn.Args);
    }
}
