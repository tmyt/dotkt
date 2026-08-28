using System;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: AssemblyMetadata("DotKt.Compiler", "metadata-v1")]

namespace DotKt.Runtime.CompilerServices
{
    // IsDotKtEmittedAssembly deliberately requires an assembly-local compiler carrier in addition to the marker.
    // Emitted DotKt DLLs embed this carrier; the fixture must do the same so its compiler metadata is trusted.
    [CompilerGenerated]
    internal sealed class KotlinFileClassAttribute : Attribute { }
}

namespace DotKt.Tests
{
    // This assembly is a compiler-owned reference-metadata fixture, not a supported user-authored annotation use.
    // It consumes the compiler stdlib's actual binding-annotation definitions. Only the assembly-local provenance
    // carrier above is embedded, matching normal DotKt output where every emitted assembly owns that carrier.
    public interface IntrinsicPhysicalSlots : IntrinsicBaseSurface
    {
        void Numeric(int value);

        // A wrong name-only selection for select(String) must remain resolvable, so the regression cannot pass merely
        // because later CLR slot lookup rejects the mismatched Numeric(Int32) signature and tries another override edge.
        void Numeric(string value);

        void Text(string value);

        void NonGeneric(int value);

        void Generic<T>(int value);
    }

    public interface IntrinsicBaseSurface
    {
        void select(int value);
        void select(string value);
        void transform(int value);
        void transform<T>(int value);
    }

    [kotlin.clr.ClrTypeAlias("DotKt.Tests.IntrinsicPhysicalSlots")]
    public interface IntrinsicDerivedSurface : IntrinsicBaseSurface
    {
        [kotlin.clr.ClrIntrinsic("Numeric")]
        new void select(int value);

        [kotlin.clr.ClrIntrinsic("Text")]
        new void select(string value);

        [kotlin.clr.ClrIntrinsic("NonGeneric")]
        new void transform(int value);

        [kotlin.clr.ClrIntrinsic("Generic")]
        new void transform<T>(int value);
    }

    public interface GenericPhysicalSlots<T> : GenericBaseSurface<T>
    {
        void Value(T value);

        // Keeps a wrong unclosed-owner selection resolvable, just like Numeric(String) above.
        void Text(string value);
    }

    public interface GenericBaseSurface<T>
    {
        void select(T value);
        void select(string value);
    }

    [kotlin.clr.ClrTypeAlias("DotKt.Tests.GenericPhysicalSlots")]
    public interface GenericDerivedSurface<T> : GenericBaseSurface<T>
    {
        [kotlin.clr.ClrIntrinsic("Value")]
        new void select(T value);

        [kotlin.clr.ClrIntrinsic("Text")]
        new void select(string value);
    }
}
