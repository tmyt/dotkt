using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    internal static void FinalSourceSelfTest()
    {
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var obj = new TypeNode.Fqn("System.Object");
        var boolean = new TypeNode.Fqn("kotlin.Boolean");
        var text = new TypeNode.Fqn("System.String");
        static void Require(bool condition,string message)
        { if (!condition) throw new InvalidOperationException("Final owning override: "+message); }
        BirFunctionDeclaration Method(string name,string id,TypeNode result,params TypeNode[] parameters) => new() {
            Name=name, DeclarationId=id, Virtual=true, Vis="public", Ret=result,
            Params=parameters.Select((type,index)=>new BirParameter { Name="p"+index, Type=type }).ToArray(),
            Body=new IBirStatement[] { new BirReturn { Value=OptionalField<IBirExpression>.Of(new BirDefaultValue { Type=result }) } },
        };
        BirOverride Edge(string owner,string member,string id) => new() {
            Owner=new TypeNode.Fqn(owner), Member=member, Kind="method", DeclarationId=id,
        };
        void Apply(IReadOnlyList<BirFileDeclaration> files,
            Action<BirFileDeclaration,IBirTypeDeclaration,BirFunctionDeclaration,BirGraphValue> callback=null)
            => ApplyAll(files,_=>false,references,files.SelectMany(file=>file.Types.OrEmpty()).Select(type=>type.Name.Value).ToHashSet(),
                false,new GenericRepresentationPolicy(new Dictionary<string,string>()),null,null,_=>true,callback);

        foreach (var isInterface in new[] { false,true })
        {
            var source=Method("NativeAccept","selected-base",boolean,obj);
            source.KotlinSourceMember="accept"; source.Abstract=true; source.Body=default;
            IBirTypeDeclaration parent=isInterface
                ? new BirInterfaceDefinition { Name="Base", Kind="interface", Methods=new[] { source } }
                : new BirClassDefinition { Name="Base", Kind="class", Abstract=true, Methods=new[] { source } };
            var implementation=Method("accept","selected-body",boolean,obj);
            implementation.Override=true; implementation.Overrides=new[] { Edge("Base","accept","selected-base") };
            var body=implementation.Body.Value;
            var derived=new BirClassDefinition { Name="Derived", Kind="class", Methods=new[] { implementation } };
            if (isInterface) derived.Interfaces=new TypeNode[] { new TypeNode.Fqn("Base") };
            else derived.Base=new TypeNode.Fqn("Base");
            var file=new BirFileDeclaration { Types=new[] { parent,derived } };
            var descriptors=new List<BirMethodImpl>();
            Apply(new[] { file },(actualFile,owner,method,descriptor)=> {
                Require(ReferenceEquals(actualFile,file) && ReferenceEquals(owner,derived),"wrong materialized owner");
                Require(method==null,"exact renamed slot unexpectedly created a bridge");
                var attached=(BirMethodImpl)descriptor;
                Require((isInterface ? implementation.ClrInterfaceImpls : implementation.ClrBaseImpls).Value.Any(value=>ReferenceEquals(value,attached)),
                    "callback preceded actual descriptor attachment");
                descriptors.Add(attached);
            });
            var descriptor=descriptors.Single();
            Require(descriptor.Member.Value=="NativeAccept" && descriptor.Params.Value.Single()==obj
                && ReferenceEquals(implementation.Body.Value,body) && ReferenceEquals(derived.Methods.Value.Single(),implementation),
                "selected renamed class/interface slot changed source identity/body");
            Require(implementation.SignatureOverrideDeclarations.Value.SequenceEqual(new[] { "selected-base" }),"selected local ID handoff lost");
            // The real late signature owner must still retarget this exact selected relation.
            var projected=file.ToDom();
            var modified=TypeJson.Write(new TypeNode.Mod(false,new TypeNode.Fqn("SignatureMarker"),obj));
            projected["types"][1]["methods"][0]["params"][0]["type"]=modified.DeepClone();
            DeclarationSignatureIdentity.BindSelectedOverrides(new[] { projected },
                new Dictionary<string,System.Text.Json.Nodes.JsonArray> { ["selected-body"]=new(modified) },references,_=>false);
            var members=projected["types"][1]["methods"].AsArray();
            Require(members.Count==2 && members[1][isInterface ? "clrInterfaceImpls" : "clrBaseImpls"].AsArray().Count==1,
                "genuine late nominal-signature repair lost selected MethodImpl");
        }

        // A single selected source declaration has two CURRENT physical obligations after Cold.
        foreach (var reverse in new[] { false,true })
        {
            var task=Method("NativeRun","source-run",obj,obj);
            var cold=Method("NativeRunCold","source-run|cold",obj,obj,obj);
            task.KotlinSourceMember="run"; cold.KotlinSourceMember="run$cold";
            task.Abstract=true; cold.Abstract=true; task.Body=default; cold.Body=default;
            task.SuspendSourceParams=Array.Empty<TypeNode>(); cold.SuspendSourceParams=Array.Empty<TypeNode>();
            task.SuspendSourceRet=obj; cold.SuspendSourceRet=obj;
            var contract=new BirInterfaceDefinition { Kind="interface", Name="Run", Methods=new[] { task,cold } };
            var taskBody=Method("run","body-run",obj,obj);
            var coldBody=Method("run$cold","body-run|cold",obj,obj,obj);
            taskBody.Overrides=new[] { Edge("Run","run","source-run") };
            coldBody.Overrides=new[] { Edge("Run","run$cold","source-run|cold") };
            taskBody.SuspendTaskOnlyBridge=true;
            var owner=new BirClassDefinition { Kind="class",Name="Runner",Interfaces=new TypeNode[] { new TypeNode.Fqn("Run") },
                Methods=new[] { taskBody,coldBody } };
            var first=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { contract } };
            var second=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { owner } };
            Apply(reverse ? new[] { second,first } : new[] { first,second });
            Require(!taskBody.SuspendTaskOnlyBridge.IsPresent && !coldBody.SuspendTaskOnlyBridge.IsPresent,
                "final-only suspend marker escaped owning stage");
            Require(ReferenceEquals(owner.Methods.Value[0],taskBody) && ReferenceEquals(owner.Methods.Value[1],coldBody),
                "Task/cold source membership reordered");
            Require(taskBody.ClrInterfaceImpls.Value.Single().Member.Value=="NativeRun"
                && coldBody.ClrInterfaceImpls.Value.Single().Member.Value=="NativeRunCold",
                "Task/cold projections did not retain separate final physical obligations");
        }

        // The class-level DIM repair consumes the live exact interface descriptor only AFTER every owner allocates.
        foreach (var reverse in new[] { false,true })
        {
            var getter=Method("get_Source","source-get",obj);
            getter.PropertyName="value"; getter.PropertyAccessor="get"; getter.PropertyAssociation="source-value";
            var exact=Method("exact-get",null,obj);
            exact.PhysicalSlotBridge=true; exact.ClrInterfaceSlotBridge=true;
            KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(exact,"value","get","source-value");
            exact.ClrInterfaceImpls=new[] { SourceImplDescriptor(new TypeNode.Fqn("Defaults"),"get_Value",0,[],obj,null,false) };
            var contract=new BirInterfaceDefinition { Kind="interface",Name="Defaults",Methods=new[] { getter,exact } };
            var ordinary=Method("get_Value","ordinary",obj);
            var owner=new BirClassDefinition { Kind="class",Name="DefaultUser",Methods=new[] { ordinary },
                Interfaces=new TypeNode[] { new TypeNode.Fqn("Defaults") },
                InheritedDefaultAccessors=new[] { new BirInheritedDefaultAccessor {
                    PropertyName="value",PropertyAccessor="get",Params=Array.Empty<TypeNode>(),Ret=obj,
                    Implementation=new BirInheritedImplementation { Owner=new TypeNode.Fqn("Defaults"),Member="value",Kind="getter",Arity=0 },
                } } };
            var first=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { contract } };
            var second=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { owner } };
            Apply(reverse ? new[] { second,first } : new[] { first,second });
            var bridge=owner.Methods.Value.Single(method=>method.PhysicalSlotBridge.OrDefault());
            Require(bridge.ClrInterfaceImpls.Value.Single().Member.Value=="get_Value"
                && bridge.KotlinPropertyAccessorCarrier.Value.SourceAssociation.Value=="source-value"
                && bridge.Body.Value.Single() is BirReturn { Value.HasValue:true } returned
                && returned.Value.Value is BirCallInstance call && call.Method.Value=="get_Source" && call.Virtual.Value,
                "inherited DIM property lost exact association, resolved descriptor or selected live target");
            Require(ReferenceEquals(owner.Methods.Value[0],ordinary) && owner.Properties.OrEmpty().Length==0,
                "DIM repair replaced an ordinary body or invented a public property surface");
        }

        // Reserve only the Kotlin iterator provider's physical enumeration slots, using current typed facts.
        var iterator=Method("iterator","bag-iterator",new TypeNode.Fqn("kotlin.collections.Iterator",new TypeNode[] { text }));
        iterator.RetKotlinType=iterator.Ret;
        var bag=new BirClassDefinition { Kind="class",Name="Bag",Methods=new[] { iterator },
            Interfaces=new TypeNode[] { new TypeNode.Fqn("System.Collections.Generic.IEnumerable",new TypeNode[] { text }) } };
        var bagFile=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { bag } };
        var inventory=CreateSourceSelection(new[] { bagFile });
        var reservation=ReverseEnumeratorBridgeSynthesis.ReserveSlots(inventory.Definitions["Bag"],inventory.Definitions,references,inventory.Declarations);
        Require(reservation!=null && reservation.Element==text,"typed iterator reader lost provider/element selection");
        Require(reservation.Owns(new TypeNode.Fqn("System.Collections.Generic.IEnumerable",new TypeNode[] { text }),"GetEnumerator",0,[],
            new TypeNode.Fqn("System.Collections.Generic.IEnumerator",new TypeNode[] { text }),type=>type,references),"enumerator slot reservation changed");
        DeclarationSignatureIdentity.PhysicalSlotSourceSelfTest();
        Console.WriteLine("[final owning override slots] self-test OK");
    }
}
