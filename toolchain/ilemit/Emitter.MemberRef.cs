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
using System.Text;
using System.Text.Json;
using DotKt.Bir;

sealed partial class Emitter
{
    /// <summary>How many references were resolved, and how many were checked against the legacy path.</summary>
    int _memberRefResolved, _memberRefParityChecked, _memberRefCarriers, _memberRefUncovered;

    /// <summary>Every key a resolved member reference can ride on. The counting below is keyed on this set.</summary>
    static readonly string[] MemberRefCarriers =
        { "memberRef", "baseCtorRef", "clrOverrideRef", "ctorRef", "addRef", "setItemRef" };

    /// <summary>
    /// Count the references the DOCUMENTS carry, so the parity check can be held to covering all of them.
    ///
    /// A check wired site by site proves whatever those sites happen to reach, and says nothing about the
    /// ones nobody wired — which is the failure this whole issue is about, one layer up. Counting both sides
    /// makes the check answerable: if a carrier exists that no consumer verifies, the totals disagree and the
    /// build stops, naming the gap instead of quietly reporting a number that means less than it reads.
    /// </summary>
    void CountMemberRefCarriers(IEnumerable<JsonElement> files)
    {
        foreach (var file in files) CountCarriers(file);
    }

    void CountCarriers(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (System.Array.IndexOf(MemberRefCarriers, property.Name) >= 0
                        && property.Value.ValueKind == JsonValueKind.Object)
                        _memberRefCarriers++;
                    CountCarriers(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) CountCarriers(item);
                break;
        }
    }

    /// <summary>
    /// Report how much of what the documents carry this build actually exercised.
    ///
    /// This is a MEASUREMENT, not the gate, and the difference matters. "Every reference in the document was
    /// checked" is not an invariant any build satisfies: a metadata-only build squashes bodies, so the calls
    /// those references describe are never emitted and there is no legacy resolution to compare against —
    /// nothing is unproven there, it is simply unused. The property that IS enforced lives at the call sites:
    /// every legacy resolver that produces a member for a node carrying a reference compares the two first,
    /// unconditionally. A count cannot state that; wiring does. What the count is good for is saying how much
    /// of the corpus a given build put through it, which is worth printing and worth watching move.
    /// </summary>
    void ReportParityCoverage()
    {
        if (_memberRefParityOff || _memberRefCarriers == 0) return;
        Console.Error.WriteLine(
            $"ilemit: member-reference parity checked {_memberRefParityChecked} of the {_memberRefCarriers} "
            + $"reference(s) these documents carry ({_memberRefResolved} resolved, "
            + $"{_memberRefUncovered} legacy resolution(s) had no reference to compare).");

        // NOTHING IS ASSERTED HERE, and the honest reason is worth writing down, because two assertions were
        // tried and both were worse than none.
        //
        // "checks == carriers" is not an invariant any build satisfies: a metadata-only build squashes bodies, so
        // references it carries are never consumed, and a node the emitter visits twice has its reference checked
        // twice. It would redden on correct builds in both directions.
        //
        // "resolved == checked" IS always true — and vacuously so, because both counters are incremented on the
        // same straight line through ShadowParity. It cannot fail, so it proves nothing; it merely reads as if it
        // did, which is worse than silence.
        //
        // Coverage is enforced by WIRING — a legacy resolver that produces a member for a node carrying a
        // reference compares them first — and MEASURED by the uncovered count above, which is the population
        // still resolved by name. A count cannot state a wiring property. Reporting the number and naming what it
        // does not cover is the strongest true thing available here.
    }

    /// <summary>The member a reference names, resolved exactly. Never a search.</summary>
    MemberInfo ResolveMemberRef(MemberRefNode reference)
    {
        var owner = OpenOwnerOf(reference);
        MemberInfo found = reference.Kind == MemberRefNode.Kinds.Field
            ? MatchField(owner, reference)
            : (MemberInfo)MatchMethodBase(owner, reference);
        _memberRefResolved++;
        return found;
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
    /// Resolve the reference beside the legacy pick and refuse to continue if they name different members.
    /// Runs over every build, which is the only way the claim is about the corpus rather than about a few
    /// examples. Disable with DOTKT_MEMBERREF_PARITY=off only to bisect.
    /// </summary>
    void ShadowParity(JsonElement node, string carrier, MemberInfo legacy, string context)
    {
        if (_memberRefParityOff || legacy == null) return;
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty(carrier, out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            // A legacy resolver produced an EXTERNAL member for a node that carries no reference: that site's
            // producer is the one still unproven. Counting it is what makes "every producer is covered" a number
            // rather than an impression — the claim that failed review last time was exactly this one, asserted
            // from wiring that reached three sites out of nine.
            //
            // A member being BUILT by this same compilation is not in that population: it has no assembly
            // identity to reference yet, and the sites below resolve local and external members through one
            // path. Counting those would bury the number that matters under the whole local corpus.
            if (!IsUnderConstruction(legacy)) _memberRefUncovered++;
            return;
        }
        MemberRefNode reference;
        try { reference = MemberRefNode.Read(element); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"ilemit: {context} carries a malformed {carrier}: {ex.Message}", ex);
        }
        var resolved = ResolveMemberRef(reference);
        _memberRefParityChecked++;
        if (SameDeclaration(resolved, legacy)) return;
        throw new InvalidOperationException(
            $"ilemit: {context}: the resolved {carrier} and the descriptor it travels with name DIFFERENT members. "
            + $"reference -> {Describe(resolved)}; descriptor -> {Describe(legacy)}; reference = {reference.Describe()}");
    }

    /// <summary>
    /// True when this member is a definition THIS compilation is emitting, rather than one it references.
    /// Reflection.Emit says so structurally: a member being built lives in a ModuleBuilder.
    /// </summary>
    static bool IsUnderConstruction(MemberInfo member)
    {
        try { return UnwrapSignatureView(member)?.Module is System.Reflection.Emit.ModuleBuilder; }
        catch { return false; }
    }

    static bool SameDeclaration(MemberInfo a, MemberInfo b)
    {
        a = UnwrapSignatureView(a);
        b = UnwrapSignatureView(b);
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        // The legacy path may hand back a member reflected THROUGH a constructed owner; the reference always
        // names the declaration. Comparing metadata identity is what makes those two comparable at all.
        try { return a.Module == b.Module && a.MetadataToken == b.MetadataToken; }
        catch { return false; }
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

    static readonly bool _memberRefParityOff =
        string.Equals(Environment.GetEnvironmentVariable("DOTKT_MEMBERREF_PARITY"), "off", StringComparison.OrdinalIgnoreCase);
}
