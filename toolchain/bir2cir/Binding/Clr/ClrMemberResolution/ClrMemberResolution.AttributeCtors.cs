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
    public static void ResolveAttributeCtors(IReadOnlySet<string> bindingLocalTypes, BirFileDeclaration root, ReferenceMetadataIndex refs, string file)
    {
        refs = refs ?? throw new ArgumentNullException(nameof(refs));
        TypedAttributeBinding.Visit(root, attribute => {
            ResolveAppliedAttribute(bindingLocalTypes, refs, attribute);
            // This lane follows the owning frontier. The earlier DOM crossing
            // check can no longer inspect a constructor first selected here.
            if (attribute.MemberRef.HasValue)
                ForeignNullableGenericCrossing.CheckParameters(attribute.MemberRef.Value.ParameterTypes.OrEmpty(),
                    file, "<unknown>", ".ctor");
        });
    }

    static void ResolveAppliedAttribute(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, BirAttribute attr)
    {
        if (attr.MemberRef.IsPresent) return;
        if (attr.Attr.OrDefault() is not TypeNode.Fqn attrFqn) return;
        // Frontend stdlib meta-annotations are not CLR imports. Bind the
        // actual stdlib constructor before publishing them on a marker type.
        var stdlibMeta = !attr.AttrExternal.OrDefault()
            && attrFqn.Name is "kotlin.annotation.Target" or "kotlin.RequiresOptIn"
            && bindingLocalTypes?.Contains(attrFqn.Name) != true;
        if (!attr.AttrExternal.OrDefault() && !stdlibMeta) return;
        var assembly = attr.AttrAssembly.OrDefault();
        if (stdlibMeta)
            assembly = bindingReferences.CompileStdlibName
                ?? throw new InvalidOperationException($"{attrFqn.Name} requires the stdlib in the compile references");
        var arguments = attr.ArgTypes.OrEmpty();
        if (ResolveAppliedAttributeCtor(bindingLocalTypes, bindingReferences, attrFqn, assembly, arguments, attr.ConstructorNativeKey.OrDefault()) is { } selected)
        {
            attr.AttrExternal = true;
            attr.MemberRef = BirMemberReference.FromPhysical(selected);
            attr.ConstructorNativeKey = default;
        }
        else if (stdlibMeta)
            throw new InvalidOperationException($"{attrFqn.Name} constructor not found in '{assembly}'");
    }

    internal static AttributeApplication ResolveAttributeCtor(IReadOnlySet<string> bindingLocalTypes, AttributeApplication attribute, ReferenceMetadataIndex refs)
    {
        refs = refs ?? throw new ArgumentNullException(nameof(refs));
        if (attribute.AttrExternal != true || attribute.MemberRef != null || attribute.Attr is not TypeNode.Fqn type)
            return attribute;
        return attribute with { MemberRef = ResolveAppliedAttributeCtor(bindingLocalTypes, refs, type, attribute.AttrAssembly, attribute.ArgTypes) };
    }

    static MemberRefNode ResolveAppliedAttributeCtor(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, TypeNode.Fqn attrFqn, string assembly, IReadOnlyList<TypeNode> arguments, string nativeKey = null)
    {
        // The exact declaring scope when one is stated: a compiler-synthesized attribute can share its FQN with
        // a private lookalike, which is why that key exists at all.
        var open = assembly != null
            ? bindingReferences.ResolveRefTypeIn(attrFqn.Name, assembly)
            : ResolveOwnerType(attrFqn, refs: bindingReferences);
        // An attribute type outside the reference set is already skipped downstream — a compile-time opt-in
        // marker that need not survive into IL. Nothing to name, so nothing is claimed.
        if (open == null) return null;
        if (nativeKey != null)
            return ClrMemberReferences.MemberRefOf(bindingReferences, bindingLocalTypes,
                bindingReferences.SelectedNativeConstructor(open, nativeKey), MemberRefNode.Kinds.Ctor, open, attrFqn.Args);
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
        var win = ClrDeclarationMatching.TryPickUniqueCtor(bindingReferences, ctors, argNodes, attrFqn.Args);
        return win == null ? null : ClrMemberReferences.MemberRefOf(bindingReferences, bindingLocalTypes, win, MemberRefNode.Kinds.Ctor, open, attrFqn.Args);
    }
}
