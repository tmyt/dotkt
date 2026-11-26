using System.Security.Cryptography;
using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationSignatureIdentity
{
    internal static void BindSelectedOverridesSource(IReadOnlyList<BirFileDeclaration> roots,
        SignatureAllocationPlan plan, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        Action<BirFileDeclaration, IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> publishMethods,
        Action<BirMethodImpl> admitDescriptor, Action<BirMethodImpl> retireDescriptor)
    {
        ArgumentNullException.ThrowIfNull(publishMethods);
        ArgumentNullException.ThrowIfNull(admitDescriptor);
        ArgumentNullException.ThrowIfNull(retireDescriptor);
        var definitions = SupertypeGraph.CollectSource(roots);
        // Same current last-wins physical hierarchy as Collect; no original
        // source-eligibility catalog is expanded by this pass-local view.
        var owners = new Dictionary<string, (BirFileDeclaration File, IBirTypeDeclaration Type)>(StringComparer.Ordinal);
        foreach (var file in roots)
            foreach (var type in file.Types.OrEmpty())
                if (type.Name.HasValue) owners[type.Name.Value] = (file, type);
        var declarations = owners.Values.SelectMany(owner => owner.Type.Methods.OrEmpty()
                .Where(method => method.DeclarationId.HasValue).Select(method => (Owner: owner.Type, Method: method)))
            .ToDictionary(item => item.Method.DeclarationId.Value, StringComparer.Ordinal);

        void BindSlot(BirFileDeclaration file, IBirTypeDeclaration owner, BirFunctionDeclaration method,
            BirMethodImpl descriptor, bool isInterface, string slotKey, Func<BirTypeParameter[]> slotFrame = null)
        {
            var descriptors = isInterface ? method.ClrInterfaceImpls.OrEmpty() : method.ClrBaseImpls.OrEmpty();
            // Retarget only this selected relation, regardless of its old
            // signature: a body cannot retain the obsolete plain-slot mapping.
            var removed = descriptors.Where(value => SameSlot(value, descriptor)).ToArray();
            foreach (var actual in removed) retireDescriptor(actual);
            descriptors = descriptors.Where(value => !removed.Any(actual => ReferenceEquals(actual, value))).ToArray();
            if (isInterface) method.ClrInterfaceImpls = descriptors;
            else method.ClrBaseImpls = descriptors;
            var attachedTo = method;
            var targetParameters = descriptor.Params.Value;
            if (!targetParameters.Select(DeclarationIdentityBinding.PhysicalTypeSignature).SequenceEqual(
                    method.Params.Value.Select(parameter => DeclarationIdentityBinding.PhysicalTypeSignature(parameter.Type.Value))))
            {
                var bridgeName = descriptor.Member.Value + "$signature$" + Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(method.DeclarationId.OrDefault() + "|" + slotKey)));
                var bridge = KotlinOverrideSlotBridge.BuildSignatureBridgeSource(owner, method, targetParameters,
                    descriptor.Ret.Value, bridgeName, isValue, refs);
                // A forwarding MethodDef implements the selected slot, including
                // constraints on physical storage parameters not visible in Kotlin.
                if (slotFrame?.Invoke() is { Length: > 0 } frame)
                {
                    var args = ((TypeNode.Fqn)descriptor.Owner.Value).Args ?? [];
                    bridge.TypeParams = frame.Select(row => {
                        var copy = (BirTypeParameter)row.DeepClone();
                        copy.RewriteOwnTypeFieldsInPlace((_, _, t) => SupertypeGraph.SubstOwnerTvs(t, args));
                        return copy;
                    }).ToArray();
                }
                owner.Methods = owner.Methods.OrEmpty().Append(bridge).ToArray();
                publishMethods(file, owner, new[] { bridge });
                attachedTo = bridge;
                descriptors = Array.Empty<BirMethodImpl>();
            }
            var attached = (BirMethodImpl)descriptor.DeepClone();
            if (isInterface) attachedTo.ClrInterfaceImpls = descriptors.Append(attached).ToArray();
            else attachedTo.ClrBaseImpls = descriptors.Append(attached).ToArray();
            admitDescriptor(attached);
            method.Virtual = true;
        }
        foreach (var (file, owner) in owners.Values)
            foreach (var method in owner.Methods.OrEmpty().ToArray())
            {
                foreach (var id in method.SignatureOverrideDeclarations.OrEmpty())
                {
                    if (id == null) continue;
                    string selectedOwnerName, name;
                    int arity;
                    bool selectedInterface;
                    TypeNode[] openParameters;
                    TypeNode openReturn;
                    var referencedAllocation = false;
                    if (declarations.TryGetValue(id, out var selected))
                    {
                        selectedOwnerName = selected.Owner.Name.Value;
                        selectedInterface = selected.Owner.Kind.OrDefault() == "interface";
                        name = selected.Method.Name.OrDefault();
                        arity = selected.Method.TypeParams.OrEmpty().Length;
                        openParameters = selected.Method.Params.Value.Select(parameter => parameter.Type.OrDefault()).ToArray();
                        openReturn = selected.Method.Ret.OrDefault();
                    }
                    else if (refs?.TryDeclarationSignatureSlot(id, out var referenced) == true)
                    {
                        selectedOwnerName = referenced.Owner; selectedInterface = referenced.IsInterface;
                        name = referenced.Name; arity = referenced.Arity; openParameters = referenced.Parameters;
                        openReturn = referenced.Return; referencedAllocation = referenced.SignatureAllocated;
                    }
                    else continue;
                    var bodyAllocated = method.DeclarationId.OrDefault() is string implementationId
                        && plan.Parameters.ContainsKey(implementationId);
                    if (!bodyAllocated && !plan.Parameters.ContainsKey(id) && !referencedAllocation) continue;
                    foreach (var selectedOwner in SupertypeGraph.SelectedOverrideOwners(definitions[owner.Name.Value], definitions, refs,
                        method.ConstructedOverrideOwners, id, selectedOwnerName, declarations.ContainsKey(id) ? null : id))
                    {
                        var arguments = selectedOwner.Args ?? Array.Empty<TypeNode>();
                        BindSlot(file, owner, method, new BirMethodImpl {
                            Owner = BirGraphValue.CopyType(selectedOwner), Member = name, Arity = arity,
                            Params = openParameters.Select(type => SupertypeGraph.SubstOwnerTvs(type, arguments)).ToArray(),
                            Ret = SupertypeGraph.SubstOwnerTvs(openReturn, arguments),
                        }, selectedInterface, id, () => selected.Method != null ? selected.Method.TypeParams.OrEmpty()
                            : refs.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true));
                    }
                }
                if (method.DeclarationId.OrDefault() is string sourceId && plan.Parameters.ContainsKey(sourceId)
                    && PhysicalSlots.TryGetValue(sourceId, out var selectedPhysicalSlots))
                    foreach (var slot in selectedPhysicalSlots)
                        BindSlot(file, owner, method, slot.Descriptor, slot.IsInterface, DescriptorKey(slot.Descriptor));
                if (method.TypeParams.OrEmpty().Length > 0 && method.SignatureOverrideDeclarations.HasValue)
                    method.SelectedGenericOverrides = method.SignatureOverrideDeclarations.Value;
                method.SignatureOverrideDeclarations = default;
            }
    }

    static bool SameSlot(BirMethodImpl left, BirMethodImpl right) =>
        TypeNodeDataEquality.Equal(left.Owner.OrDefault(), right.Owner.OrDefault())
        && left.Member.OrDefault() == right.Member.OrDefault()
        && left.Arity.HasValue == right.Arity.HasValue && (!left.Arity.HasValue || left.Arity.Value == right.Arity.Value);

    // Preserve the old compact metadata-leaf hash input without constructing a
    // DOM expression/declaration or admitting the prototype into the program.
    static string DescriptorKey(BirMethodImpl descriptor) => Encoding.UTF8.GetString(
        BirCarrier.EncodeBody(BirCarrier.JsonV1, descriptor, (writer, value) => value.WriteJson(writer)));
}
