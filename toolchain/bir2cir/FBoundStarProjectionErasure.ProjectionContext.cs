using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    sealed record ProjectionDeclaration(string Kind, bool SourceGeneric, string[] Variances,
        NullableRepresentationFrame Frame);

    // Declaration readers change at the owning frontier; representation policy
    // does not. Current definitions and retained logical alias headers have
    // distinct, producer-owned membership and precedence.
    internal sealed class CarrierProjectionContext
    {
        readonly Func<string, ProjectionDeclaration> declaration;
        internal CurrentDeclarationPreparation Preparation { get; }
        internal IReadOnlyDictionary<string, Owner> Owners { get; }
        internal ReferenceMetadataIndex References { get; }
        internal GenericRepresentationPolicy Values { get; }
        internal NativeProofPlan NativeProofs { get; }
        internal IReadOnlyDictionary<string, string> LocalClrAliases { get; }

        CarrierProjectionContext(CurrentDeclarationPreparation preparation, IReadOnlyDictionary<string, Owner> owners,
            ReferenceMetadataIndex references, GenericRepresentationPolicy values, NativeProofPlan nativeProofs,
            IReadOnlyDictionary<string, string> aliases, Func<string, ProjectionDeclaration> declaration)
        {
            Preparation = preparation; Owners = owners; References = references;
            Values = values; NativeProofs = nativeProofs; LocalClrAliases = aliases;
            this.declaration = declaration;
        }

        internal static CarrierProjectionContext ForSource(CarrierDemandPlan demand, ReferenceMetadataIndex references,
            AliasDeclarationTransitions aliases = null)
        {
            var preparation = demand.Preparation;
            ProjectionDeclaration Read(string name)
            {
                if (preparation.Bindings.SourceDefinitions.TryGetValue(name, out var current))
                    return ReadSource(AliasDeclarationTransitions.ReadLogicalHeaderFacts((IBirTypeDeclaration)current.Source));
                return aliases != null && aliases.TryRetiredLogicalHeader(name, out var retired) ? ReadSource(retired) : null;
            }
            return new(preparation, demand.Owners, references, demand.Values, demand.NativeProofs,
                preparation.LocalClrAliases, Read);
        }

        internal static CarrierProjectionContext ForDom(IReadOnlyDictionary<string, Owner> owners,
            ReferenceMetadataIndex references, GenericRepresentationPolicy values, NativeProofPlan nativeProofs,
            IReadOnlyDictionary<string, string> aliases = null) => new(null, owners, references, values, nativeProofs,
                aliases, name => values?.LocalDeclaration(name) is JsonObject current ? new(
                    Str(current["kind"]), IsSourceGeneric(current),
                    (current["typeParams"] as JsonArray)?.Select(parameter => Str((parameter as JsonObject)?["variance"])).ToArray(),
                    KotlinSupertypesRecord.ReadNullableFrame(current)) : null);

        static ProjectionDeclaration ReadSource(AliasDeclarationTransitions.LogicalHeaderFacts header) => new(
            header.Kind.OrDefault(), IsSourceGeneric(header.Name.OrDefault(), header.Generated.OrDefault(),
                header.Attrs.OrEmpty().Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" }),
                header.TypeParams.OrEmpty().Length),
            header.TypeParams.HasValue ? header.TypeParams.Value.Select(parameter => parameter.Variance.OrDefault()).ToArray() : null,
            header.KotlinSupertypesPre.OrDefault()?.NullableFrame);

        void RequireSourceClock()
        {
            if (Preparation?.Bindings.IsBound == true)
                throw new InvalidOperationException("Source carrier projection crossed its owning frontier");
        }

        ProjectionDeclaration LocalDeclaration(string name) => declaration(name);

        internal NullableRepresentationFrame OwnerFrame(Owner owner) => owner.DefinitionBinding is { IsPhysical: false } source
            ? NullableRepresentationViews.TypeFacts(source.Source)?.NullableFrame
            : KotlinSupertypesRecord.ReadNullableFrame(owner.Def);

        internal TypeNode Rewrite(TypeNode type, bool boundDeclaration = false,
            bool preserveConstructedHead = false, bool preserveArrayShape = false)
        {
            RequireSourceClock();
            return RewriteType(type, this, boundDeclaration, preserveConstructedHead, preserveArrayShape);
        }

        internal TypeNode ProjectOrdinaryParameter(TypeNode type)
        {
            RequireSourceClock();
            return FBoundStarProjectionErasure.ProjectOrdinaryParameter(type, this);
        }

        internal bool IsCapturedInner(TypeNode type)
        {
            RequireSourceClock();
            return FBoundStarProjectionErasure.IsCapturedInner(type, Owners, References);
        }

        internal bool ProjectionCanChangeConstruction(TypeNode.Fqn application) =>
            LocalDeclaration(application.Name) is { } local
                ? HasKotlinVariantParameter(application, local.Variances)
                : HasKotlinVariantParameter(application, References.OwnerTypeParametersInPhysicalFrame(application.Name)
                    ?.Select(parameter => parameter.Variance.OrDefault()).ToArray());

        internal bool RequiresOpaqueVariantAlias(TypeNode.Fqn application)
        {
            var local = LocalDeclaration(application.Name);
            return AliasVarianceRepresentation.RequiresErasureFromSource(application, References, local?.Variances,
                Values?.BindingAlias(application.Name) ?? LocalClrAliases?.GetValueOrDefault(application.Name), local?.Frame);
        }

        internal bool RequiresKotlinValueCarrier(TypeNode.Fqn application)
        {
            if (application.Args is not { Length: > 0 } || Values?.IsBindingAlias(application.Name) == true) return false;
            if (LocalDeclaration(application.Name) is { } local)
                return local.Kind is "class" or "interface" && local.SourceGeneric && Values.OwnsValueSlots(application.Name);
            if (IsCapturedInner(application)) return true;
            if (!References.HasDotKtOwner(application.Name) || !Values.OwnsValueSlots(application.Name)) return false;
            var physicalOwner = References.TryExactPhysicalTypeName(application.Name, application.Args.Length, out var exact)
                && exact != null ? exact : application.Name;
            var reflected = References.ResolveRefType(physicalOwner,
                physicalOwner.Contains('`') || physicalOwner.Contains('+') ? 0 : application.Args.Length);
            return reflected != null && !reflected.IsValueType && !typeof(MulticastDelegate).IsAssignableFrom(reflected);
        }

        internal bool TryExistentialCarrier(string name, out string physicalOwner)
        {
            if (Owners.TryGetValue(name, out var local)) { physicalOwner = local.ErasedName; return local.Needed; }
            if (LocalDeclaration(name) != null) { physicalOwner = null; return false; }
            return References.TryExistentialPhysicalOwner(name, out physicalOwner);
        }
    }

    static bool PreservesNativeWitnessHead(TypeNode type, CarrierProjectionContext context) =>
        StripSourceNullability(type) is TypeNode.Fqn component
        && !ContainsExistentialProjection(component)
        && context.RequiresKotlinValueCarrier(component)
        && !context.ProjectionCanChangeConstruction(component);

    static TypeNode ProjectOrdinaryParameter(TypeNode type, CarrierProjectionContext context) => type switch {
        TypeNode.Nullable nullable => new TypeNode.Nullable(ProjectOrdinaryParameter(nullable.Of, context)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ProjectOrdinaryParameter(oblivious.Of, context)),
        TypeNode.Fqn { Args: { Length: > 0 } } named
            when (!context.Owners.TryGetValue(named.Name, out var local) || local.Kind is "class" or "interface")
                && !context.References.IsValueType(named)
                && context.TryExistentialCarrier(named.Name, out var carrier) => new TypeNode.Fqn(carrier),
        _ => type,
    };

    static bool TryWritableVariantArrayElement(TypeNode element, CarrierProjectionContext context,
        out TypeNode.Fqn semanticElement, out TypeNode.Fqn physicalElement)
    {
        semanticElement = StripSourceNullability(element) as TypeNode.Fqn;
        physicalElement = null;
        if (semanticElement?.Args is not { Length: > 0 } || !context.ProjectionCanChangeConstruction(semanticElement)
            || !context.TryExistentialCarrier(semanticElement.Name, out var carrier)) return false;
        physicalElement = new TypeNode.Fqn(carrier);
        return true;
    }
}
