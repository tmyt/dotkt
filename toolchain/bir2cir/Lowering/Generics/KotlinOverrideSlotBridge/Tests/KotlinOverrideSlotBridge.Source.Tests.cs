using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    static void SourceDeclarationSelfTest()
    {
        var references = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var variable = new TypeNode.Tv("type", 0);
        var integer = new TypeNode.Fqn("kotlin.Int");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        TypeNode Box(TypeNode type) => new TypeNode.Fqn("Box", new[] { type });
        BirOverride Edge(string member, string kind, string identity) => new() {
            Owner = new TypeNode.Fqn("Base", new[] { integer }), Member = member, Kind = kind, DeclarationId = identity,
        };
        var sourceParameter = new TypeNode.Nullable(integer);
        var parameter = new BirParameter { Name = "value", Type = Box(sourceParameter), NullableFlags = OptionalField<int[]>.Null };
        var implementation = new BirFunctionDeclaration { Name = "move", DeclarationId = "body-move", Params = new[] { parameter },
            Ret = Box(sourceParameter), RetNullableFlags = OptionalField<int[]>.Null,
            Body = Array.Empty<IBirStatement>(), Overrides = new[] { Edge("move", "method", "slot-move") } };
        var suspend = new BirFunctionDeclaration { Name = "read", DeclarationId = "body-read", Params = Array.Empty<BirParameter>(),
            Ret = sourceParameter, Mods = new BirModifiers { Suspend = true }, Body = Array.Empty<IBirStatement>(),
            Overrides = new[] { Edge("read", "method", "slot-read") } };
        var getter = new BirFunctionDeclaration { Name = "bodyGetter", DeclarationId = "body-getter", PropertyName = "value", PropertyAccessor = "get",
            Params = Array.Empty<BirParameter>(), Ret = integer, Body = Array.Empty<IBirStatement>(),
            Overrides = new[] { Edge("value", "getter", "slot-getter") } };
        var ancestor = new BirInterfaceDefinition { Kind = "interface", Name = "Base", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Methods = new[] {
                new BirFunctionDeclaration { Name = "move", DeclarationId = "slot-move", Abstract = true,
                    Params = new[] { new BirParameter { Name = "value", Type = Box(new TypeNode.Nullable(variable)) } }, Ret = Box(new TypeNode.Nullable(variable)) },
                new BirFunctionDeclaration { Name = "read", DeclarationId = "slot-read", Abstract = true, Mods = new BirModifiers { Suspend = true },
                    Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Nullable(variable) },
                new BirFunctionDeclaration { Name = "sourceGetter", DeclarationId = "slot-getter", Abstract = true, ExplicitClrName = "exactGetter",
                    PropertyName = "value", PropertyAccessor = "get", Params = Array.Empty<BirParameter>(), Ret = integer },
            } };
        var owner = new BirClassDefinition { Kind = "class", Name = "Derived", Interfaces = new TypeNode[] { new TypeNode.Fqn("Base", new[] { integer }) },
            Methods = new[] { implementation, suspend, getter } };
        var root = new BirFileDeclaration { FileClass = "OverrideSource", Types = new IBirTypeDeclaration[] { ancestor, owner } };
        var originalBody = implementation.Body.Value;
        var policy = GenericRepresentationPolicy.CreateSource(new[] { root }, new Dictionary<string, string>(), references);
        PropagateErasedSlots(new[] { root }, type => type.Name == "kotlin.Int", references, true, policy, _ => false);
        var objectType = new TypeNode.Fqn("object");
        if (parameter.Type.Value != Box(objectType) || implementation.Ret.Value != Box(objectType)
            || parameter.NullableGeneric.Value != Box(sourceParameter) || implementation.NullableGenericRet.Value != Box(sourceParameter)
            || !parameter.NullableFlags.IsNull || !implementation.RetNullableFlags.IsNull
            || !ReferenceEquals(implementation.Body.Value, originalBody) || implementation.DeclarationId.Value != "body-move"
            || !ReferenceEquals(owner.Methods.Value[0], implementation)
            || suspend.SuspendTaskResult.Value != objectType || suspend.Ret.Value != sourceParameter
            || !getter.Virtual.OrDefault() || getter.ClrInterfaceImpls.Value.Single().Member.Value != "exactGetter")
            throw new InvalidOperationException("Typed override propagation lost current declaration identity, nullable facts, suspend result, or exact property slot");

        // Exercise the same declaration-edit function for an ordinary-function
        // source signature, including null/present source metadata semantics.
        var function = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>());
        var callback = new BirParameter { Name = "callback", Type = Box(function) };
        new OverrideParameter(callback).Move(Box(objectType), _ => false, null);
        if (callback.FunctionSignatureType.Value != Box(function) || callback.NullableGeneric.Value != Box(function))
            throw new InvalidOperationException("Typed override move lost its original ordinary-function discriminator");
        Console.WriteLine("[source override declarations] self-test OK (nested moves, preserved presence/identity, suspend results, exact property slots)");
    }

    // The unique former physical-owner assertions now exercise the same typed views used by all three phases.
    static void RepresentationSourceSelfTest()
    {
        var queryCarrier = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        if (Classify(queryCarrier,new TypeNode.Fqn("kotlin.String"),null,_=>false,false) != Fit.Bridge
            || !ErasureAligned(queryCarrier,new TypeNode.Fqn("kotlin.String")))
            throw new InvalidOperationException("Nullable object query slots lost their concrete implementation bridge");
        var local = new BirInterfaceDefinition { Name="LocalSlot",Kind="interface",Methods=Array.Empty<BirFunctionDeclaration>() };
        var inventory = SupertypeGraph.CollectSource(new[] { new BirFileDeclaration { Types=new IBirTypeDeclaration[] { local } } });
        if (IsNativeSlotOwner("LocalSlot",inventory,_=>false)
            || IsNativeSlotOwner("ReferencedSlot",inventory,name=>name=="ReferencedSlot")
            || !IsNativeSlotOwner("NativeSlot",inventory,_=>false))
            throw new InvalidOperationException("Physical-only slot capture admitted a selected Kotlin declaration");
        var variable = new TypeNode.Tv("type",0);
        var constructed = new BirFunctionDeclaration { Name="accept",Vis="public",
            Params=new[] { new BirParameter { Name="value",Type=new TypeNode.Fqn("ElementBox",new TypeNode[] { variable }) } },
            Overrides=new[] { new BirOverride { Owner=new TypeNode.Fqn("Base"),Member="accept",Kind="method",DeclarationId="selected-base" } } };
        var owner = new BirClassDefinition { Kind="class",Name="Derived",Methods=new[] { constructed } };
        var selection = CreateSourceSelection(new[] { new BirFileDeclaration { Types=new IBirTypeDeclaration[] { owner } } });
        var views = new OverrideViews(selection.Declarations);
        var constructedView = new TypeNode.Fqn("ElementBox$star");
        BirFunctionDeclaration Select(IEnumerable<BirFunctionDeclaration> candidates,string identity="selected-base") =>
            FindImplementer(views.Owner(selection.Definitions["Derived"]),new Dictionary<string,SupertypeGraph.Def>(),
                candidates.Select(views.Method),"Base","NativeAccept","accept",identity,null,null,0,
                new TypeNode[] { constructedView },null,Array.Empty<TypeNode>(),new TypeNode[] { variable },
                carrierSeam:(slot,body)=>slot==constructedView && body is TypeNode.Fqn { Name:"ElementBox",Args.Length:1 })?.Source;
        if (!ReferenceEquals(Select(new[] { constructed }),constructed)
            || Select(new[] { constructed },"other-base")!=null
            || Select(new[] { constructed,(BirFunctionDeclaration)constructed.DeepClone() })!=null)
            throw new InvalidOperationException("Renamed local slot lost its exact unchanged-source constructed override");

        var suspend = new BirFunctionDeclaration { DeclarationId="selected-suspend",SuspendSourceParams=Array.Empty<TypeNode>(),
            SuspendSourceRet=new TypeNode.Fqn("kotlin.Unit") };
        var cold=(BirFunctionDeclaration)suspend.DeepClone(); cold.DeclarationId="selected-suspend|cold";
        var other=(BirFunctionDeclaration)suspend.DeepClone(); other.DeclarationId="other-suspend";
        var missingId=(BirFunctionDeclaration)suspend.DeepClone(); missingId.DeclarationId=default;
        var missingSignature=(BirFunctionDeclaration)cold.DeepClone(); missingSignature.SuspendSourceParams=default;
        bool Same(params BirFunctionDeclaration[] methods) => SameSelectedSuspendDeclaration(methods.Select(method=>new OverrideMethod(method)).ToArray());
        if (!Same(suspend,cold) || !Same(cold,suspend) || Same(suspend,other) || Same(suspend,missingId) || Same(suspend,missingSignature))
            throw new InvalidOperationException("Inherited suspend projections lost their exact source identity proof");
        var frame=new NullableRepresentationFrame(2,new[] { 0 },physicalOrder:new[] { 3,0,4,1,2,5 },
            storageIndices:new[] { 0,1 },nullableStorageIndices:new[] { 0 });
        var ordinary=frame.Variable(variable,NullableRepresentationFrame.Role.Ordinary);
        var storage=frame.Variable(variable,NullableRepresentationFrame.Role.Storage);
        var otherStorage=frame.Variable(new TypeNode.Tv("type",1),NullableRepresentationFrame.Role.Storage);
        if (!SameSourceRepresentationVariables(storage,ordinary,frame)
            || SameSourceRepresentationVariables(otherStorage,ordinary,frame)
            || SameSourceRepresentationVariables(new TypeNode.Tv("method",storage.I),ordinary,frame)
            || SameSourceRepresentationVariables(new TypeNode.Array(storage),new TypeNode.Array(ordinary),frame)
            || SameSourceRepresentationVariables(frame.Variable(variable,NullableRepresentationFrame.Role.Nullable),ordinary,frame)
            || SameSourceRepresentationVariables(storage,ordinary,null))
            throw new InvalidOperationException("Override representation seam lost declaration-owned source identity");
        var permuted=new BirFunctionDeclaration {
            TypeParams=Enumerable.Range(0,frame.PhysicalArity).Select(index=>new BirTypeParameter { Name="P"+index }).ToArray(),
            NullableRepresentationFrame=frame,
            MethodTypeParameterBoundsPre=new SourceBoundsSnapshot(new Dictionary<int,TypeNode[]> {
                [0]=new TypeNode[] { new TypeNode.Fqn("FirstBound") },[1]=new TypeNode[] { new TypeNode.Fqn("SecondBound") },
            }),
        };
        var authored=AuthoredParameters(new OverrideMethod(permuted));
        var semantic=SemanticMethodTypeParameters(permuted);
        if (authored.Length!=frame.SourceArity || authored[0].Constraints.Value[0]!=new TypeNode.Fqn("FirstBound")
            || authored[1].Constraints.Value[0]!=new TypeNode.Fqn("SecondBound")
            || !semantic[frame.SourcePosition(0)].StructurallyEquals(authored[0])
            || !semantic[frame.SourcePosition(1)].StructurallyEquals(authored[1])
            || permuted.TypeParams.Value.Any(parameter=>parameter.Constraints.IsPresent))
            throw new InvalidOperationException("Source constraint comparison consumed companions or ignored recorded physical order");
        var function=new TypeNode.Fn(false,new TypeNode.Fqn("kotlin.Unit"),new[] { ordinary });
        var functionBody=function with { Params=new[] { storage } };
        if (!SameSourceFunctionRepresentation(function,functionBody,frame)
            || SameSourceFunctionRepresentation(function,function with { Params=new[] { otherStorage } },frame)
            || SameSourceFunctionRepresentation(function with { Clr="System.Action" },functionBody with { Clr="System.Action" },frame)
            || SameSourceFunctionRepresentation(new TypeNode.Array(function),new TypeNode.Array(functionBody),frame))
            throw new InvalidOperationException("Function override carrier seam lost source identity or admitted a nominal conversion");
        var valueSignature=new TypeNode.Fn(false,new TypeNode.Fqn("kotlin.Unit"),new TypeNode[] { new TypeNode.Fqn("kotlin.Unit") },Clr:"System.Func");
        var voidSignature=valueSignature with { Ret=new TypeNode.Fqn("void"),Clr="System.Action" };
        if (ClrMemberResolution.SameInterfaceSlotType(valueSignature,voidSignature)
            || SameSourceFunctionRepresentation(valueSignature,voidSignature,null)
            || ClrMemberResolution.SameInterfaceSlotType(BirTypeLowering.DelegateFqnOf(valueSignature),BirTypeLowering.DelegateFqnOf(voidSignature)))
            throw new InvalidOperationException("Function signature bridge confused source identity with delegate equivalence");
        var carrier=new TypeNode.Fn(false,new TypeNode.Fqn("object"),new TypeNode[] { new TypeNode.Fqn("object") },Clr:"System.Func");
        var integer=new TypeNode.Fqn("System.Int32");
        if (IsNullableValueSlot(carrier,new TypeNode.Nullable(carrier),null,_=>false,false)
            || !IsNullableValueSlot(integer,new TypeNode.Nullable(integer),null,type=>type.Name=="System.Int32",false))
            throw new InvalidOperationException("Nullable bridge confused reference annotations with CLR nullable values");
        var storageFrame=new NullableRepresentationFrame(1,Array.Empty<int>(),storageIndices:new[] { 0 });
        var slot=new BirFunctionDeclaration { TypeParams=new[] {
            new BirTypeParameter { Name="R",Constraints=new TypeNode[] { variable } },new BirTypeParameter { Name="P" },
        },NullableRepresentationFrame=storageFrame };
        var animal=new TypeNode.Fqn("Animal");
        var implementation=new BirFunctionDeclaration { TypeParams=new[] {
            new BirTypeParameter { Name="R",Constraints=new TypeNode[] { animal } },
            new BirTypeParameter { Name="P",Constraints=new TypeNode[] { animal } },
        },NullableRepresentationFrame=storageFrame };
        var arguments=new TypeNode[] { animal };
        if (!SameSourceMethodTypeParameterShape(SourceParameters(new OverrideMethod(slot)),SourceParameters(new OverrideMethod(implementation)),arguments,[])
            || SameSourceMethodTypeParameterShape(slot.TypeParams.Value,implementation.TypeParams.Value,arguments,[])
            || implementation.TypeParams.Value[1].Constraints.Value.Length!=1)
            throw new InvalidOperationException("Override source selection consumed or mutated a physical companion constraint");
        OwnerConstrainedMethodLowering.RecordOverride(implementation,slot.TypeParams.Value,arguments,new[] { new BirTypeParameter { Name="T" } });
        if (!implementation._ownerConstraintOverrideBounds.HasValue
            || implementation._ownerConstraintOverrideBounds.Value.Rows.Count!=2
            || !implementation._ownerConstraintOverrideBounds.Value.Rows.Select(row=>row.Index).SequenceEqual(new[] { 0,1 }))
            throw new InvalidOperationException("Selected override failed to transfer its bound plan to matching storage companion");
        Console.WriteLine("[override representation seam] self-test OK (permuted frame, distinct source, scope, nested rejection)");
    }
}
