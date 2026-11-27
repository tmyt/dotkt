using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// dll2klib restores a compiler-generated existential's Kotlin surface (physical carrier -> `G<*>`) before frontend
// analysis. That is the correct source signature, but a cross-module CIR call still has to name the referenced DLL's
// physical parameter/return slots. Rebind only provenance-verified existential signatures from the reference index,
// not the caller's local storage, which was projected from its own declaration. Metadata remains the frontend authority; this pass
// is the Kotlin-to-CLR ABI boundary and ilemit receives an exact signature.
static partial class ReferenceExistentialAbiBinding
{
    static bool ContainsPhysicalExistential(TypeNode type, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Fqn f => refs.IsExistentialPhysicalOwner(f.Name)
            || f.Args?.Any(argument => ContainsPhysicalExistential(argument, refs)) == true,
        TypeNode.Nullable nullable => ContainsPhysicalExistential(nullable.Of, refs),
        TypeNode.Oblivious oblivious => ContainsPhysicalExistential(oblivious.Of, refs),
        TypeNode.Projection projection => ContainsPhysicalExistential(projection.Of, refs),
        TypeNode.Array array => ContainsPhysicalExistential(array.Elem, refs),
        TypeNode.ByRef byRef => ContainsPhysicalExistential(byRef.Of, refs),
        TypeNode.Ptr pointer => ContainsPhysicalExistential(pointer.Of, refs),
        TypeNode.Mod modifier => ContainsPhysicalExistential(modifier.M, refs)
            || ContainsPhysicalExistential(modifier.Of, refs),
        TypeNode.Fn function => ContainsPhysicalExistential(function.Ret, refs)
            || function.Params.Any(parameter => ContainsPhysicalExistential(parameter, refs))
            || function.Recv != null && ContainsPhysicalExistential(function.Recv, refs)
            || function.Ctx?.Any(context => ContainsPhysicalExistential(context, refs)) == true,
        _ => false,
    };

    static TypeNode CloseResult(TypeNode declaration, TypeNode[] ownerArguments, TypeNode[] methodArguments) =>
        FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(declaration, ownerArguments, methodArguments);

    // A referenced alias can contain an existential argument without giving its ordinary Kotlin value an exact
    // CLR construction. Keep that enclosing alias's value ABI. Native heads and their nested signature positions
    // are exact descriptors, not ordinary value slots, so do not recursively project their arguments or arrays.
    static TypeNode ProjectAliasValueResult(TypeNode result, Func<TypeNode.Fqn, bool> opaqueAlias) => result switch
    {
        TypeNode.Nullable nullable => new TypeNode.Nullable(ProjectAliasValueResult(nullable.Of, opaqueAlias)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ProjectAliasValueResult(oblivious.Of, opaqueAlias)),
        TypeNode.Fqn alias when opaqueAlias(alias) => new TypeNode.Fqn("kotlin.Any"),
        _ => result,
    };

    internal static void SelfTest()
    {
        var owner = new TypeNode.Tv("type", 0);
        var companion = new TypeNode.Tv("method", 1);
        var callerMethod = new TypeNode.Tv("method", 1);
        var callerOwner = new TypeNode.Tv("type", 0);
        var result = new TypeNode.Fn(false, new TypeNode.Fqn("IteratorCarrier"),
            new TypeNode[] { new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { companion }) },
            owner, "System.Func", new TypeNode[] { owner });
        var closed = (TypeNode.Fn)CloseResult(result, new TypeNode[] { callerMethod },
            new TypeNode[] { new TypeNode.Fqn("System.String"), callerOwner });
        if (closed.Recv != callerMethod || closed.Ctx[0] != callerMethod || closed.Clr != result.Clr
            || ((TypeNode.Fqn)closed.Params[0]).Args[0] != callerOwner)
            throw new InvalidOperationException("Existential result retained a callee variable or substituted a caller variable twice");
        var alias = new TypeNode.Fqn("Alias", new TypeNode[] { new TypeNode.Fqn("Carrier") });
        bool Opaque(TypeNode.Fqn value) => value.Name == alias.Name;
        if (ProjectAliasValueResult(new TypeNode.Nullable(alias), Opaque)
            is not TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Any" } })
            throw new InvalidOperationException("Referenced alias result lost its nullable value-slot projection");
        foreach (var exact in new TypeNode[] { new TypeNode.Fqn("Native", new TypeNode[] { alias }),
            new TypeNode.Array(alias), new TypeNode.Fn(false, alias, new TypeNode[] { alias }) })
            if (!ReferenceEquals(ProjectAliasValueResult(exact, Opaque), exact))
                throw new InvalidOperationException("Referenced alias projection changed an exact native nested signature");
        Console.WriteLine("[existential result frame] self-test OK (nested delegate, owner/method arguments, simultaneous substitution)");
    }

}
