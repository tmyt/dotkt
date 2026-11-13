using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NetInteropBinding
{
    internal static BirFileDeclaration Apply(BirFileDeclaration file, ReferenceMetadataIndex references)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            var result = node switch {
                BirCallStatic call when !call.ClrAwaitBridge.OrDefault() => LowerCall(new(call), references),
                BirCallInstance call when !call.ClrAwaitBridge.OrDefault() => LowerCall(new(call), references),
                BirFieldGet field => LowerField(field, field.OwnerType, field.Name, field.Recv, default,
                    field.CompanionCall.OrDefault(), field.MemberVisibility.OrDefault(), field.Sty, field.Ret, references),
                BirStaticFieldGet field => LowerField(field, field.OwnerType, field.Name, default, default,
                    field.CompanionCall.OrDefault(), field.MemberVisibility.OrDefault(), field.Sty, field.Ret, references),
                BirFieldSetExpression field => LowerField(field, field.OwnerType, field.Name, field.Recv, field.Value,
                    field.CompanionCall.OrDefault(), field.MemberVisibility.OrDefault(), default, default, references, write: true),
                BirStaticFieldSet field => LowerField(field, field.OwnerType, field.Name, default, field.Value,
                    field.CompanionCall.OrDefault(), field.MemberVisibility.OrDefault(), default, default, references, write: true),
                BirSetField field => LowerField(field, field.OwnerType, field.Name, field.Recv, field.Value,
                    false, field.MemberVisibility.OrDefault(), default, default, references, write: true),
                BirEventGet handle => LowerEvent(handle, references),
                BirNewSuspendLambda lambda => LowerSuspend(lambda, references),
                _ => node,
            };
            // Classification is consumed even when a genuine Kotlin carrier
            // keeps its ordinary shape. No payload is entered to clear it.
            switch (node)
            {
                case BirCallStatic value: value.CompanionCall = default; break;
                case BirCallInstance value: value.CompanionCall = default; break;
                case BirFieldGet value: value.CompanionCall = default; break;
                case BirStaticFieldGet value: value.CompanionCall = default; break;
                case BirFieldSetExpression value: value.CompanionCall = default; break;
                case BirStaticFieldSet value: value.CompanionCall = default; break;
                case BirEventGet value: value.CompanionCall = default; break;
            }
            return result;
        }
        Walk(file);
        return file;
    }

    sealed class SourceCall
    {
        internal BirGraphNode Node;
        internal OptionalField<TypeNode> Owner, Ret, Sty;
        internal OptionalField<string> Method, Property;
        internal string Visibility;
        internal bool Static, Companion;
        internal OptionalField<TypeNode[]> Signature, Shapes, ArgumentTypes, TypeArguments;
        internal OptionalField<IBirExpression> Receiver;
        internal OptionalField<IBirExpression[]> Arguments;
        internal OptionalField<bool> Super, Suspend;
        internal OptionalField<CapturedGenericArgument[]> CapturedArguments;
        internal OptionalField<TypeNode> CapturedResult;

        internal SourceCall(BirCallStatic call)
        {
            Node = call; Static = true; Owner = call.OwnerType; Ret = call.Ret; Sty = call.Sty;
            Method = call.Method; Property = call.Prop; Visibility = call.MemberVisibility.OrDefault(); Companion = call.CompanionCall.OrDefault();
            Signature = call.Sig; Shapes = call.ShapeTypes; ArgumentTypes = call.ArgTypes; TypeArguments = call.TypeArgs;
            Arguments = call.Args; Suspend = call.SuspendCall; CapturedArguments = call._capturedGenericArgument; CapturedResult = call._capturedGenericResult;
        }
        internal SourceCall(BirCallInstance call)
        {
            Node = call; Owner = call.OwnerType; Ret = call.Ret; Sty = call.Sty;
            Method = call.Method; Property = call.Prop; Visibility = call.MemberVisibility.OrDefault(); Companion = call.CompanionCall.OrDefault();
            Signature = call.Sig; Shapes = call.ShapeTypes; ArgumentTypes = call.ArgTypes; TypeArguments = call.TypeArgs;
            Receiver = call.Recv; Arguments = call.Args; Super = call.Super; Suspend = call.SuspendCall;
            CapturedArguments = call._capturedGenericArgument; CapturedResult = call._capturedGenericResult;
        }
        internal void Retain(CallSelection plan)
        {
            switch (Node)
            {
                case BirCallStatic call:
                    call.Method = plan.Method;
                    if (plan.ClearProperty) call.Prop = default;
                    if (plan.Static) call.OwnerType = plan.Owner;
                    break;
                case BirCallInstance call:
                    call.Method = plan.Method;
                    if (plan.ClearProperty) call.Prop = default;
                    if (plan.Static) call.OwnerType = plan.Owner;
                    break;
            }
        }
    }

    static OptionalField<T> ValueOnly<T>(OptionalField<T> value) => value.HasValue ? value : default;
    static OptionalField<SourceTypeVector> ForeignSignature(SourceCall call, bool isStatic)
        => !isStatic && call.Signature.HasValue ? OptionalField<SourceTypeVector>.Of(SourceTypeVector.Create(call.Signature.Value)) : default;
    static BirGraphValue LowerCall(SourceCall call, ReferenceMetadataIndex refs)
    {
        var args = call.Arguments.OrEmpty();
        var signatures = SelectSignatures(call.ArgumentTypes.OrDefault(), call.Shapes.OrDefault(), call.Signature.OrDefault());
        var plan = SelectCall(refs, call.Owner.OrDefault(), call.Method.OrDefault(), call.Static, call.Companion,
            call.Property.OrDefault(), call.Visibility, args.Length, call.TypeArguments.OrEmpty().Length > 0,
            signatures.Declaration, signatures.Declared);
        if (plan == null) return call.Node;
        if (plan.Kind == NativeCallKind.Restricted) { call.Retain(plan); return call.Node; }
        var receiver = ValueOnly(call.Receiver);
        if (plan.Kind == NativeCallKind.Event)
            return new BirEventGet { Type = plan.Owner, Name = plan.Method, Static = plan.Static,
                Recv = plan.Static ? default : receiver, Sty = ValueOnly(call.Sty), Ret = ValueOnly(call.Ret) };
        if (plan.Kind == NativeCallKind.EnumName)
            return new BirObjectMethod { Method = "ToString", Recv = receiver };
        if (plan.Kind == NativeCallKind.EnumOrdinal)
            return EnumValueLowering.ReferencedOrdinal(receiver.OrDefault(), plan.Owner, refs);
        if (plan.Kind == NativeCallKind.EnumCompare)
            return new BirBinaryOperation { Op = "-",
                Lhs = EnumValueLowering.ReferencedOrdinal(receiver.OrDefault(), plan.Owner, refs),
                Rhs = EnumValueLowering.ReferencedOrdinal(args[0], plan.Owner, refs) };
        var foreign = ForeignSignature(call, plan.Static);
        var sty = ValueOnly(call.Sty);
        var captured = ValueOnly(call.CapturedArguments);
        var capturedResult = ValueOnly(call.CapturedResult);
        var suspend = ValueOnly(call.Suspend);
        var super = ValueOnly(call.Super);
        if (plan.Kind == NativeCallKind.Generic)
        {
            var signature = NormalizeSignature(signatures.Generic);
            return plan.Static ? new BirGenericStatic {
                Type = plan.Owner, Method = plan.Method, TypeArgs = call.TypeArguments, ResolvedMemberParams = signature,
                Ret = ValueOnly(call.Ret), Args = args, Sty = sty, SuspendCall = suspend,
                _capturedGenericArgument = captured, _capturedGenericResult = capturedResult,
            } : new BirGenericInstance {
                Type = plan.Owner, Method = plan.Method, TypeArgs = call.TypeArguments, ResolvedMemberParams = signature,
                Ret = ValueOnly(call.Ret), Recv = receiver, Args = args, Sty = sty, SuspendCall = suspend, Super = super,
                _capturedGenericArgument = captured, _capturedGenericResult = capturedResult, _foreignDeclarationSignature = foreign,
            };
        }
        if (plan.Kind == NativeCallKind.PropertyGet)
            return new BirPropertyGet { Type = plan.Owner, Name = plan.Method, Static = plan.Static,
                Ret = call.Ret, Recv = plan.Static ? default : receiver, Sty = sty, Super = plan.Static ? default : super,
                _capturedGenericArgument = captured, _capturedGenericResult = capturedResult, _foreignDeclarationSignature = foreign };
        if (plan.Kind == NativeCallKind.PropertySet)
            return new BirPropertySet { Type = plan.Owner, Name = plan.Method, Static = plan.Static,
                Recv = plan.Static ? default : receiver, Value = args.Length == 0 ? default : OptionalField<IBirExpression>.Of(args[0]),
                Sty = sty, Super = plan.Static ? default : super,
                _capturedGenericArgument = captured, _capturedGenericResult = capturedResult, _foreignDeclarationSignature = foreign };
        if (plan.Kind == NativeCallKind.Operator)
            return new BirNativeStatic { Type = plan.Owner, Method = plan.Method,
                ArgTypes = new[] { BirGraphValue.CopyType(plan.Owner) }.Concat(signatures.Operator).ToArray(),
                Args = new[] { call.Receiver.OrDefault() }.Concat(args).ToArray(), Ret = call.Ret, Sty = sty, SuspendCall = suspend,
                _capturedGenericArgument = captured, _capturedGenericResult = capturedResult, _foreignDeclarationSignature = foreign };
        var argumentTypes = signatures.Declared;
        OptionalField<TypeNode[]> resolved = call.Signature.HasValue ? NormalizeSignature(call.Signature.Value) : default;
        return plan.Static ? new BirNativeStatic {
            Type = plan.Owner, Method = plan.Method, ArgTypes = argumentTypes, ResolvedMemberParams = resolved,
            Ret = call.Ret, Args = args, Sty = sty, SuspendCall = suspend,
            _capturedGenericArgument = captured, _capturedGenericResult = capturedResult,
        } : new BirNativeInstance {
            Type = plan.Owner, Method = plan.Method, ArgTypes = argumentTypes, ResolvedMemberParams = resolved,
            Ret = call.Ret, Recv = receiver, Args = args, Sty = sty, SuspendCall = suspend, Super = super,
            _capturedGenericArgument = captured, _capturedGenericResult = capturedResult, _foreignDeclarationSignature = foreign,
        };
    }

    static BirGraphValue LowerField(BirGraphNode field, OptionalField<TypeNode> owner, OptionalField<string> name,
        OptionalField<IBirExpression> receiver, OptionalField<IBirExpression> value, bool companion,
        string visibility, OptionalField<TypeNode> sty, OptionalField<TypeNode> ret, ReferenceMetadataIndex refs, bool write = false)
    {
        var plan = SelectField(refs, owner.OrDefault(), name.OrDefault(), null, companion, visibility);
        if (plan == null) return field;
        if (plan.Restricted)
        {
            switch (field)
            {
                case BirFieldGet node: node.Name = plan.Name; if (plan.Static) node.OwnerType = plan.Owner; break;
                case BirStaticFieldGet node: node.Name = plan.Name; if (plan.Static) node.OwnerType = plan.Owner; break;
                case BirFieldSetExpression node: node.Name = plan.Name; if (plan.Static) node.OwnerType = plan.Owner; break;
                case BirStaticFieldSet node: node.Name = plan.Name; if (plan.Static) node.OwnerType = plan.Owner; break;
                case BirSetField node: node.Name = plan.Name; if (plan.Static) node.OwnerType = plan.Owner; break;
            }
            return field;
        }
        var recv = plan.Static ? default : ValueOnly(receiver);
        if (!write) return new BirPropertyGet { Type = plan.Owner, Name = plan.Name, Static = plan.Static,
            Recv = recv, Sty = ValueOnly(sty), Ret = ValueOnly(ret) };
        var lowered = new BirPropertySet { Type = plan.Owner, Name = plan.Name, Static = plan.Static, Recv = recv, Value = value };
        return field is IBirStatement ? new BirExpressionStatement { Expr = lowered } : lowered;
    }

    static BirGraphValue LowerEvent(BirEventGet handle, ReferenceMetadataIndex refs)
    {
        if (handle.CompanionCall.OrDefault())
        {
            handle.Static = CompanionEventStatic(refs, handle.Type.OrDefault());
            if (handle.Static.Value) handle.Recv = default;
        }
        if (handle.Static.OrDefault() && StaticEventOwner(refs, handle.Type.OrDefault()) is { } owner) handle.Type = owner;
        return handle;
    }

    static BirGraphValue LowerSuspend(BirNewSuspendLambda lambda, ReferenceMetadataIndex refs)
    {
        if (lambda.ExternalCompanionOwner.HasValue)
        {
            var owner = lambda.ExternalCompanionOwner.Value;
            lambda.ExternalCompanionOwner = default;
            ValidateSuspendCompanion(refs, owner);
        }
        return lambda;
    }
}
