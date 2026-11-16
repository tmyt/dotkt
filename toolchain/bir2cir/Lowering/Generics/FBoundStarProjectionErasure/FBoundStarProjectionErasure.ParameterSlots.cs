static partial class FBoundStarProjectionErasure
{
    // A source Kotlin method consumes an erased generic view, not proof of one CLR construction.
    // Constructors, property accessors, generated linkage methods and native overrides retain their contracts.
    static bool OrdinaryKotlinParameterPolicy(bool generatedMethod, bool generatedOwner, bool aliasOwner, bool intrinsic,
        IEnumerable<string> overrideOwners, Func<string, bool?> localAlias, ReferenceMetadataIndex refs)
    {
        if (generatedMethod || generatedOwner || aliasOwner || intrinsic) return false;
        foreach (var name in overrideOwners)
        {
            if (name == null) continue;
            if (localAlias(name) is { } alias)
            {
                if (alias) return false;
            }
            else if (!refs.HasDotKtOwner(name) || refs.TryResolveClrOwner(name, out _, out _)) return false;
        }
        return true;
    }

}
