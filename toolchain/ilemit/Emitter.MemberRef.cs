// #370: resolving the ONE scalar member reference CIR carries, exactly.
//
// bir2cir resolved a Kotlin operation to a single declaration in the target reference universe and wrote that
// answer down. This reads it back. There is no selection here and there must never be: the declaring type is
// stated with its assembly, so it is looked up in that assembly; the member's own signature is stated in full,
// so the one declaration whose signature equals it is the answer. No name-only fallback, no arity probe, no
// assignability, no most-derived rule, no host reflection — each of those exists to CHOOSE between candidates,
// and the choosing already happened.
//
// Zero matches means the document and the reference set disagree, and the message says so with the complete
// reference — an emitter that reports "member not found" without saying which member is the reason a target
// mismatch takes an afternoon. More than one match means two declarations share a signature the reference
// cannot tell apart, which is a defect in the identity, not a cue to pick.
//
// TRANSITIONAL. While the old descriptors still exist, `ShadowParity` resolves BOTH ways and refuses to
// continue if they disagree — that is what proves, over the whole corpus rather than by argument, that the
// reference names what the emitter links today. It disappears with the descriptors it compares against.

using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using DotKt.Bir;

sealed partial class Emitter
{
    /// <summary>How many references were resolved, and how many were checked against the legacy path.</summary>

    /// <summary>Every key a resolved member reference can ride on. The counting below is keyed on this set.</summary>

    readonly Dictionary<string, MemberInfo> _wellKnown = new(StringComparer.Ordinal);

    /// <summary>Members whose identity a RESOLVED reference supplied, plus the sanctioned residual.</summary>
    readonly HashSet<(Module, int)> _provenance = new();
    /// <summary>Set once the check has something to check against; before that every build would trip it.</summary>
    bool _auditArmed;

    /// <summary>
    /// Record that this member's identity came from somewhere #370 sanctions.
    /// </summary>
    /// <remarks>
    /// Three previous versions of the residual gate matched SOURCE SHAPES — a name written, a name computed, a
    /// candidate set filtered — and each one reported green while a shape it did not match was live. Shapes are
    /// endless; the property is not. This records provenance at the few places identity legitimately comes from,
    /// and the emit chokepoints check it, so HOW a member was found stops mattering.
    /// </remarks>
    MemberInfo Sanction(MemberInfo member)
    {
        if (Token(member) is { } key) _provenance.Add(key);
        return member;
    }

    T Sanction<T>(T member) where T : MemberInfo { Sanction((MemberInfo)member); return member; }

    static (Module, int)? Token(MemberInfo member)
    {
        try
        {
            var m = UnwrapSignatureView(member);
            return m == null ? null : (m.Module, m.MetadataToken);
        }
        catch { return null; }
    }

    /// <summary>
    /// Refuse to encode an EXTERNAL member whose identity no resolved reference supplied.
    /// </summary>
    /// <remarks>
    /// A member of the assembly under construction has no reference to come from — that is the local axis (#395).
    /// Everything else reaching an operand or a MethodImpl must have arrived named.
    /// </remarks>
    void AuditExternal(MemberInfo member, string position)
    {
        if (!_auditArmed || member == null) return;
        try
        {
            var m = UnwrapSignatureView(member);
            if (m?.Module is ModuleBuilder or null) return;             // being built here
            if (m.DeclaringType is TypeBuilder) return;                 // ditto, through a constructed view
            if (Token(m) is not { } key || _provenance.Contains(key)) return;
            throw new InvalidOperationException(
                $"ilemit: {position} names the external member {Describe(m)}, whose identity no resolved reference "
                + "supplied. Every external member ilemit consumes arrives named (#370); if this one cannot, "
                + "sanction it explicitly and say why in docs/architecture.md invariant 10.");
        }
        catch (InvalidOperationException) { throw; }
        catch { /* a member whose module cannot be read cannot be audited */ }
    }

