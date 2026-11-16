using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void RewriteSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning carrier body rewriting: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        try
        {
            var variable = new TypeNode.Tv("type", 0);
            var any = new TypeNode.Fqn("kotlin.Any");
            var logical = new TypeNode.Fqn("BodyBox", new TypeNode[] { new TypeNode.Star() });
            var exact = new TypeNode.Fqn("BodyBox", new TypeNode[] { variable });
            var getter = new BirFunctionDeclaration { Name = "value", DeclarationId = "get-value", Ret = variable,
                PropertyName = "value", PropertyAccessor = "get", PropertyAssociation = "BodyBox:field:value",
                Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirReturn {
                    Value = new BirFieldGet { OwnerType = exact, Name = "value", Recv = new BirThis(), MemberType = variable } } } };
            var setter = new BirFunctionDeclaration { Name = "value", DeclarationId = "set-value", Ret = new TypeNode.Fqn("kotlin.Unit"),
                PropertyName = "value", PropertyAccessor = "set", PropertyAssociation = "BodyBox:field:value",
                Params = new[] { new BirParameter { Name = "newValue", Type = variable } }, Body = Array.Empty<IBirStatement>() };
            var method = new BirFunctionDeclaration { Name = "read", DeclarationId = "read-box", Ret = variable,
                Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
            var eventAccessor = new BirFunctionDeclaration { Name = "add", DeclarationId = "add-box", Ret = new TypeNode.Fqn("kotlin.Unit"),
                Params = new[] { new BirParameter { Name = "handler", Type = any } }, Body = Array.Empty<IBirStatement>() };
            var type = new BirClassDefinition { Name = "BodyBox", Kind = "class", Vis = "public",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { getter, setter, method, eventAccessor },
                Fields = new[] { new BirFieldDeclaration { Name = "value", Type = variable } } };
            var file = new BirFileDeclaration { FileClass = "BodySourceTest", Types = new IBirTypeDeclaration[] { type } };
            var context = SourceSynthesisTestContext(file, refs);
            SynthesizeSource(context.Owners["BodyBox"], context);
            var state = new SourceRewriteState(context, (_, _) => null);
            var carrier = new TypeNode.Fqn(context.Owners["BodyBox"].ErasedName);
            var receiver = new BirValueBlock { Type = logical, Result = new BirLocal { Name = "box", Sty = logical } };
            var field = new BirFieldGet { OwnerType = logical, Name = "value", Recv = receiver,
                MemberType = variable, Sty = any, GeneratedEqualsFieldRead = false };
            var selected = (BirCallInstance)BindStarFieldThroughCanonicalGetterSource(field, state);
            Require(ReferenceEquals(selected.Recv.Value, receiver) && selected.OwnerType.Value == carrier
                && selected.DeclarationId.Value == ExistentialSlotIdentity("get-value", "BodyBox")
                && selected.Ret.Value == any && selected.Sty.Value == any && selected.Virtual.Value
                && !field.GeneratedEqualsFieldRead.IsPresent,
                "canonical getter lost its exact selected slot, result or receiver occurrence");
            var addressed = new BirFieldGet { OwnerType = logical, Name = "value", Recv = receiver,
                MemberType = variable, _nativeFieldAddress = true, DataClassCopyDefault = true };
            Require(ReferenceEquals(BindStarFieldThroughCanonicalGetterSource(addressed, state), addressed)
                && addressed.DataClassCopyDefault.Value, "an addressed storage field entered getter binding");
            getter.Virtual = true;
            var overridable = new BirFieldGet { OwnerType = logical, Name = "value", Recv = receiver, MemberType = variable };
            Require(ReferenceEquals(BindStarFieldThroughCanonicalGetterSource(overridable, state), overridable),
                "an overridable source getter was guessed into direct field access");
            getter.Virtual = false;

            var nested = new BirFunctionDeclaration { Name = "nested", Ret = any,
                Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
            var assigned = new BirValueBlock { Type = any,
                Stmts = new IBirStatement[] { new BirLocalFunction { Id = "nested", Decl = nested } },
                Result = new BirLocal { Name = "assigned", Sty = any } };
            var write = new BirSetField { OwnerType = logical, Name = "value", Recv = receiver, Value = assigned };
            var statement = (BirExpressionStatement)BindProjectedFieldStorageSource(write, state);
            var writeCall = (BirCallInstance)statement.Expr.Value;
            Require(ReferenceEquals(writeCall.Recv.Value, receiver) && ReferenceEquals(writeCall.Args.Value[0], assigned)
                && ReferenceEquals(((BirLocalFunction)assigned.Stmts.Value[0]).Decl.Value, nested)
                && writeCall.PropertyAccessor.Value == "set" && writeCall.OwnerType.Value == carrier,
                "direct storage setter cloned executable inputs or lost its statement boundary");

            var constraintCall = new BirCallInstance { OwnerType = new TypeNode.Fqn("BodyBox", new TypeNode[] { any }),
                Method = "read", Recv = new BirLocal { Name = "constrained", Sty = new TypeNode.Tv("method", 0) } };
            BindProjectedConstraintMemberSource(constraintCall, null, new[] { new BirTypeParameter { Name = "M",
                Constraints = new TypeNode[] { new TypeNode.Tv("method", 1) } }, new BirTypeParameter { Name = "Bound",
                ErasedInnerConstraints = new TypeNode[] { logical } } }, state);
            Require(constraintCall.OwnerType.Value == logical, "transitive declaration-owned erased bound did not close the selected member owner");

            var localEvent = new BirEventAdd { LocalAccessor = true, AccessorOwner = logical, Accessor = "add",
                DeclarationId = "add-box", Sig = new TypeNode[] { any }, Recv = receiver,
                Handler = new BirLocal { Name = "handler" } };
            BindLocalEventAccessorSource(localEvent, state);
            Require(ReferenceEquals(localEvent.Recv.Value, receiver) && localEvent.AccessorOwner.Value == carrier
                && localEvent.DeclarationId.Value == ExistentialSlotIdentity("add-box", "BodyBox"),
                "selected local event edge lost its identity or its live receiver");

            var arraySemantic = new TypeNode.Fqn("BodyBox", new TypeNode[] { any });
            // Ordinary source-generic values already use their nominal carrier.
            // The array flow retains the distinct Kotlin element separately,
            // just as initializer/local normalization publishes it in production.
            var array = new BirLocal { Name = "array", Sty = new TypeNode.Array(carrier) };
            state.ArrayProjection(array, arraySemantic);
            var read = new BirArrayGet { Array = array, Index = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"),
                Value = new BirLiteral.IntegerLiteral(0) }, Elem = arraySemantic };
            var projected = (BirCast)BindProjectedArrayReadSource(read, state);
            Require(ReferenceEquals(projected.E.Value, read) && ReferenceEquals(read.Array.Value, array)
                && projected.Type.Value == carrier && projected._exactBridgeCast.Value
                && state.ProjectedType(projected)?.Name == "BodyBox"
                && ReferenceEquals(BindProjectedArrayReadSource(read, state), read),
                "projected array read did not retain its single actual operand and semantic projection");
            var generic = new BirCallStatic { Owner = new TypeNode.Fqn("BodySourceTest"), Method = "arrayIdentity",
                Sig = new TypeNode[] { new TypeNode.Array(new TypeNode.Tv("method", 0)) },
                TypeArgs = new TypeNode[] { arraySemantic }, Args = new IBirExpression[] { array },
                Ret = new TypeNode.Array(arraySemantic), DynRet = OptionalField<TypeNode>.Null };
            Require(ReferenceEquals(BindProjectedArrayGenericCallSource(generic, state), generic)
                && generic.TypeArgs.Value[0] == carrier && generic.Ret.Value == new TypeNode.Array(carrier)
                && generic.DynRet.IsNull && ReferenceEquals(generic.Args.Value[0], array)
                && state.ArrayProjection(generic) == arraySemantic,
                "generic array result lost its physical instantiation, nullable presence or live argument");

            var opaque = DefaultBirCarrier.Create(0, new BirLocal { Name = "opaque", Sty = logical });
            var opaqueBytes = ((BirOpaqueBytes)opaque.Args.Value[2]).Bytes.Value;
            var bodyRead = new BirCallInstance { OwnerType = logical, Method = "read", DeclarationId = "read-box",
                Recv = new BirLocal { Name = "box", Sty = logical }, Args = Array.Empty<IBirExpression>(),
                Ret = variable, Sty = any, Sig = Array.Empty<TypeNode>() };
            var body = new IBirStatement[] { new BirExpressionStatement { Expr = bodyRead } };
            // Explicitly admit a newly attached method through the existing
            // declaration producer, before the single owning body walk.
            var use = new BirFunctionDeclaration { Name = "use", Ret = any,
                Params = new[] { new BirParameter { Name = "ignored", Type = logical, Attrs = new[] { opaque } } }, Body = body };
            context.AttachMethod(type, use);
            var sourceTypes = file.Types.Value;
            RewriteCarrierBodiesSource(new[] { file }, context, Array.Empty<SharedSyntheticSynthesis.CellStorage>(), (_, _) => null);
            Require(ReferenceEquals(file.Types.Value, sourceTypes) && ReferenceEquals(use.Body.Value, body)
                && ((BirExpressionStatement)body[0]).Expr.Value is BirCallInstance actual
                && ReferenceEquals(actual, bodyRead) && actual.OwnerType.Value == carrier
                && ReferenceEquals(((BirOpaqueBytes)opaque.Args.Value[2]).Bytes.Value, opaqueBytes),
                "whole body rewriting replaced owning declarations/arrays or entered executable carrier bytes");
            Console.WriteLine("[owning carrier body rewriting] self-test OK (selected fields/members, constraint frames, event edges, array projection, live operands and opaque carriers)");
        }
        finally { OwnerConstrainedMethodLowering.Reset(refs); }
    }
}
