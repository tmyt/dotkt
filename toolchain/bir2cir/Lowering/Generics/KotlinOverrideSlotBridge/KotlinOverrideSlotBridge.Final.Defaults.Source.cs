using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Def = SupertypeGraph.Def;

static partial class KotlinOverrideSlotBridge
{
    static void RepairFinalSourceDefaults(Def definition, FinalSourceContext context)
    {
        if (context.Index.Declarations[definition.Name] is not BirClassDefinition declaration || !declaration.Methods.HasValue) return;
        var bridges = new Dictionary<string,BirFunctionDeclaration>(StringComparer.Ordinal);
        var ordinal = 0;
        var reachable = SupertypeGraph.Reachable(definition,context.Index.Definitions,context.References)
            .Where(edge => edge.isInterface).Select(edge => edge.spec).ToArray();
        foreach (var fact in declaration.InheritedDefaultAccessors.OrEmpty())
            RepairProperty(fact);
        foreach (var fact in declaration.InheritedDefaultMethods.OrEmpty())
            RepairMethod(fact);

        void Add(string name,string accessor,TypeNode.Fqn callOwner,SelectedMethodHeader source,
            string association,IEnumerable<BirMethodImpl> descriptors,TypeNode[] sourceArguments,
            TypeNode[] descriptorArguments,bool virtualCall)
            => AddFinalSourceDefaultBridge(definition,declaration,context,bridges,ref ordinal,name,accessor,
                callOwner,source,association,descriptors,sourceArguments,descriptorArguments,virtualCall);

        void RepairMethod(BirInheritedDefaultMethod fact)
        {
            var implementation = fact.Implementation.OrDefault();
            if (!fact.Member.HasValue || !fact.Params.HasValue || !fact.Ret.HasValue || implementation == null
                || implementation.Owner.OrDefault() is not TypeNode.Fqn implementationOwner
                || !implementation.Member.HasValue || implementation.Kind.OrDefault() != "method") return;
            var arity = implementation.Arity.HasValue ? implementation.Arity.Value : -1;
            var parameters = implementation.TypeParams.OrDefault();
            if (arity < 0 || (parameters?.Length ?? 0) != arity || fact.Params.Value.Any(type => type == null)) return;
            var identities = fact.Overrides.OrEmpty().Where(edge => edge.Owner.OrDefault() is TypeNode.Fqn owner
                    && owner.Name == implementationOwner.Name && edge.Member.OrDefault() == implementation.Member.Value)
                .Select(edge => edge.DeclarationId.OrDefault()).Where(id => id != null).Distinct().ToArray();
            var selectedId = identities.Length == 1 ? identities[0] : null;
            foreach (var spec in reachable.Where(value => value.Name == implementationOwner.Name)
                         .GroupBy(SupertypeGraph.TypeKey).Select(group => group.First()))
            {
                if (context.Index.Definitions.TryGetValue(spec.Name,out var parent) && parent.Kind == "interface")
                {
                    var arguments = SupertypeGraph.EffectiveArgs(spec,parent.Arity);
                    if (arguments == null) continue;
                    var methods = context.Index.Declarations[parent.Name].Methods.OrEmpty();
                    var sources = methods.Where(method => !method.PhysicalSlotBridge.OrDefault()
                        && !KotlinPropertyAccessors.TryIdentity(method,out _,out _)
                        && (method.KotlinSourceMember.OrDefault() ?? method.Name.OrDefault()) == implementation.Member.Value
                        && method.TypeParams.OrEmpty().Length == arity
                        && SameSourceMethodTypeParameterShape(method.TypeParams.OrDefault(),parameters,arguments,arguments)
                        && (selectedId != null ? SourceDeclarationId(method.DeclarationId.OrDefault()) == SourceDeclarationId(selectedId)
                            : SignatureMatches(context.Views.Method(method),fact.Params.Value,fact.Ret.Value,arguments,context.References,context.IsValue))).ToArray();
                    if (sources.Length == 0 || sources.Length > 1 && sources.Any(method =>
                            !method.SuspendSourceParams.HasValue || !method.SuspendSourceRet.HasValue)) continue;
                    foreach (var source in sources)
                    {
                        var slotParameters = source.Params.OrEmpty().Select(parameter =>
                            SupertypeGraph.SubstOwnerTvs(FunctionSignatureIdentity.SignatureType(parameter),arguments)).ToArray();
                        var result = SupertypeGraph.SubstOwnerTvs(source.Ret.OrDefault(),arguments);
                        Add(fact.Member.Value,null,spec,SelectedMethodHeader.FromSource(source),null,
                            new[] { SourceImplDescriptor(spec,source.ExplicitClrName.OrDefault() ?? source.Name.OrDefault(),arity,
                                slotParameters,result,SubstituteParameters(source.TypeParams.OrDefault(),arguments),false) },arguments,[],false);
                        if (source.DeclarationId.HasValue)
                            foreach (var bridge in methods.Where(candidate => context.ExactSources.TryGetValue(candidate,out var identity)
                                         && identity == source.DeclarationId.Value && candidate.ClrInterfaceImpls.HasValue))
                                Add(fact.Member.Value,null,spec,SelectedMethodHeader.FromSource(source),null,
                                    bridge.ClrInterfaceImpls.Value,arguments,arguments,true);
                    }
                    continue;
                }
                var refs = context.References;
                var selectedArguments = spec.Args ?? [];
                var semanticArguments = refs.NullableTypeFrames.GetValueOrDefault(spec.Name)?.OrdinaryArguments(selectedArguments) ?? selectedArguments;
                ReferencedMethodDeclaration selected;
                var found = selectedId != null && refs.HasDotKtOwner(spec.Name)
                    ? refs.TrySelectedSourceOverrideDeclaration(spec.Name,implementation.Member.Value,arity,fact.Params.Value,
                        selectedArguments,SemanticMethodTypeParameters(implementation),semanticArguments,false,out selected,
                        selectedDeclarationId:selectedId)
                    : refs.TrySelectedSourceMethodDeclaration(spec.Name,implementation.Member.Value,arity,fact.Params.Value,fact.Ret.Value,
                        selectedArguments,SemanticMethodTypeParameters(implementation),semanticArguments,out selected);
                if (!found || selected.Return == null || selected.Parameters == null || selected.Parameters.Any(type => type == null)) continue;
                var physicalParameters = selected.PhysicalParameters.Select(type => SupertypeGraph.SubstOwnerTvs(type,selectedArguments)).ToArray();
                var physicalReturn = SupertypeGraph.SubstOwnerTvs(selected.PhysicalReturn,selectedArguments);
                var owner = new TypeNode.Fqn(refs.ExactReflectedOwner(spec.Name,spec.Args?.Length ?? 0),spec.Args);
                var selectedParameters = selected.TypeParams;
                var header = FinalMethodHeader(selected.PhysicalMember,physicalParameters,physicalReturn,arity,selectedParameters);
                Add(fact.Member.Value,null,owner,header,null,new[] { SourceImplDescriptor(owner,selected.PhysicalMember,arity,
                    physicalParameters,physicalReturn,selectedParameters,false) },[],[],false);
                foreach (var slot in refs.ReferencedMethodImpls(spec.Name,selected.DeclarationId))
                    Add(fact.Member.Value,null,owner,header,null,new[] { SourceImplDescriptor(slot.DeclarationOwner,
                        slot.DeclarationMember,slot.MethodArity,slot.Parameters,slot.Return,
                        slot.TypeParams,false) },[],selectedArguments,true);
            }
        }

        void RepairProperty(BirInheritedDefaultAccessor fact)
        {
            var implementation = fact.Implementation.OrDefault();
            if (!fact.PropertyName.HasValue || fact.PropertyAccessor.OrDefault() is not ("get" or "set")
                || !fact.Params.HasValue || !fact.Ret.HasValue || implementation == null
                || implementation.Owner.OrDefault() is not TypeNode.Fqn implementationOwner || !implementation.Member.HasValue) return;
            var accessor = implementation.Kind.OrDefault() switch { "getter" => "get", "setter" => "set", _ => null };
            var arity = implementation.Arity.HasValue ? implementation.Arity.Value : -1;
            var parameters = implementation.TypeParams.OrDefault();
            if (accessor == null || arity < 0 || (parameters?.Length ?? 0) != arity || fact.Params.Value.Any(type => type == null)) return;
            foreach (var spec in reachable.Where(value => value.Name == implementationOwner.Name)
                         .GroupBy(SupertypeGraph.TypeKey).Select(group => group.First()))
            {
                if (context.Index.Definitions.TryGetValue(spec.Name,out var parent) && parent.Kind == "interface")
                {
                    var arguments = SupertypeGraph.EffectiveArgs(spec,parent.Arity);
                    if (arguments == null) continue;
                    var methods = context.Index.Declarations[parent.Name].Methods.OrEmpty();
                    var sources = methods.Where(method => !method.PhysicalSlotBridge.OrDefault()
                        && KotlinPropertyAccessors.TryIdentity(method,out var name,out var role)
                        && name == implementation.Member.Value && role == accessor && method.TypeParams.OrEmpty().Length == arity
                        && SameSourceMethodTypeParameterShape(method.TypeParams.OrDefault(),parameters,arguments,arguments)
                        && SignatureMatches(context.Views.Method(method),fact.Params.Value,fact.Ret.Value,arguments,context.References,context.IsValue)).ToArray();
                    if (sources.Length != 1) continue;
                    var source = sources[0]; var association = source.PropertyAssociation.OrDefault();
                    foreach (var bridge in methods.Where(candidate => candidate.ClrInterfaceSlotBridge.OrDefault()
                                 && candidate.ClrInterfaceImpls.HasValue && KotlinPropertyAccessors.TryIdentity(candidate,out var name,out var role)
                                 && name == implementation.Member.Value && role == accessor
                                 && context.ExactSources.TryGetValue(candidate,out var selected) && selected == association))
                        Add(fact.PropertyName.Value,fact.PropertyAccessor.Value,spec,SelectedMethodHeader.FromSource(source),association,
                            bridge.ClrInterfaceImpls.Value,arguments,arguments,true);
                    continue;
                }
                var refs = context.References;
                var referenced = refs.ReferencedPropertyMethodImpls(spec.Name,implementation.Member.Value,accessor,
                    fact.Params.Value.Length,arity,fact.Params.Value,spec.Args ?? []);
                if (referenced.Count != 0)
                {
                    foreach (var selected in referenced)
                    {
                        var arguments = spec.Args ?? [];
                        var slotParameters = selected.Parameters.Select(type => SupertypeGraph.SubstOwnerTvs(
                            NullableGenericErasure.EraseNullableTv(type,context.IsValue),arguments)).ToArray();
                        var slotReturn = SupertypeGraph.SubstOwnerTvs(NullableGenericErasure.EraseNullableTv(selected.Return,context.IsValue),arguments);
                        if (SupertypeGraph.SubstOwnerTvs(selected.DeclarationOwner,arguments) is not TypeNode.Fqn descriptorOwner) continue;
                        Add(fact.PropertyName.Value,fact.PropertyAccessor.Value,spec,
                            FinalMethodHeader(selected.SourceMember,fact.Params.Value,fact.Ret.Value,arity,parameters),
                            "inherited-default:"+fact.PropertyName.Value+":"+fact.PropertyAccessor.Value,
                            new[] { SourceImplDescriptor(descriptorOwner,selected.DeclarationMember,selected.MethodArity,
                                slotParameters,slotReturn,selected.TypeParams,false) },[],[],true);
                    }
                    continue;
                }
                if (!refs.TryExternalPropertyAccessor(spec.Name,implementation.Member.Value,accessor,fact.Params.Value.Length,
                        arity,fact.Params.Value,spec.Args ?? [],out var physicalOwner,out _,out var physicalMember)) continue;
                var provisional = HasFinalSourceCollision(definition,declaration,physicalMember,arity,fact.Params.Value,fact.Ret.Value,
                    context,includeNonVirtual:false);
                if (!refs.TryNullableGenericPropertySlot(spec.Name,implementation.Member.Value,accessor,false,fact.Params.Value.Length,
                        arity,fact.Params.Value,spec.Args ?? [],out var result0,out var parameters0,out var refused,includeUnchanged:true)
                    || parameters0 == null || parameters0.Any(type => type == null) || refused?.Any(value => value) == true)
                {
                    if (provisional) throw UnrepresentableInheritedDefault(definition.Name,implementationOwner.Name,implementation.Member.Value,
                        accessor,"its exact CLR slot signature could not be resolved");
                    continue;
                }
                var arguments0 = spec.Args ?? [];
                var slotParameters0 = parameters0.Select(type => SupertypeGraph.SubstOwnerTvs(
                    NullableGenericErasure.EraseNullableTv(type,context.IsValue),arguments0)).ToArray();
                var slotReturn0 = result0 == null ? fact.Ret.Value : SupertypeGraph.SubstOwnerTvs(
                    NullableGenericErasure.EraseNullableTv(result0,context.IsValue),arguments0);
                var callable = refs.IsPublicConcreteInstanceMethod(physicalOwner,physicalMember,arity,slotParameters0,slotReturn0);
                if (!HasFinalSourceCollision(definition,declaration,physicalMember,arity,slotParameters0,slotReturn0,context,callable)) continue;
                if (!callable) throw UnrepresentableInheritedDefault(definition.Name,implementationOwner.Name,implementation.Member.Value,
                    accessor,$"the selected external body is not a public concrete MethodDef ({physicalOwner}.{physicalMember})");
                var descriptor = new TypeNode.Fqn(physicalOwner,spec.Args);
                Add(fact.PropertyName.Value,fact.PropertyAccessor.Value,descriptor,FinalMethodHeader(physicalMember,slotParameters0,slotReturn0,arity),
                    "inherited-default:"+fact.PropertyName.Value+":"+fact.PropertyAccessor.Value,
                    new[] { SourceImplDescriptor(descriptor,physicalMember,arity,slotParameters0,slotReturn0,null,false) },[],[],false);
            }
        }
    }