    /// <summary>
    /// Load the document's table of fixed BCL members — the ones a Kotlin operation EXPANDS into.
    /// </summary>
    /// <remarks>
    /// The source wrote none of them, and that is not the test. The test is whether an EXTERNAL member reaches a
    /// CIL operand, which it does for every entry here. They take no per-site decision — same member every time,
    /// no type arguments, no overload picked from context — so one table per document says them all, and the
    /// emitter keeps the expansion while the member arrives named.
    /// </remarks>
    void LoadWellKnown(IEnumerable<JsonElement> files)
    {
        _auditArmed = true;
        foreach (var file in files)
        {
            if (file.ValueKind != JsonValueKind.Object
                || !file.TryGetProperty("wellKnownRefs", out var table)
                || table.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in table.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    var resolved = ResolveMemberRef(MemberRefNode.Read(entry.Value));
                    // Every document states the same fixed member for a role. Taking the first and skipping the
                    // rest would make a disagreement invisible — and a disagreement means two documents were
                    // resolved against different reference sets, which is worth stopping for.
                    if (_wellKnown.TryGetValue(entry.Name, out var already))
                    {
                        if (already.MetadataToken != resolved.MetadataToken || already.Module != resolved.Module)
                            throw new InvalidOperationException(
                                $"ilemit: two documents name different members for the fixed-member role "
                                + $"`{entry.Name}`: {Describe(already)} and {Describe(resolved)}");
                        continue;
                    }
                    _wellKnown[entry.Name] = Sanction(resolved);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"ilemit: the document's `{entry.Name}` reference does not resolve: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>The fixed BCL member a role names. Absent means the producer did not state it.</summary>
    T WellKnown<T>(string role) where T : MemberInfo =>
        _wellKnown.TryGetValue(role, out var m) && m is T typed ? typed
            : throw new InvalidOperationException(
                $"ilemit: the expansion needs the `{role}` member, which this document does not name. Every "
                + "external member an operand encodes arrives resolved (#370)");

    /// <summary>
    /// The member a REQUIRED carrier names. The schema makes these mandatory, so a node without one is a
    /// producer defect, and falling back to a search would be the emitter deciding whether the build survives —
    /// the exact arrangement this change removes everywhere else.
    /// </summary>
    T RequiredRef<T>(JsonElement node, string carrier, string kind) where T : MemberInfo =>
        PrimaryFromRef(node, carrier) as T
            ?? throw new InvalidOperationException(
                $"ilemit: {kind} carries no resolved `{carrier}`. The members a construction builds through are "
                + "named by the pass that minted it; a node without one is an earlier-layer drop (#370)");

    /// <summary>The member a reference names, resolved exactly. Never a search.</summary>
    MemberInfo ResolveMemberRef(MemberRefNode reference)
    {
        // Whatever this returns came from a reference; record it before anything wraps or anchors it.
        var owner = OpenOwnerOf(reference);
        MemberInfo found = reference.Kind == MemberRefNode.Kinds.Field
            ? MatchField(owner, reference)
            : (MemberInfo)MatchMethodBase(owner, reference);
        return Sanction(found);
    }

    /// <summary>The DECLARING type, resolved in the assembly the reference names — never searched for.</summary>
    Type OpenOwnerOf(MemberRefNode reference)
    {
        var declaring = (TypeNode.Fqn)reference.DeclaringType;
        try
        {
            return _target.ResolveType(declaring.Name, reference.Assembly);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ilemit: the declaring type of {reference.Describe()} is absent from the target reference set: {ex.Message}", ex);
        }
    }

    // DeclaredOnly, always: the reference states which type DECLARES the member, so an inherited namesake on
    // that type is a different member and must not be a candidate. This is also why no base walk or interface
    // fan-out appears anywhere in this file.
    const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    MethodBase MatchMethodBase(Type owner, MemberRefNode reference)
    {
        var candidates = new List<MethodBase>();
        if (reference.Kind == MemberRefNode.Kinds.Ctor)
            candidates.AddRange(owner.GetConstructors(DeclaredMembers));
        else
            candidates.AddRange(owner.GetMethods(DeclaredMembers));
        var hits = candidates.Where(m => SignatureEquals(m, reference)).ToList();
        if (hits.Count == 1) return hits[0];
        throw Mismatch(reference, owner, hits.Count, candidates.Where(m => m.Name == reference.Name));
    }

    FieldInfo MatchField(Type owner, MemberRefNode reference)
    {
        var candidates = owner.GetFields(DeclaredMembers);
        var hits = candidates.Where(field => field.Name == reference.Name
                && TypeRefEquals(reference.ReturnType, field.FieldType,
                    field.GetRequiredCustomModifiers(), field.GetOptionalCustomModifiers()))
            .ToList();
        if (hits.Count == 1) return hits[0];
        throw Mismatch(reference, owner, hits.Count, candidates.Where(field => field.Name == reference.Name));
    }

    bool SignatureEquals(MethodBase member, MemberRefNode reference)
    {
        if (!string.Equals(member.Name, reference.Name, StringComparison.Ordinal)) return false;
        if ((member.IsGenericMethod ? member.GetGenericArguments().Length : 0) != reference.GenericArity) return false;
        bool vararg = (member.CallingConvention & CallingConventions.VarArgs) != 0;
        var convention = vararg
            ? (member.IsStatic ? MemberRefNode.VarargStatic : MemberRefNode.VarargInstance)
            : (member.IsStatic ? MemberRefNode.Static : MemberRefNode.Instance);
        if (!string.Equals(convention, reference.CallingConvention, StringComparison.Ordinal)) return false;
        var parameters = member.GetParameters();
        var declared = reference.ParameterTypes ?? System.Array.Empty<TypeNode>();
        if (parameters.Length != declared.Length) return false;
        for (int i = 0; i < parameters.Length; i++)
            if (!TypeRefEquals(declared[i], parameters[i].ParameterType,
                    parameters[i].GetRequiredCustomModifiers(), parameters[i].GetOptionalCustomModifiers()))
                return false;
        if (member is MethodInfo method)
        {
            var returnParameter = method.ReturnParameter;
            if (!TypeRefEquals(reference.ReturnType, method.ReturnType,
                    returnParameter?.GetRequiredCustomModifiers() ?? Type.EmptyTypes,
                    returnParameter?.GetOptionalCustomModifiers() ?? Type.EmptyTypes))
                return false;
        }
        return true;
    }

    // A slot's declared type WITH its modifiers. Reflection reports modifiers as two sets beside the type;
    // the reference nests them around it, in the same order, so the comparison unwraps and matches sets.
    bool TypeRefEquals(TypeNode declared, Type actual, Type[] required, Type[] optional)
    {
        var wantRequired = new List<TypeNode>();
        var wantOptional = new List<TypeNode>();
        while (declared is TypeNode.Mod mod)
        {
            (mod.Req ? wantRequired : wantOptional).Add(mod.M);
            declared = mod.Of;
        }
        if (!ModifiersEqual(wantRequired, required) || !ModifiersEqual(wantOptional, optional)) return false;
        return TypeRefEquals(declared, actual);
    }

    bool ModifiersEqual(List<TypeNode> declared, Type[] actual)
    {
        if (declared.Count != actual.Length) return false;
        // Reflection's two lists are SETS, so membership is what can be compared; order between them is not a
        // fact either side reliably has.
        var remaining = actual.ToList();
        foreach (var want in declared)
        {
            int index = remaining.FindIndex(a => TypeRefEquals(want, a));
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }
        return true;
    }

    bool TypeRefEquals(TypeNode declared, Type actual)
    {
        switch (declared)
        {
            case TypeNode.ByRef byRef:
                return actual.IsByRef && TypeRefEquals(byRef.Of, actual.GetElementType());
            case TypeNode.Ptr pointer:
                return actual.IsPointer && TypeRefEquals(pointer.Of, actual.GetElementType());
            case TypeNode.Array array:
            {
                if (!actual.IsArray) return false;
                bool sz; try { sz = actual.IsSZArray; } catch { sz = true; }
                int rank; try { rank = actual.GetArrayRank(); } catch { rank = 1; }
                // `T[]` and `T[*]` are different types; the reference says which by whether it states a rank.
                if (array.SzArray != sz || (!array.SzArray && array.Rank != rank)) return false;
                return TypeRefEquals(array.Elem, actual.GetElementType());
            }
            case TypeNode.Tv variable:
                if (!actual.IsGenericParameter) return false;
                bool method = actual.DeclaringMethod != null;
                return (variable.Scope == "method") == method
                    && variable.I == actual.GenericParameterPosition;
            case TypeNode.Nullable nullable:
                return actual.IsGenericType && !actual.IsGenericTypeDefinition
                    && actual.GetGenericTypeDefinition()?.FullName == "System.Nullable`1"
                    && TypeRefEquals(nullable.Of, actual.GetGenericArguments()[0]);
            case TypeNode.Fqn named:
            {
                if (named.Args is { Length: > 0 })
                {
                    if (!actual.IsGenericType || actual.IsGenericTypeDefinition) return false;
                    var definition = actual.GetGenericTypeDefinition();
                    if (!NameEquals(named.Name, definition)) return false;
                    var arguments = actual.GetGenericArguments();
                    if (arguments.Length != named.Args.Length) return false;
                    for (int i = 0; i < arguments.Length; i++)
                        if (!TypeRefEquals(named.Args[i], arguments[i])) return false;
                    return true;
                }
                return NameEquals(named.Name, actual);
            }
            default:
                throw new NotSupportedException(
                    $"ilemit: a member reference signature cannot contain a `{declared?.GetType().Name}` type (#370)");
        }
    }

    // A signature LEAF is compared by the target universe's own identity, unlike the declaring type, which is
    // pinned to a stated assembly. That asymmetry is deliberate: a leaf's scope is whatever the universe
    // resolves it to, exactly as a TypeRef in real metadata resolves, and pinning leaves would make a
    // reference miss whenever a contract and its implementation disagree about which assembly owns a name.
    bool NameEquals(string name, Type actual)
    {
        if (actual == null) return false;
        // `void` is the ONE name inside a reference that is not the target's own spelling — the document's
        // canonical void, which the spec names as the single exception because it can appear nowhere else in
        // a signature and so makes no member ambiguous. Asking the universe for "void" asks it for a type
        // nothing declares, so it is answered here.
        if (name == "void") return string.Equals(actual.FullName, "System.Void", StringComparison.Ordinal);
        // EVERY other leaf goes through the universe, and a matching FullName is NOT enough to shortcut it:
        // two references can define the same full name, which is exactly the ambiguity the universe refuses.
        // Accepting on the name alone would route around that refusal and let a member whose parameter comes
        // from one assembly match a reference meaning the other.
        Type resolved;
        try { resolved = _target.ResolveType(name); } catch { return false; }
        return ReferenceEquals(resolved, actual) || resolved == actual;
    }

    InvalidOperationException Mismatch(MemberRefNode reference, Type owner, int hitCount, IEnumerable<MemberInfo> sameName)
    {
        var sb = new StringBuilder();
        sb.Append("ilemit: ").Append(hitCount == 0
            ? "no member of the target reference set matches "
            : $"{hitCount} members of the target reference set match ");
        sb.Append(reference.Describe());
        sb.Append(hitCount == 0
            ? " — the document and the reference set disagree about this member."
            : " — two declarations share a signature this reference cannot tell apart.");
        var named = sameName.Take(8).ToList();
        if (named.Count > 0)
        {
            sb.Append(" Declared on ").Append(owner.FullName).Append(" under that name: ");
            sb.Append(string.Join("; ", named.Select(m => m.ToString())));
        }
        return new InvalidOperationException(sb.ToString());
    }

    // ---- transitional: prove the reference names what the emitter links today ------------------------

    /// <summary>
    /// The member this node's reference names, or null when it carries none. THE PRIMARY PATH: where a
    /// reference exists it is the answer, and the descriptor beside it is only still consulted so the two can
    /// be compared. A node without one is not yet migrated and falls back until it is.
    /// </summary>
    MemberInfo PrimaryFromRef(JsonElement node, string carrier)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty(carrier, out var element)
            || element.ValueKind != JsonValueKind.Object) return null;
        var reference = MemberRefNode.Read(element);
        return AnchorOnUseSite(ResolveMemberRef(reference), reference);
    }

    /// <summary>
    /// The reference names a DECLARATION, which lives on the open definition; the call is on the use site's
    /// INSTANTIATION of it, which the reference states in `declaringType.args`. Putting the declaration back
    /// on that instantiation is mechanical — it is the one thing the reference deliberately does not
    /// pre-compute, because a MemberRef's signature is the uninstantiated one (ECMA II.9.8) and the
    /// instantiation belongs to the type spec built around it.
    /// </summary>
    MemberInfo AnchorOnUseSite(MemberInfo declaration, MemberRefNode reference)
    {
        if (reference.DeclaringType is not TypeNode.Fqn { Args: { Length: > 0 } args }) return declaration;
        // A definition can only be instantiated with its OWN arity. The reference states the declarer's
        // instantiation, so a count that disagrees is not an instantiation of this declarer at all — anchoring
        // does not apply and the declaration stands, which is what a member on a non-generic declarer gets too.
        var definition = declaration.DeclaringType;
        if (definition == null || !definition.IsGenericTypeDefinition) return declaration;
        // Past this point the reference DOES state an instantiation for a generic declarer, so failing to build
        // it is not a shape anchoring does not apply to — it is a reference the target cannot honour. Emitting
        // the open declaration instead writes a generic token where a constructed one belongs: invalid IL, blamed
        // on nothing. Say which reference, and stop.
        if (definition.GetGenericArguments().Length != args.Length)
            throw new InvalidOperationException(
                $"ilemit: {reference.Describe()} instantiates its declarer with {args.Length} argument(s), but "
                + $"{definition.FullName} declares {definition.GetGenericArguments().Length}");
        Type owner;
        try { owner = ConstructedType(definition, args.Select(MapType).ToArray()); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ilemit: {reference.Describe()} cannot be anchored on its declarer's instantiation — "
                + $"{ex.GetType().Name}: {ex.Message}", ex);
        }
        if (owner == null || owner == definition) return declaration;
        // A constructed owner made entirely of target types is an ordinary reflection type: its members are
        // reachable directly, and the same metadata token identifies the declaration on it. The Anchor*
        // helpers next door are for the other case — an owner built over a TypeBuilder, which cannot be
        // reflected and needs a signature view instead.
        var reflected = ReflectOnConstructed(owner, declaration);
        if (reflected != null) return reflected;
        return declaration switch
        {
            ConstructorInfo constructor => AnchorConstructor(owner, constructor),
            MethodInfo method => AnchorMethod(owner, method),
            FieldInfo field => AnchorField(owner, field),
            _ => declaration,
        };
    }

