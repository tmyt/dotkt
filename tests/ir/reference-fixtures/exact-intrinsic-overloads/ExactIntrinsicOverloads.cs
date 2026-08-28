using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyMetadata("DotKt.Compiler", "metadata-v1")]

namespace DotKt.Tests
{
    [CompilerGenerated]
    internal static class FixtureProvenance { }

    // This assembly is a compiler-owned reference-metadata fixture, not a supported user-authored annotation use.
    // It consumes the compiler stdlib's actual annotation definitions and carries compiler provenance; defining FQN
    // lookalikes here would introduce a second type identity into every lowering fixture's reference universe.
    public interface IntrinsicPhysicalSlots : IntrinsicBaseSurface
    {
        void Numeric(int value);

        // A wrong name-only selection for select(String) must remain resolvable, so the regression cannot pass merely
        // because later CLR slot lookup rejects the mismatched Numeric(Int32) signature and tries another override edge.
        void Numeric(string value);

        void Text(string value);
    }

    public interface IntrinsicBaseSurface
    {
        void select(int value);
        void select(string value);
    }

    [kotlin.clr.ClrTypeAlias("DotKt.Tests.IntrinsicPhysicalSlots")]
    public interface IntrinsicDerivedSurface : IntrinsicBaseSurface
    {
        [kotlin.clr.ClrIntrinsic("Numeric")]
        new void select(int value);

        [kotlin.clr.ClrIntrinsic("Text")]
        new void select(string value);
    }
}
