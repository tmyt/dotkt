using DotKt.Bir;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    internal sealed partial class NativeProofPlan
    {
        readonly Dictionary<DeclarationBinding, Dictionary<int, List<TypeNode>>> lateSourceTypes = new();

        internal void RegisterCurrentSourceDefinitions(CurrentPhysicalTypeView current)
        {
            foreach (var (name, actual) in current.Bindings)
            {
                if (definitions.ContainsKey(name)) continue;
                // Same physical-only interpretation as RegisterPhysicalLocalDefinitions;
                // do not rerun source demand or extend original declaration catalogs.
                var physical = ReadRows(Constraints(actual), false, Frame(actual), physicalDeclaration: true);
                lateSourceTypes[actual] = physical;
                lateLocalTypes[name] = physical;
            }
        }
    }
}
