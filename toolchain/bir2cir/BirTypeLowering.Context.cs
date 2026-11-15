using DotKt.Bir;

static partial class BirTypeLowering
{
    // The immutable invocation header refers to the existing authoritative maps.
    // Neither the scalar engine nor the actual/shadow projections install ambient
    // state or recollect a semantic catalog from the graph being rewritten.
    internal sealed class PhysicalTypeContext
    {
        internal bool RefBuild { get; }
        internal IReadOnlyDictionary<string, string> Aliases { get; }
        internal ValueTypeOracle IsValue { get; }
        internal IReadOnlyDictionary<string, string> PhysicalNames { get; }
        internal IReadOnlySet<string> LocalNames { get; }
        internal IReadOnlyDictionary<string, NullableRepresentationFrame> NullableFrames { get; }
        internal IReadOnlyDictionary<string, IReadOnlyList<string>> InterfaceMarkers { get; }
        internal string File { get; }

        internal PhysicalTypeContext(bool refBuild, IReadOnlyDictionary<string, string> aliases = null,
            ValueTypeOracle isValue = null, IReadOnlyDictionary<string, string> physicalNames = null,
            IReadOnlySet<string> localTypes = null,
            IReadOnlyDictionary<string, NullableRepresentationFrame> nullableFrames = null,
            IReadOnlyDictionary<string, IReadOnlyList<string>> interfaceMarkers = null, string file = null)
        {
            RefBuild = refBuild;
            Aliases = aliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
            IsValue = isValue ?? (_ => false);
            PhysicalNames = physicalNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
            LocalNames = localTypes ?? new HashSet<string>(StringComparer.Ordinal);
            NullableFrames = nullableFrames;
            InterfaceMarkers = interfaceMarkers ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            File = string.IsNullOrEmpty(file) ? "<unknown>" : file;
        }
    }

    internal enum PhysicalSlotRole { Value, Return, Owner, Delegate, GenericArgument }

    internal static TypeNode LowerSlot(TypeNode type, PhysicalTypeContext context, PhysicalSlotRole role,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        // These are positional entry rules into one recursion, not alternate
        // alias/head/native/delegate implementations. Value-returning ret slots
        // explicitly choose Value instead of acquiring Kotlin's Unit convention.
        if (role == PhysicalSlotRole.Return && type is TypeNode.Fqn { Name: "kotlin.Unit", Args: null })
            return VoidType;
        if (role == PhysicalSlotRole.Owner && type is TypeNode.Fqn { Args: null } owner
            && PrimArrayElem.ContainsKey(owner.Name)) return owner;
        if (role == PhysicalSlotRole.Delegate && type is TypeNode.Fn function)
            return LowerFnDelegate(function, context, force);
        return LowerType(type, context, force, typeArg: role == PhysicalSlotRole.GenericArgument);
    }
}
