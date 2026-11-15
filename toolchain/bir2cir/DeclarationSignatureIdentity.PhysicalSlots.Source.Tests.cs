using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationSignatureIdentity
{
    internal static void PhysicalSlotSourceSelfTest()
    {
        var saved=PhysicalSlots.ToArray();
        try
        {
            PhysicalSlots.Clear();
            var method=new BirFunctionDeclaration { DeclarationId="selected-native" };
            var descriptor=new BirMethodImpl { Owner=new TypeNode.Fqn("System.Object"),Member="ToString",Arity=0,
                Params=Array.Empty<TypeNode>(),Ret=new TypeNode.Fqn("System.String"),
                TypeParams=OptionalField<BirTypeParameter[]>.Null,DotktValueReturn=false };
            RecordSelectedPhysicalSlot(method,descriptor,false);
            RecordSelectedPhysicalSlot(method,descriptor,false);
            RecordSelectedPhysicalSlot(new System.Text.Json.Nodes.JsonObject { [DeclarationIdentityBinding.Key]="selected-native" },descriptor.ToDom(),false);
            var snapshot=PhysicalSlots["selected-native"].Single();
            if (!snapshot.Descriptor.StructurallyEquals(descriptor) || ReferenceEquals(snapshot.Descriptor,descriptor)
                || !snapshot.Descriptor.TypeParams.IsNull || !snapshot.Descriptor.DotktValueReturn.IsPresent)
                throw new InvalidOperationException("Selected physical slot leaf lost presence, independence or shared dedup");
            descriptor.Member="mutated";
            if (snapshot.Descriptor.Member.Value!="ToString") throw new InvalidOperationException("Physical slot snapshot retained mutable declaration facts");
            var absent=(BirMethodImpl)snapshot.Descriptor.DeepClone(); absent.TypeParams=default;
            RecordSelectedPhysicalSlot(method,absent,false);
            RecordSelectedPhysicalSlot(method,absent,true);
            RecordSelectedPhysicalSlot(new BirFunctionDeclaration(),absent,false);
            if (PhysicalSlots.Count!=1 || PhysicalSlots["selected-native"].Count!=3)
                throw new InvalidOperationException("Physical slot leaf merged absent/null or interface/base obligations");
        }
        finally
        {
            PhysicalSlots.Clear();
            foreach (var row in saved) PhysicalSlots[row.Key]=row.Value;
        }
    }
}