    static SelectedMethodHeader FinalMethodHeader(string name,TypeNode[] parameters,TypeNode result,int arity,BirTypeParameter[] typeParameters=null)
        => new() {
            Name=name, Params=parameters.Select((type,index)=>new SelectedParameterHeader { Name="p"+index, Type=BirGraphValue.CopyType(type) }).ToArray(),
            Ret=BirGraphValue.CopyType(result), TypeParams=typeParameters != null
                ? typeParameters.Select(parameter=>(BirTypeParameter)parameter.DeepClone()).ToArray()
                : arity > 0 ? Enumerable.Range(0,arity).Select(index=>new BirTypeParameter { Name="T"+index }).ToArray() : default,
        };

    static void AddFinalSourceDefaultBridge(Def definition,IBirTypeDeclaration declaration,FinalSourceContext context,
        Dictionary<string,BirFunctionDeclaration> bridges,ref int ordinal,string name,string accessor,TypeNode.Fqn callOwner,
        SelectedMethodHeader source,string association,IEnumerable<BirMethodImpl> descriptors,
        TypeNode[] sourceArguments,TypeNode[] descriptorArguments,bool virtualCall)
    {
        if (!source.Params.HasValue || !source.Ret.HasValue) return;
        var sourceParameters = source.Params.Value.Select(parameter=>parameter.Type.OrDefault() is {} type
            ? SupertypeGraph.SubstOwnerTvs(SignatureValueTypes.Of(type),sourceArguments) : null).ToArray();
        if (sourceParameters.Any(type=>type==null)) return;
        var callSignature = source.Params.Value.Select(parameter=>SupertypeGraph.SubstOwnerTvs(
            FunctionSignatureIdentity.SignatureType(parameter.Type.Value,parameter.FunctionSignatureType.OrDefault()),sourceArguments)).ToArray();
        var result=SupertypeGraph.SubstOwnerTvs(source.Ret.Value,sourceArguments);
        var parameters=SubstituteParameters(source.TypeParams.OrDefault(),sourceArguments);
        var member=source.ExplicitClrName.OrDefault() ?? source.Name.OrDefault();
        if (context.Index.Declarations.TryGetValue(callOwner.Name,out var target)
            && !target.Methods.OrEmpty().Any(method=>ReferenceEquals(method,source.Source)))
            throw new InvalidOperationException($"bir2cir: inherited-default call target {callOwner.Name}.{member} is absent from its declaring type");
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Owner.OrDefault() is not TypeNode.Fqn originalOwner || !descriptor.Member.HasValue
                || !descriptor.Params.HasValue || !descriptor.Ret.HasValue || !descriptor.Arity.HasValue || descriptor.Arity.Value<0) continue;
            var slotParameters=descriptor.Params.Value.Select(type=>type==null ? null : SupertypeGraph.SubstOwnerTvs(type,descriptorArguments)).ToArray();
            if (slotParameters.Any(type=>type==null)) continue;
            var slotReturn=SupertypeGraph.SubstOwnerTvs(descriptor.Ret.Value,descriptorArguments);
            if (!HasFinalSourceCollision(definition,declaration,descriptor.Member.Value,descriptor.Arity.Value,slotParameters,slotReturn,context,true)) continue;
            if (SupertypeGraph.SubstOwnerTvs(originalOwner,descriptorArguments) is not TypeNode.Fqn owner) continue;
            var slotTypeParameters=SubstituteParameters(descriptor.TypeParams.OrDefault(),descriptorArguments);
            var property=accessor is "get" or "set";
            var key=(property ? "dim-property:" : "dim-method:")+owner.Name+":"+name+":"+accessor+"#"
                +MethodTypeParameterShapeKey(slotTypeParameters,[])+":"+descriptor.Arity.Value+"("
                +string.Join(",",slotParameters.Select(SupertypeGraph.TypeKey))+")->"+SupertypeGraph.TypeKey(slotReturn);
            if (!bridges.TryGetValue(key,out var bridge))
            {
                var header=source.Copy();
                header.Params=source.Params.Value.Select((parameter,index)=> {
                    var copy=parameter.Copy(); copy.Type=sourceParameters[index]; return copy;
                }).ToArray();
                header.Ret=result;
                if (parameters!=null) header.TypeParams=parameters;
                bridge=BuildBridgeSource(declaration,header,slotParameters.Select(SignatureValueTypes.Of).ToArray(),slotReturn,
                    $"dotkt${(property ? "$dimprop" : "$dimmethod")}${SafeName(name)}${ordinal++}",
                    context.IsValue,context.References,callOwner,virtualCall,member,physicalImplementation:true,declarationSignature:callSignature);
                for (var index=0;index<slotParameters.Length;index++)
                    bridge.Params.Value[index].Type=BirGraphValue.CopyType(slotParameters[index]);
                if (!property) RoundtripMetadata.AddSourceMethodIdentity(bridge,name);
                else KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(bridge,name,accessor,association);
                bridges[key]=bridge;
                context.Append(declaration,bridge);
            }
            context.Attach(declaration,bridge,SourceImplDescriptor(owner,descriptor.Member.Value,descriptor.Arity.Value,
                slotParameters,slotReturn,slotTypeParameters,false),true);
        }
    }

    static bool HasFinalSourceCollision(Def definition,IBirTypeDeclaration declaration,string member,int arity,
        TypeNode[] parameters,TypeNode result,FinalSourceContext context,bool includeNonVirtual)
    {
        // CLR prefers an ordinary class method over a selected DIM. The exact MethodImpl repair
        // protects that default even when the colliding public method is inherited from a base.
        bool Collision(IBirTypeDeclaration owner,TypeNode[] arguments)
        {
            foreach (var method in owner.Methods.OrEmpty())
            {
                // Match ilemit's MethodAttributes.Virtual projection. A nonvirtual collision matters
                // only when the selected DIM is publicly callable, not beside a private-final body.
                if (method.Static.OrDefault() || method.Name.OrDefault()!=member || KotlinPropertyAccessors.TryIdentity(method,out _,out _)
                    || method.Vis.OrDefault() is not (null or "public") || method.TypeParams.OrEmpty().Length!=arity
                    || !method.Params.HasValue || method.Params.Value.Length!=parameters.Length || !method.Ret.HasValue
                    || !includeNonVirtual && !(method.Override.OrDefault() || method.Virtual.OrDefault() || method.Abstract.OrDefault()
                        || method.ObjectOverride.OrDefault() || method.PendingOverrideOwner.HasValue)) continue;
                var types=method.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();
                if (types.All(type=>type!=null) && SamePhysicalSignature(types.Select(type=>SupertypeGraph.SubstOwnerTvs(type,arguments)).ToArray(),
                        SupertypeGraph.SubstOwnerTvs(method.Ret.Value,arguments),parameters,result,context.References,context.IsValue,context.HasClassifier)) return true;
            }
            return false;
        }
        if (Collision(declaration,ClassOwnArgs(definition))) return true;
        var current=definition.Base; var seen=new HashSet<string>(StringComparer.Ordinal);
        while (current!=null && seen.Add(SupertypeGraph.TypeKey(current)))
        {
            if (context.Index.Definitions.TryGetValue(current.Name,out var parent) && parent.Kind=="class")
            {
                var arguments=SupertypeGraph.EffectiveArgs(current,parent.Arity);
                if (arguments==null) return false;
                if (Collision(context.Index.Declarations[parent.Name],arguments)) return true;
                current=parent.Base==null ? null : SupertypeGraph.SubstOwnerTvs(parent.Base,arguments) as TypeNode.Fqn;
                continue;
            }
            foreach (var candidate in context.References.AccessibleDeclaredInstanceMethods(current,member,arity))
                if ((includeNonVirtual || candidate.IsVirtual) && SamePhysicalSignature(candidate.Parameters,candidate.Return,
                        parameters,result,context.References,context.IsValue,context.HasClassifier)) return true;
            var args=current.Args ?? [];
            current=context.References.ReferencedSupertypes(current).Where(edge=>!edge.isInterface)
                .Select(edge=>SupertypeGraph.SubstOwnerTvs(edge.spec,args) as TypeNode.Fqn).FirstOrDefault(type=>type!=null);
        }
        return false;
    }
}
