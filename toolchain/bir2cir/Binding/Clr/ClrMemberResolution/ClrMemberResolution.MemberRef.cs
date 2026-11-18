using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ClrMemberResolution
{
    // Temporary bir2cir-only carrier consumed by ExternalGenericConstraintValidation at the owning typed frontier,
    // after member resolution. The selected MethodDef alone owns these CLR flags/rows; ilemit never sees them.
    internal const string ResolvedMethodTypeParamsKey = "_resolvedMethodTypeParams";

    static Type[] ResolvedMethodTypeParameters(MethodInfo method, int argumentCount)
    {
        method = ClrMemberReferences.OpenDeclarationOf(method);
        if (argumentCount == 0 || !method.IsGenericMethodDefinition) return null;
        var parameters = method.GetGenericArguments();
        if (parameters.Length != argumentCount)
            throw new InvalidOperationException(
                $"bir2cir: resolved generic method '{method.DeclaringType?.FullName}.{method.Name}' declares " +
                $"{parameters.Length} type parameter(s), but the use carries {argumentCount} argument(s)");
        return parameters;
    }

    /// <summary>The scalar reference for a resolved method/constructor, ready to be stamped on a node.</summary>
    internal static MemberRefNode MemberRefOf(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, MethodBase member,
        string kind, Type openOwner, TypeNode[] ownerArgs, bool? ownerArgumentsAreMethodSlots = null)
    {
        return ClrMemberReferences.MemberRefOf(refs, bindingLocalTypes, member, kind, openOwner, ownerArgs, ownerArgumentsAreMethodSlots);
    }

    // Use the same complete projection for a native type test and its selected MemberRef.
    internal static TypeNode.Fqn PhysicalInstanceView(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, TypeNode.Fqn view)
    {
        return new TypeNode.Fqn(view.Name, view.Args.Select(argument => ClrMemberReferences.PhysicalOwnerArg(refs, bindingLocalTypes, argument, typeArg: true)).ToArray());
    }

    internal static TypeNode DeclaredMethodReturn(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, MethodInfo method)
    {
        return ClrMemberReferences.RefReturnOf(refs, bindingLocalTypes, method);
    }

    sealed record ExactDispatchPlan(string Name, MemberRefNode Reference, TypeNode[] Arguments,
        TypeNode Result, string Visibility, IReadOnlyList<BirTypeParameter> OwnerParameters);

    // Exact call renderers consume one selected MethodDef and the same
    // physical signature, argument adaptation and lexical-access metadata.
    static ExactDispatchPlan PlanExactDispatch(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, MethodInfo method,
        TypeNode.Fqn declaringOwner, bool isStatic)
    {
        if (method.IsStatic != isStatic || method.IsGenericMethod)
            throw new InvalidOperationException("Exact dispatch requires a non-generic MethodDef with matching staticness");
        var reference = ClrMemberReferences.MemberRefOf(refs, bindingLocalTypes, method, MemberRefNode.Kinds.Method, method.DeclaringType, declaringOwner.Args);
        var parameters = method.GetParameters().Select(parameter => ClrDeclarationMatching.PhysicalTypeOf(refs, bindingLocalTypes, parameter.ParameterType,
            declaringOwner.Args, typeArg: false)).ToArray();
        // This result is already closed in the caller's owner frame. Both
        // renderers stamp ret AND sty to prevent a second substitution; the
        // MemberRef retains the selected MethodDef's open signature.
        var result = ClrDeclarationMatching.PhysicalTypeOf(refs, bindingLocalTypes, method.ReturnType, declaringOwner.Args, typeArg: false);
        var visibility = method.IsPrivate ? "private" : method.IsAssembly ? "internal"
            : method.IsFamily || method.IsFamilyOrAssembly || method.IsFamilyAndAssembly ? "protected" : null;
        return new(method.Name, reference, parameters, result, visibility, visibility == null ? null
            : refs.OwnerTypeParametersInPhysicalFrame(((TypeNode.Fqn)reference.DeclaringType).Name));
    }

    internal static MemberRefNode ArrayStorageMethodOf(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, string name, string[] parameters,
        string ownerName = "System.Array", bool isStatic = false)
        => PlanArrayStorageCall(bindingLocalTypes, refs, name, parameters, ownerName, isStatic).Reference;

    internal sealed record ArrayStorageCall(MemberRefNode Reference, string Dispatch);

    // Native vocabulary already carries its final dispatch. Select it from the
    // same MethodDef as the reference, rather than relying on a later resolver.
    internal static ArrayStorageCall PlanArrayStorageCall(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, string name, string[] parameters,
        string ownerName = "System.Array", bool isStatic = false)
    {
        var owner = refs.ResolveRefType(ownerName);
        var method = owner.GetMethods((isStatic ? BindingFlags.Static : BindingFlags.Instance)
                | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == name && m.GetParameters().Select(p => p.ParameterType.FullName)
                .SequenceEqual(parameters));
        return new(ClrMemberReferences.MemberRefOf(refs, bindingLocalTypes, method, "method", owner, null), isStatic ? "call" : Dispatch(method, owner, false));
    }

    /// <summary>
    /// The reference for a COMPILER-AUTHORED delegation to an external parameterless constructor — the base
    /// call in a synthesized attribute class. The compiler names that member itself rather than reading it
    /// off a Kotlin program, and a member the compiler names is still a member of another assembly: it gets
    /// the same complete identity, resolved here once, instead of a hand-written owner and an empty vector.
    /// </summary>
    internal static MemberRefNode ParameterlessBaseCtorRef(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex refs, string ownerFqn)
    {
        // Resolve the synthesized base against this compilation's reference universe.
        refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var owner = new TypeNode.Fqn(ownerFqn);
        var open = ResolveOwnerType(owner, refs: refs)
            ?? throw new InvalidOperationException($"bir2cir: synthesized base owner '{ownerFqn}' does not resolve to a .NET type");
        // Protected is the usual shape for an abstract base's constructor (System.Attribute's is), so the probe
        // must see non-public declarations; the parameter count is what selects, and one match is required.
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == 0 && IsPublicOrProtected(c)).ToList();
        if (ctors.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: '{ownerFqn}' has {ctors.Count} parameterless constructors; a synthesized delegation needs exactly one");
        return ClrMemberReferences.MemberRefOf(refs, bindingLocalTypes, ctors[0], MemberRefNode.Kinds.Ctor, open, Array.Empty<TypeNode>());
    }

    internal static TypeNode DeclaringTypeRef(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences, MemberInfo member, Type openOwner, TypeNode[] ownerArgs,
        bool? ownerArgumentsAreMethodSlots = null)
        => ClrMemberReferences.DeclaringTypeRef(bindingReferences, bindingLocalTypes, member, openOwner, ownerArgs, ownerArgumentsAreMethodSlots);

    static bool IsDelegate(Type t)
    {
        try
        {
            for (var b = t.BaseType; b != null; b = b.BaseType)
                if (b.FullName == "System.MulticastDelegate") return true;
        }
        catch { /* a reference whose base chain does not load is simply not a delegate here */ }
        return false;
    }
}
