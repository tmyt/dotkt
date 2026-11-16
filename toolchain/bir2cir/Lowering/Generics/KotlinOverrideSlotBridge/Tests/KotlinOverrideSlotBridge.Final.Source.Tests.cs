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
            Action<BirFileDeclaration,IBirTypeDeclaration,BirFunctionDeclaration,BirGraphValue> callback=null,
            IReadOnlySet<CovariantInterfaceReturnBridge.SourceBridgedSlot> covariant=null)
            => ApplyAll(files,_=>false,references,files.SelectMany(file=>file.Types.OrEmpty()).Select(type=>type.Name.Value).ToHashSet(),
                false,new GenericRepresentationPolicy(new Dictionary<string,string>()),covariant,null,_=>true,callback);

        {
            string Key(TypeNode.Fqn owner, TypeNode parameter, TypeNode result, BirTypeParameter[] generics = null)
            {
                var body = Method("body", "selected-body", result, parameter);
                body.TypeParams = generics ?? [];
                return SelectedSlotKey(new(owner, owner, true, true, false, "Read", "Read", null,
                    [parameter], result, new OverrideMethod(body), generics));
            }
            var integer = new TypeNode.Fqn("System.Int32");
            var outerGeneric = new TypeNode.Fqn("NestedArityInterop.SegmentCollisionOuter`1+Contract`1", [integer, text]);
            var innerGeneric = new TypeNode.Fqn("NestedArityInterop.SegmentCollisionOuter+Contract`2", [integer, text]);
            var owned = new HashSet<string> { Key(outerGeneric, obj, integer) };
            Require(!owned.Contains(Key(innerGeneric, obj, integer)), "slot priority collapsed nested TypeDef segment arity");
            Require(Key(outerGeneric, new TypeNode.Fqn("int"), new TypeNode.Fqn("int"))
                == Key(outerGeneric, integer, integer), "slot priority did not normalize physical primitive spelling");
            Require(Key(new TypeNode.Fqn("Face`1", [integer]), obj, integer)
                != Key(new TypeNode.Fqn("Face`1", [text]), obj, integer), "slot priority collapsed constructed owner arguments");
            var marker = new TypeNode.Fqn("SignatureMarker");
            Require(Key(outerGeneric, new TypeNode.Mod(false, marker, obj), integer)
                != Key(outerGeneric, obj, integer), "slot priority lost optional signature modifiers");
            Require(Key(outerGeneric, obj, integer, [new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { new TypeNode.Fqn("System.IComparable") } }])
                != Key(outerGeneric, obj, integer, [new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { new TypeNode.Fqn("System.IDisposable") } }]),
                "slot priority collapsed method generic constraints");
        }

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
            // The owning signature owner retargets this SAME selected relation.
            var modified=new TypeNode.Mod(false,new TypeNode.Fqn("SignatureMarker"),obj);
            implementation.Params.Value[0].Type=modified;
            var retired=new List<BirMethodImpl>();
            DeclarationSignatureIdentity.BindSelectedOverridesSource(new[] { file },
                new DeclarationSignatureIdentity.SignatureAllocationPlan(new Dictionary<string,TypeNode[]> {
                    ["selected-body"]=new TypeNode[] { modified },
                }),references,_=>false,(_,_,_)=> { },_=> { },actual=>retired.Add(actual));
            var members=derived.Methods.Value;
            var originalRelations=isInterface ? members[0].ClrInterfaceImpls.Value : members[0].ClrBaseImpls.Value;
            var bridgeRelations=isInterface ? members[1].ClrInterfaceImpls.Value : members[1].ClrBaseImpls.Value;
            Require(members.Length==2 && originalRelations.Length==0
                && members[1].Params.Value[0].Type.Value==obj && bridgeRelations.Length==1
                && bridgeRelations[0].Member.Value=="NativeAccept"
                && retired.Count==1 && ReferenceEquals(retired[0],descriptor),
                "owning nominal-signature repair lost selected MethodImpl or exact retirement");
        }

        // Pin the final owner's capability lookup to the SAME actual source handle, not equal record values.
        // With no matching capability the object seam must publish its authored bridge before the descriptor.
        {
            var slot=Method("accept","object-slot",boolean,obj); slot.Abstract=true; slot.Body=default;
            var contract=new BirInterfaceDefinition { Kind="interface",Name="ObjectSink",Methods=new[] { slot } };
            var implementation=Method("accept","string-body",boolean,text);
            implementation.Overrides=new[] { Edge("ObjectSink","accept","object-slot") };
            var originalBody=implementation.Body.Value;
            var owner=new BirClassDefinition { Kind="class",Name="StringSink",Methods=new[] { implementation },
                Interfaces=new TypeNode[] { new TypeNode.Fqn("ObjectSink") } };
            var file=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { contract,owner } };
            var key=CovariantInterfaceReturnBridge.DescriptorKey(new TypeNode.Fqn("ObjectSink"),"accept",0,
                new TypeNode[] { obj },boolean,references,_=>false);
            var capabilities=new HashSet<CovariantInterfaceReturnBridge.SourceBridgedSlot>(new CovariantInterfaceReturnBridge.SourceSlotComparer()) {
                new(implementation,key),
            };
            Apply(new[] { file },(_,_,_,_)=>throw new InvalidOperationException("A selected covariant capability materialized a duplicate final slot"),capabilities);
            Require(owner.Methods.Value.Length==1 && ReferenceEquals(owner.Methods.Value[0],implementation),
                "matching covariant source handle did not suppress final allocation");
            var foreign=implementation with { };
            Require(!ReferenceEquals(foreign,implementation) && foreign.Equals(implementation),"reference-identity probe no longer has equal record values");
            capabilities.Clear(); capabilities.Add(new(foreign,key));
            var order=new List<string>();
            BirFunctionDeclaration published=null;
            Apply(new[] { file },(actualFile,actualOwner,method,descriptor)=> {
                Require(ReferenceEquals(actualFile,file) && ReferenceEquals(actualOwner,owner),"bridge publication selected another owner/file");
                if (method!=null)
                {
                    Require(descriptor==null && published==null && owner.Methods.Value.Length==2
                        && ReferenceEquals(owner.Methods.Value[^1],method) && method.PhysicalSlotBridge.Value
                        && method.ClrInterfaceImpls.Value.Length==0,"bridge callback did not observe the exact completed append");
                    published=method; order.Add("method");
                }
                else
                {
                    Require(published!=null && descriptor is BirMethodImpl exact
                        && ReferenceEquals(published.ClrInterfaceImpls.Value.Single(),exact),"descriptor callback preceded its actual attachment");
                    order.Add("descriptor");
                }
            },capabilities);
            Require(order.SequenceEqual(new[] { "method","descriptor" }) && published!=null
                && published.ClrInterfaceImpls.Value.Single().Member.Value=="accept"
                && ReferenceEquals(owner.Methods.Value[0],implementation) && ReferenceEquals(implementation.Body.Value,originalBody),
                "foreign covariant handle suppressed allocation or publication copied the continuing body");
        }

        // A single selected source declaration has two CURRENT physical obligations after Cold.
        foreach (var reverse in new[] { false,true })
        {
            var taskResult=new TypeNode.Fqn("System.Threading.Tasks.Task",new TypeNode[] { obj });
            var continuation=new TypeNode.Fqn("kotlin.coroutines.Continuation",new TypeNode[] { obj });
            var task=Method("NativeRun","source-run",taskResult,obj);
            var cold=Method("NativeRun$dotkt_suspend","source-run|cold",obj,obj,continuation);
            task.KotlinSourceMember="run"; cold.KotlinSourceMember="run";
            task.Abstract=true; cold.Abstract=true; task.Body=default; cold.Body=default;
            task.SuspendSourceParams=new TypeNode[] { obj }; cold.SuspendSourceParams=new TypeNode[] { obj };
            task.SuspendSourceRet=obj; cold.SuspendSourceRet=obj;
            var contract=new BirInterfaceDefinition { Kind="interface", Name="Run", Methods=new[] { task,cold } };
            var taskBody=Method("run","body-run",taskResult,obj);
            var coldBody=Method("run$dotkt_suspend","body-run|cold",obj,obj,continuation);
            taskBody.KotlinSourceMember="run"; coldBody.KotlinSourceMember="run";
            taskBody.SuspendSourceParams=new TypeNode[] { obj }; coldBody.SuspendSourceParams=new TypeNode[] { obj };
            taskBody.SuspendSourceRet=obj; coldBody.SuspendSourceRet=obj;
            taskBody.Overrides=new[] { Edge("Run","run","source-run") };
            coldBody.Overrides=new[] { Edge("Run","run","source-run") };
            coldBody.Overrides.Value[0].PhysicalDeclarationId="source-run|cold";
            var taskOnly=Method("taskOnlyBridge",null,taskResult,obj);
            taskOnly.Generated=true; taskOnly.PhysicalSlotBridge=true; taskOnly.SuspendTaskOnlyBridge=true;
            var owner=new BirClassDefinition { Kind="class",Name="Runner",Interfaces=new TypeNode[] { new TypeNode.Fqn("Run") },
                Methods=new[] { taskBody,coldBody,taskOnly } };
            var first=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { contract } };
            var second=new BirFileDeclaration { Types=new IBirTypeDeclaration[] { owner } };
            Apply(reverse ? new[] { second,first } : new[] { first,second });
            Require(!taskOnly.SuspendTaskOnlyBridge.IsPresent && !taskBody.SuspendTaskOnlyBridge.IsPresent && !coldBody.SuspendTaskOnlyBridge.IsPresent,
                "final-only suspend marker escaped owning stage");
            Require(ReferenceEquals(owner.Methods.Value[0],taskBody) && ReferenceEquals(owner.Methods.Value[1],coldBody),
                "Task/cold source membership reordered");
            Require(taskBody.ClrInterfaceImpls.Value.Single().Member.Value=="NativeRun"
                && coldBody.ClrInterfaceImpls.Value.Single().Member.Value=="NativeRun$dotkt_suspend",
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
