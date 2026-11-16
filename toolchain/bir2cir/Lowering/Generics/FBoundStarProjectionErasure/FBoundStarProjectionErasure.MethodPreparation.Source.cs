using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void PrepareMethodBounds(CurrentDeclarationBindings bindings, ReferenceMetadataIndex references)
    {
        OwnerConstrainedMethodLowering.BeginSourceMethodPreparation(bindings, references);
        // This clock precedes field-accessor authoring. Keep its exact current
        // definitions+file membership rather than the earlier selection index.
        foreach (var owner in bindings.SourceDefinitions.Values.Concat(bindings.SourceRoots))
        {
            var methods = owner.Source switch {
                IBirTypeDeclaration type => type.Methods.OrEmpty(),
                BirFileDeclaration file => file.Methods.OrEmpty(), _ => Array.Empty<BirFunctionDeclaration>(),
            };
            foreach (var method in methods)
                if (method.TypeParams.OrEmpty().Any(parameter => parameter.Constraints.OrEmpty().Any(ContainsOwnerTv))
                    || OwnerConstrainedMethodLowering.HasMethodDependentBounds(method, references)
                    || method._ownerConstraintOverrideBounds.HasValue)
                    OwnerConstrainedMethodLowering.Record(method, owner.Source, bindings);
        }
    }

    internal sealed class SourceMethodSignatures
    {
        Dictionary<CurrentDeclarationBindings.DeclarationBinding, TypeNode[]> signatures;
        internal SourceMethodSignatures(CurrentDeclarationBindings bindings)
        {
            signatures = new();
            // Accessors have now been appended. Bind every actual method,
            // including these ID-less records, without extending LocalMethods.
            foreach (var owner in bindings.SourceDefinitions.Values)
            foreach (var method in ((IBirTypeDeclaration)owner.Source).Methods.OrEmpty())
                signatures.Add(bindings.GetSource(method), method.Params.OrEmpty()
                    .Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray());
        }

        // Read the captured row, not the declaration's now-projected Params.
        // Consumers treat this source signature as immutable until completion.
        internal TypeNode[] GetSourceSignature(CurrentDeclarationBindings.DeclarationBinding declaration)
        {
            if (signatures == null) throw new InvalidOperationException("Source method signatures already handed off");
            return declaration != null && signatures.TryGetValue(declaration, out var signature) ? signature
                : throw new InvalidOperationException("The declaration has no captured source signature");
        }

        internal void CompleteSourceLifetime()
        {
            if (signatures == null) throw new InvalidOperationException("Source method signatures already handed off");
            signatures = null;
        }
    }

    internal static SourceMethodSignatures CaptureSourceSignatures(CurrentDeclarationBindings bindings) => new(bindings);
}
