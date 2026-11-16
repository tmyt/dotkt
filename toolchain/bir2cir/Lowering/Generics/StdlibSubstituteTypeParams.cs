using DotKt.Bir;

// #66 — kotc emits ONE substitute-INDEPENDENT BIR: the stdlib REFERENCE and RUNTIME builds get BIT-IDENTICAL type
// params (the pure-Kotlin shape, keeping a `kotlin.Comparable` upper bound and `in` declaration-site variance). The
// ref/rt divergence for those two is a SUBSTITUTION CONSEQUENCE, so it lives HERE — the RUNTIME stdlib build only
// (`--build-stdlib=runtime`, `SubstituteStdlibBuild`). It reproduces exactly what BirEmitter.typeParamsJson used to do
// under `stdlibSubstitute`:
//
//   (1) DROP every interface type-param constraint in the executable runtime CIR. Kotlin/frontend constraints remain
//       complete in the metadata/reference CIR, but reifying the same bound on a CLR interface type param is unsafe for
//       generic DIM forwarding: CoreCLR validates a nested `Key<!!E>` using its shared `object` canon before substituting
//       the forwarding method's `E : Element`, and rejects the valid MethodSpec as `Key<object>`. Method constraints are
//       untouched; only the runtime interface TYPE declaration drops the duplicate CLR constraint.
//   (2) DROP `in` (contravariant) declaration-site variance. The CLR's variance-validity check is stricter than
//       Kotlin's (e.g. `Continuation<in T>.resumeWith(Result<out T>)` — T in an input position — is rejected). Runtime
//       types don't need declaration-site variance (a compile-time concern; the ref.dll keeps it).
//
// Class and method bounds remain intact: BirTypeLowering projects them to their exact CLR declarations, including
// IComparable<T>. Removing such a bound after constrained-call selection invalidates that call's verifier proof.
static partial class StdlibSubstituteTypeParams
{
    internal static TypeParameterDeclaration Rewrite(TypeParameterDeclaration declaration, bool runtimeInterface) =>
        declaration with {
            Constraints = runtimeInterface ? null : declaration.Constraints,
            Variance = declaration.Variance == "in" ? null : declaration.Variance,
        };

    internal static void SelfTest()
    {
        var parameter = new TypeParameterDeclaration("T", Reified: true, Variance: "in",
            Constraints: [new TypeNode.Fqn("kotlin.Any")],
            ErasedInnerConstraints: [new TypeNode.Tv("type", 0)], SpecialConstraints: ["class"]);
        var physical = Rewrite(parameter, runtimeInterface: true);
        if (physical.Constraints != null || physical.Variance != null || physical.Reified != true
            || physical.ErasedInnerConstraints != parameter.ErasedInnerConstraints
            || physical.SpecialConstraints != parameter.SpecialConstraints)
            throw new System.InvalidOperationException("Runtime interface projection changed unrelated generic declaration facts");
        var decoded = TypeParameterDeclaration.Read(parameter.ToDom());
        if (decoded.Name != parameter.Name || decoded.Reified != true || decoded.Variance != "in"
            || decoded.Constraints?.Single() != parameter.Constraints.Single()
            || decoded.ErasedInnerConstraints?.Single() != parameter.ErasedInnerConstraints.Single()
            || decoded.SpecialConstraints?.Single() != "class")
            throw new System.InvalidOperationException("Generic declaration boundary changed current declaration facts");
        System.Console.WriteLine("[runtime generic bounds] self-test OK (shared declaration planner and current metadata facts)");
    }
}
