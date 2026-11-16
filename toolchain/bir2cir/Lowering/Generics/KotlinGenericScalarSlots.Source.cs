using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinGenericScalarSlots
{
    // A view of the actual flat source inventory, not a second IR or policy.
    internal sealed record SourceOwner(string Name, string Kind, bool IsType, BirTypeParameter[] TypeParameters,
        SourceTypeFacts Facts, BirFunctionDeclaration[] Methods, BirPropertyDeclaration[] Properties,
        BirInheritedClassMethod[] InheritedMethods);

    internal static IEnumerable<SourceOwner> SourceOwners(IEnumerable<BirFileDeclaration> roots)
    {
        foreach (var file in roots)
        {
            yield return new(file.FileClass.OrDefault(), null, false, Array.Empty<BirTypeParameter>(), null,
                file.Methods.OrEmpty(), file.Properties.OrEmpty(), Array.Empty<BirInheritedClassMethod>());
            foreach (var declaration in file.Types.OrEmpty())
                yield return declaration switch {
                    BirClassDefinition type => new(type.Name.OrDefault(), type.Kind.OrDefault(), true,
                        type.CapturedTypeParams.OrEmpty().Concat(type.TypeParams.OrEmpty()).ToArray(), type.KotlinSupertypesPre.OrDefault(),
                        type.Methods.OrEmpty(), type.Properties.OrEmpty(), type.InheritedClassMethods.OrEmpty()),
                    BirInterfaceDefinition type => new(type.Name.OrDefault(), type.Kind.OrDefault(), true,
                        type.CapturedTypeParams.OrEmpty().Concat(type.TypeParams.OrEmpty()).ToArray(), type.KotlinSupertypesPre.OrDefault(),
                        type.Methods.OrEmpty(), type.Properties.OrEmpty(), Array.Empty<BirInheritedClassMethod>()),
                    BirEnumDefinition type => new(type.Name.OrDefault(), type.Kind.OrDefault(), true,
                        type.CapturedTypeParams.OrEmpty().Concat(type.TypeParams.OrEmpty()).ToArray(), type.KotlinSupertypesPre.OrDefault(),
                        type.Methods.OrEmpty(), type.Properties.OrEmpty(), Array.Empty<BirInheritedClassMethod>()),
                    _ => throw new InvalidOperationException("Unaccounted scalar source owner: " + declaration.GetType().Name),
                };
        }
    }

    static bool Intrinsic(IEnumerable<BirAttribute> attributes) => attributes.Any(attribute =>
        attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrIntrinsic" });

    internal static Projection ApplyAll(IReadOnlyList<BirFileDeclaration> roots, GenericRepresentationPolicy policy,
        ReferenceMetadataIndex references)
    {
        var owners = SourceOwners(roots).ToArray();
        var intrinsics = owners.SelectMany(owner => owner.Methods)
            .Where(method => method.DeclarationId.HasValue && Intrinsic(method.Attrs.OrEmpty()))
            .Select(method => method.DeclarationId.Value).ToHashSet();
        var declarations = new Dictionary<string, BirFunctionDeclaration>();
        var projection = new Projection(new Dictionary<string, int[]>(), new HashSet<string>(), policy, intrinsics, references);
        foreach (var owner in owners.Where(owner => !policy.IsBindingAlias(owner.Name)))
        {
            foreach (var method in owner.Methods)
            {
                projection.ProjectMethod(method, owner.Facts?.NullableFrame);
                if (method.DeclarationId.HasValue) declarations[method.DeclarationId.Value] = method;
            }
            foreach (var inherited in owner.InheritedMethods) projection.ProjectMethod(inherited, owner.Facts?.NullableFrame);
            foreach (var property in owner.Properties)
                if (!policy.HasNativeMethodSignature(false, Intrinsic(property.Attrs.OrEmpty()),
                        property.Overrides.OrEmpty().Select(edge => edge.Owner.OrDefault()))
                    && property.Type.OrDefault() is TypeNode.Tv variable)
                {
                    // Properties preserve their raw physical variable here. Unlike methods,
                    // this source fact was never mapped through SourceVariable in the policy.
                    if (!property.KotlinType.HasValue) property.KotlinType = variable;
                    property.Type = new TypeNode.Fqn("object");
                }
        }
        void Calls(BirGraphValue node)
        {
            if (node is BirAttribute or BirOverride or IBirExecutablePayload) return;
            node.VisitChildren(Calls);
            var (identity, overrides, signature) = node switch {
                BirCallStatic call => (call.DeclarationId.OrDefault(), Array.Empty<BirOverride>(), call.Sig.OrDefault()),
                BirCallInstance call => (call.DeclarationId.OrDefault(), call.Overrides.OrEmpty(), call.Sig.OrDefault()),
                BirConstrainedCall call => (call.DeclarationId.OrDefault(), Array.Empty<BirOverride>(), call.Sig.OrDefault()),
                _ => (null, null, null),
            };
            if (overrides == null) return;
            var indices = projection.CallParameters(identity, overrides.Select(edge => edge.DeclarationId.OrDefault()));
            if (signature != null)
                foreach (var index in indices) signature[index] = new TypeNode.Fqn("object");
            if (identity == null || !projection.Returns.Contains(identity)) return;
            var selected = declarations.GetValueOrDefault(identity);
            switch (node)
            {
                case BirCallStatic call when !call.Ret.HasValue:
                    call.Ret = OptionalField<TypeNode>.Of(call.Sty.OrDefault() ?? call.DynRet.OrDefault() ?? selected?.Ret.OrDefault());
                    break;
                case BirCallInstance call when !call.Ret.HasValue:
                    call.Ret = OptionalField<TypeNode>.Of(call.Sty.OrDefault() ?? call.DynRet.OrDefault() ?? selected?.Ret.OrDefault());
                    break;
                case BirConstrainedCall call when !call.Ret.HasValue:
                    call.Ret = OptionalField<TypeNode>.Of(call.Sty.OrDefault() ?? selected?.Ret.OrDefault());
                    break;
            }
        }
        foreach (var root in roots) Calls(root);
        return projection;
    }

    internal sealed partial record Projection
    {
        void ProjectParameters(BirParameter[] parameters, bool query, NullableRepresentationFrame owner, NullableRepresentationFrame method,
            out int[] projected)
        {
            projected = ParameterIndices(parameters.Select(parameter => parameter.Type.OrDefault()).ToArray(), query);
            foreach (var index in projected)
            {
                var parameter = parameters[index];
                if (!parameter.KotlinType.HasValue)
                    parameter.KotlinType = parameter.NullableGeneric.OrDefault()
                        ?? SourceVariable((TypeNode.Tv)parameter.Type.Value, owner, method);
                parameter.Type = new TypeNode.Fqn("object");
            }
        }

        internal void ProjectMethod(BirFunctionDeclaration method, NullableRepresentationFrame owner)
        {
            var edges = method.Overrides.OrEmpty();
            if (!ProjectsMethod(Policy.HasNativeMethodSignature(method._physicalDeclarationSignature.OrDefault(),
                    Intrinsic(method.Attrs.OrEmpty()), edges.Select(edge => edge.Owner.OrDefault())),
                edges.Select(edge => edge.DeclarationId.OrDefault()))) return;
            var frame = method.NullableRepresentationFrame.OrDefault();
            ProjectParameters(method.Params.OrEmpty(), KotlinCollectionSlotSynthesis.ErasedQueryOverride(edges) != null,
                owner, frame, out var projected);
            if (ProjectsReturn(method.Ret.OrDefault(), method.Mods.OrDefault()?.Suspend.OrDefault() == true))
            {
                if (!method.RetKotlinType.HasValue)
                    method.RetKotlinType = method.NullableGenericRet.OrDefault()
                        ?? SourceVariable((TypeNode.Tv)method.Ret.Value, owner, frame);
                method.Ret = new TypeNode.Fqn("object");
            }
            Record(method.DeclarationId.OrDefault(), projected, method.RetKotlinType.OrDefault());
        }

        internal void ProjectMethod(BirInheritedClassMethod method, NullableRepresentationFrame owner)
        {
            var edges = method.Overrides.OrEmpty();
            if (!ProjectsMethod(Policy.HasNativeMethodSignature(false, false, edges.Select(edge => edge.Owner.OrDefault())),
                edges.Select(edge => edge.DeclarationId.OrDefault()))) return;
            ProjectParameters(method.Params.OrEmpty(), KotlinCollectionSlotSynthesis.ErasedQueryOverride(edges) != null,
                owner, null, out _);
            if (ProjectsReturn(method.Ret.OrDefault(), method.Mods.OrDefault()?.Suspend.OrDefault() == true))
            {
                if (!method.RetKotlinType.HasValue) method.RetKotlinType = SourceVariable((TypeNode.Tv)method.Ret.Value, owner, null);
                method.Ret = new TypeNode.Fqn("object");
            }
        }
    }
}
