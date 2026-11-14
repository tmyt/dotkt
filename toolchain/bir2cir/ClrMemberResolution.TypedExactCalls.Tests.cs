using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void TypedExactCallSelfTest()
    {
        static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Typed exact CLR calls: " + reason); }
        static int Occurrences(BirGraphValue root, BirGraphValue target)
        {
            var count = 0;
            root.Visit(node => { if (ReferenceEquals(node, target)) count++; });
            return count;
        }
        static void SameCurrentRenderer(BirGraphValue typed, JsonObject dom)
        {
            if (!JsonNode.DeepEquals(typed.ToDom(), dom))
                throw new InvalidOperationException("Typed exact CLR calls disagree with the live DOM renderer");
        }
        var previousReferences = _refs;
        try
        {
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var text = new TypeNode.Fqn("System.String");
            var obj = new TypeNode.Fqn("System.Object");
            var integer = new TypeNode.Fqn("System.Int32");
            var definingAssembly = typeof(object).Assembly.GetName().Name;

            var concat = typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(object), typeof(object) })!;
            var left = new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("left") };
            var right = new BirConstant { Type = obj, Value = new BirLiteral.NullLiteral() };
            IBirExpression[] arguments = { left, right };
            var staticPlan = PlanExactDispatch(refs, concat, text, isStatic: true);
            Require(staticPlan.Name == "Concat" && staticPlan.Result == text
                && staticPlan.Arguments.SequenceEqual(new TypeNode[] { obj, obj })
                && staticPlan.Reference.ReturnType == text && staticPlan.Visibility == null
                && staticPlan.OwnerParameters == null, "static plan changed its selected physical signature");
            var call = ExactStaticCall(refs, concat, text, arguments);
            var reference = call.MemberRef.Value;
            Require(call.Owner.Value == text && !call.OwnerType.IsPresent && call.Method.Value == "Concat"
                && call.Virtual.HasValue && !call.Virtual.Value && call.DotktDeclarationIdentityBound.Value
                && call.Ret.Value == text && call.Sty.Value == text
                && call.Sig.Value.SequenceEqual(new TypeNode[] { obj, obj })
                && reference.Kind.Value == MemberRefNode.Kinds.Method && reference.Name.Value == "Concat"
                && reference.Assembly.Value == definingAssembly && reference.GenericArity.Value == 0
                && reference.CallingConvention.Value == MemberRefNode.Static && reference.DeclaringType.Value == text
                && reference.ReturnType.Value == text && reference.ParameterTypes.Value.SequenceEqual(new TypeNode[] { obj, obj })
                && !call.MemberVisibility.IsPresent && !call.MemberOwnerTypeParams.IsPresent
                && !call.MemberReturnType.IsPresent && !call.ArgTypes.IsPresent && !call.DeclarationId.IsPresent,
                "public static call lost its exact MemberRef, bound flag or optional presence");
            Require(call.Args.Value is [BirCast first, BirCast second] && first.Type.Value == obj && second.Type.Value == obj
                && !ReferenceEquals(first.E.Value, left) && !ReferenceEquals(second.E.Value, right)
                && ((BirGraphValue)first.E.Value).StructurallyEquals(left) && ((BirGraphValue)second.E.Value).StructurallyEquals(right)
                && ReferenceEquals(arguments[0], left) && ReferenceEquals(arguments[1], right)
                && !ReferenceEquals(call.Args.Value, arguments) && !ReferenceEquals(call.Sig.Value, reference.ParameterTypes.Value),
                "exact arguments were moved, reordered or shared instead of independently cloned/cast");
            SameCurrentRenderer(call, ExactStaticCall(refs, concat, text, new JsonArray(left.ToDom(), right.ToDom())));

            var item = typeof(List<>).GetProperty("Item")!.GetMethod!;
            var owner = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { text });
            IBirExpression receiver = new BirNew { Type = owner, Args = Array.Empty<IBirExpression>() };
            IBirExpression index = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) };
            var instancePlan = PlanExactDispatch(refs, item, owner, isStatic: false);
            Require(instancePlan.Result == text && instancePlan.Reference.ReturnType == new TypeNode.Tv("type", 0)
                && instancePlan.Arguments.SequenceEqual(new TypeNode[] { integer })
                && instancePlan.Reference.DeclaringType == owner && instancePlan.Visibility == null,
                "generic owner's closed result was confused with its open MemberRef return");
            var instance = ExactInstanceCall(refs, item, owner, receiver, new[] { index });
            var itemReference = instance.MemberRef.Value;
            Require(instance.OwnerType.Value == owner && instance.Method.Value == "get_Item" && instance.Virtual.Value
                && instance.DotktDeclarationIdentityBound.Value && instance.Ret.Value == text && instance.Sty.Value == text
                && instance.ResultTypeStamp == text && itemReference.ReturnType.Value == new TypeNode.Tv("type", 0)
                && itemReference.DeclaringType.Value == owner && itemReference.Kind.Value == MemberRefNode.Kinds.Method
                && itemReference.Name.Value == "get_Item" && itemReference.Assembly.Value == definingAssembly
                && itemReference.CallingConvention.Value == MemberRefNode.Instance && itemReference.GenericArity.Value == 0
                && itemReference.ParameterTypes.Value.SequenceEqual(new TypeNode[] { integer })
                && instance.Sig.Value.SequenceEqual(new TypeNode[] { integer })
                && instance.Recv.Value is BirCast receiverCast && receiverCast.Type.Value == owner
                && ReferenceEquals(receiverCast.E.Value, receiver) && Occurrences(instance, (BirGraphValue)receiver) == 1
                && instance.Args.Value is [BirCast indexCast] && indexCast.Type.Value == integer
                && !ReferenceEquals(indexCast.E.Value, index) && ((BirGraphValue)indexCast.E.Value).StructurallyEquals((BirGraphValue)index)
                && !instance.MemberVisibility.IsPresent && !instance.MemberOwnerTypeParams.IsPresent && !instance.MemberReturnType.IsPresent,
                "instance renderer lost receiver-once, open signature or the already-closed result/stamp");
            var closedItem = typeof(List<string>).GetProperty("Item")!.GetMethod!;
            var closedPlan = PlanExactDispatch(refs, closedItem, owner, isStatic: false);
            Require(closedPlan.Result == text && closedPlan.Reference.ReturnType == new TypeNode.Tv("type", 0),
                "a reflected closed MethodDef was substituted twice or leaked its closed signature into the MemberRef");
            SameCurrentRenderer(instance, ExactInstanceCall(refs, item, owner, ((BirGraphValue)receiver).ToDom(),
                new JsonArray(((BirGraphValue)index).ToDom())));

            var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var protectedPlan = PlanExactDispatch(refs, clone, obj, isStatic: false);
            Require(protectedPlan.Visibility == "protected" && protectedPlan.OwnerParameters == null
                && protectedPlan.Result == obj && protectedPlan.Reference.ReturnType == obj,
                "protected nongeneric access plan acquired inferred owner parameters or return facts");
            IBirExpression self = new BirThis();
            IBirExpression other = new BirNew { Type = obj, Args = Array.Empty<IBirExpression>() };
            var thisCall = ExactInstanceCall(refs, clone, obj, self, Array.Empty<IBirExpression>());
            var otherCall = ExactInstanceCall(refs, clone, obj, other, Array.Empty<IBirExpression>());
            foreach (var protectedCall in new[] { thisCall, otherCall })
                Require(protectedCall.MemberVisibility.Value == "protected"
                    && protectedCall.MemberOwnerTypeParams.IsPresent && protectedCall.MemberOwnerTypeParams.IsNull
                    && protectedCall.MemberReturnType.Value == obj && protectedCall.Ret.Value == obj && protectedCall.Sty.Value == obj
                    && protectedCall.Sig.HasValue && protectedCall.Sig.Value.Length == 0
                    && protectedCall.Args.HasValue && protectedCall.Args.Value.Length == 0
                    && protectedCall.DotktDeclarationIdentityBound.Value && protectedCall.Virtual.Value
                    && protectedCall.MemberRef.Value.Name.Value == "MemberwiseClone"
                    && protectedCall.MemberRef.Value.ParameterTypes.Value.Length == 0
                    && protectedCall.MemberRef.Value.ReturnType.Value == obj && !protectedCall.ArgTypes.IsPresent,
                    "protected access metadata lost explicit null/empty presence, signature or result");
            Require(thisCall.Recv.Value is BirThis && !ReferenceEquals(thisCall.Recv.Value, self)
                && Occurrences(thisCall, (BirGraphValue)self) == 0
                && otherCall.Recv.Value is BirCast otherCast && otherCast.Type.Value == obj
                && ReferenceEquals(otherCast.E.Value, other) && Occurrences(otherCall, (BirGraphValue)other) == 1,
                "protected this must remain lexical while a non-this receiver is cast exactly once");
            SameCurrentRenderer(thisCall, ExactInstanceCall(refs, clone, obj, ((BirGraphValue)self).ToDom(), new JsonArray()));
            SameCurrentRenderer(otherCall, ExactInstanceCall(refs, clone, obj, ((BirGraphValue)other).ToDom(), new JsonArray()));
        }
        finally { _refs = previousReferences; }
        Console.WriteLine("[typed exact CLR calls] self-test OK (real MethodDefs, open/closed signatures, argument copies, receiver-once, protected metadata and live renderer agreement)");
    }
}