    static MemberInfo ReflectOnConstructed(Type owner, MemberInfo declaration)
    {
        try
        {
            IEnumerable<MemberInfo> candidates = declaration switch
            {
                ConstructorInfo => owner.GetConstructors(DeclaredMembers),
                MethodInfo => owner.GetMethods(DeclaredMembers),
                FieldInfo => owner.GetFields(DeclaredMembers),
                _ => null,
            };
            if (candidates == null) return null;
            foreach (var candidate in candidates)
                if (candidate.MetadataToken == declaration.MetadataToken && candidate.Module == declaration.Module)
                    return candidate;
        }
        catch { }
        return null;
    }

    // A signature view is a description of a member on a constructed owner, not a member the metadata declares:
    // it has no token of its own, so a comparison has to look at the declaration it describes. Which member it
    // is and which owner it is being viewed on are separate questions, and only the first one is asked here.
    static MemberInfo UnwrapSignatureView(MemberInfo member) => member switch
    {
        SignatureMethod view => view.Declaration,
        SignatureConstructor view => view.Declaration,
        SignatureField view => view.Declaration,
        _ => member,
    };

    static string Describe(MemberInfo member)
    {
        try { return $"{member.DeclaringType?.FullName}::{member}"; }
        catch { return member?.Name ?? "<null>"; }
    }
}
