using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    // A view over the three real call owners. It neither creates a declaration
    // stand-in nor resolves a member; dispatch selection remains in the binder.
    sealed class SourceDispatchCall
    {
        readonly BirGraphValue node;
        SourceDispatchCall(BirGraphValue node) { this.node = node; }
        internal static SourceDispatchCall Read(BirGraphValue node) =>
            node is BirCallInstance or BirNativeInstance or BirGenericInstance ? new(node) : null;
        internal OptionalField<TypeNode> Owner {
            get => node switch { BirCallInstance value => value.OwnerType, BirNativeInstance value => value.Type,
                BirGenericInstance value => value.Type, _ => default };
            set { switch (node) { case BirCallInstance call: call.OwnerType = value; break;
                case BirNativeInstance call: call.Type = value; break; case BirGenericInstance call: call.Type = value; break; } }
        }
        internal OptionalField<IBirExpression> Receiver {
            get => node switch { BirCallInstance value => value.Recv, BirNativeInstance value => value.Recv,
                BirGenericInstance value => value.Recv, _ => default };
            set { switch (node) { case BirCallInstance call: call.Recv = value; break;
                case BirNativeInstance call: call.Recv = value; break; case BirGenericInstance call: call.Recv = value; break; } }
        }
        internal OptionalField<bool> Erased {
            get => node switch { BirCallInstance value => value._erasedConstraintDispatch,
                BirNativeInstance value => value._erasedConstraintDispatch, BirGenericInstance value => value._erasedConstraintDispatch,
                _ => default };
            set { switch (node) { case BirCallInstance call: call._erasedConstraintDispatch = value; break;
                case BirNativeInstance call: call._erasedConstraintDispatch = value; break;
                case BirGenericInstance call: call._erasedConstraintDispatch = value; break; } }
        }

        internal BirConstrainedCall ToConstrained(TypeNode.Tv receiver, TypeNode.Fqn owner)
        {
            var result = node switch {
                BirCallInstance call => new BirConstrainedCall {
                    Method = call.Method, Recv = call.Recv, Args = call.Args, Sig = call.Sig,
                    ProjectedMethodWitnesses = call.ProjectedMethodWitnesses, SelectedGenericCallable = call.SelectedGenericCallable,
                    TypeArgs = call.TypeArgs, ArgTypes = call.ArgTypes, MemberRef = call.MemberRef,
                    Ret = call.Ret.HasValue ? call.Ret : call.DynRet.HasValue ? call.DynRet : call.Ret,
                    Sty = call.Sty, DeclarationId = call.DeclarationId,
                    Super = call.Super, Prop = call.Prop, ShapeTypes = call.ShapeTypes,
                    PropertyName = call.PropertyName, PropertyAccessor = call.PropertyAccessor,
                    MemberVisibility = call.MemberVisibility, MemberOwnerTypeParams = call.MemberOwnerTypeParams,
                    MemberMethodTypeParams = call.MemberMethodTypeParams, MemberSignature = call.MemberSignature,
                    MemberReturnType = call.MemberReturnType, _exactExistentialBridgeOwnerCall = call._exactExistentialBridgeOwnerCall,
                    CalleeRet = call.CalleeRet, CalleeParams = call.CalleeParams,
                    SuspendCall = call.SuspendCall, DeclarationReferencedIntrinsic = call.DeclarationReferencedIntrinsic,
                    NullablePendingWitnesses = call.NullablePendingWitnesses, NullableWitnessCount = call.NullableWitnessCount,
                    DotktDeclarationIdentityBound = call.DotktDeclarationIdentityBound,
                    FunctionCallSignature = call.FunctionCallSignature,
                    ResolvedMemberParams = call.ResolvedMemberParams, ResolvedMemberReturn = call.ResolvedMemberReturn,
                    _capturedGenericArgument = call._capturedGenericArgument, _capturedGenericResult = call._capturedGenericResult,
                    _resolvedMethodTypeParams = call._resolvedMethodTypeParams,
                    _foreignDeclarationSignature = call._foreignDeclarationSignature,
                },
                BirNativeInstance call => new BirConstrainedCall {
                    Method = call.Method, Recv = call.Recv, Args = call.Args, Sig = call.Sig,
                    TypeArgs = call.TypeArgs, ArgTypes = call.ArgTypes, MemberRef = call.MemberRef,
                    Ret = call.Ret, Sty = call.Sty, CalleeParams = call.CalleeParams, SuspendCall = call.SuspendCall,
                    Super = call.Super, Prop = call.Prop,
                    ResolvedMemberParams = call.ResolvedMemberParams, ResolvedMemberReturn = call.ResolvedMemberReturn,
                    _capturedGenericArgument = call._capturedGenericArgument, _capturedGenericResult = call._capturedGenericResult,
                    _foreignDeclarationSignature = call._foreignDeclarationSignature,
                    _resolvedMethodTypeParams = call._resolvedMethodTypeParams,
                },
                BirGenericInstance call => new BirConstrainedCall {
                    Method = call.Method, Recv = call.Recv, Args = call.Args, TypeArgs = call.TypeArgs,
                    MemberRef = call.MemberRef, Ret = call.Ret, Sty = call.Sty, SuspendCall = call.SuspendCall,
                    Super = call.Super,
                    ResolvedMemberParams = call.ResolvedMemberParams, ResolvedMemberReturn = call.ResolvedMemberReturn,
                    _capturedGenericArgument = call._capturedGenericArgument, _capturedGenericResult = call._capturedGenericResult,
                    _foreignDeclarationSignature = call._foreignDeclarationSignature,
                    _resolvedMethodTypeParams = call._resolvedMethodTypeParams,
                },
                _ => throw new InvalidOperationException("Constrained dispatch requires an actual instance call"),
            };
            if (result.ResolvedMemberParams.HasValue) result.Sig = result.ResolvedMemberParams;
            result.RecvType = BirGraphValue.CopyType(receiver);
            result.Iface = BirGraphValue.CopyType(owner);
            return result;
        }

        internal BirGraphValue ToForeign(TypeNode.Fqn owner)
        {
            var result = node switch {
                BirNativeInstance call => call,
                BirCallInstance call => new BirNativeInstance {
                    Method = call.Method, Recv = call.Recv, Args = call.Args, TypeArgs = call.TypeArgs,
                    Sig = call.Sig, CalleeParams = call.CalleeParams, MemberRef = call.MemberRef, Prop = call.Prop,
                    Ret = call.Ret, Sty = call.Sty, ArgTypes = call.ArgTypes, Super = call.Super,
                    ClrOwnerResolved = call.ClrOwnerResolved, SuspendCall = call.SuspendCall,
                    ResolvedMemberParams = call.ResolvedMemberParams, ResolvedMemberReturn = call.ResolvedMemberReturn,
                    _capturedGenericArgument = call._capturedGenericArgument, _capturedGenericResult = call._capturedGenericResult,
                    _resolvedMethodTypeParams = call._resolvedMethodTypeParams,
                    _foreignDeclarationSignature = call._foreignDeclarationSignature,
                },
                BirGenericInstance call => new BirNativeInstance {
                    Method = call.Method, Recv = call.Recv, Args = call.Args, TypeArgs = call.TypeArgs,
                    MemberRef = call.MemberRef, Ret = call.Ret, Sty = call.Sty, Super = call.Super,
                    Dispatch = call.Dispatch, SuspendCall = call.SuspendCall,
                    ResolvedMemberParams = call.ResolvedMemberParams, ResolvedMemberReturn = call.ResolvedMemberReturn,
                    _capturedGenericArgument = call._capturedGenericArgument, _capturedGenericResult = call._capturedGenericResult,
                    _foreignDeclarationSignature = call._foreignDeclarationSignature,
                    _resolvedMethodTypeParams = call._resolvedMethodTypeParams,
                },
                _ => throw new InvalidOperationException("Foreign constraint dispatch requires an actual instance call"),
            };
            result.Type = BirGraphValue.CopyType(owner);
            return result;
        }
    }

    static BirGraphValue RestoreSourceDispatch(BirConstrainedCall call, TypeNode.Fqn owner, bool local)
    {
        if (local) return new BirCallInstance {
            OwnerType = BirGraphValue.CopyType(owner), Virtual = true, Method = call.Method,
            Recv = call.Recv, Args = call.Args, Sig = call.Sig, TypeArgs = call.TypeArgs,
            MemberRef = call.MemberRef, Ret = call.Ret, Sty = call.Sty, DeclarationId = call.DeclarationId,
            Super = call.Super, Prop = call.Prop, ShapeTypes = call.ShapeTypes,
            PropertyName = call.PropertyName, PropertyAccessor = call.PropertyAccessor,
            MemberVisibility = call.MemberVisibility, MemberOwnerTypeParams = call.MemberOwnerTypeParams,
            MemberMethodTypeParams = call.MemberMethodTypeParams, MemberSignature = call.MemberSignature,
            MemberReturnType = call.MemberReturnType, _exactExistentialBridgeOwnerCall = call._exactExistentialBridgeOwnerCall,
            ArgTypes = call.ArgTypes, CalleeRet = call.CalleeRet, CalleeParams = call.CalleeParams,
            SuspendCall = call.SuspendCall, DeclarationReferencedIntrinsic = call.DeclarationReferencedIntrinsic,
            NullablePendingWitnesses = call.NullablePendingWitnesses, NullableWitnessCount = call.NullableWitnessCount,
            DotktDeclarationIdentityBound = call.DotktDeclarationIdentityBound,
            FunctionCallSignature = call.FunctionCallSignature,
            ResolvedMemberParams = call.ResolvedMemberParams, ResolvedMemberReturn = call.ResolvedMemberReturn,
            _capturedGenericArgument = call._capturedGenericArgument, _capturedGenericResult = call._capturedGenericResult,
            _resolvedMethodTypeParams = call._resolvedMethodTypeParams,
            _foreignDeclarationSignature = call._foreignDeclarationSignature,
        };
        return new BirNativeInstance {
            Type = BirGraphValue.CopyType(owner), Method = call.Method, Recv = call.Recv, Args = call.Args,
            Sig = call.Sig, TypeArgs = call.TypeArgs, MemberRef = call.MemberRef, Ret = call.Ret,
            Sty = call.Sty, ArgTypes = call.ArgTypes, CalleeParams = call.CalleeParams, SuspendCall = call.SuspendCall,
            Prop = call.Prop, Super = call.Super,
            ResolvedMemberParams = call.ResolvedMemberParams, ResolvedMemberReturn = call.ResolvedMemberReturn,
            _capturedGenericArgument = call._capturedGenericArgument, _capturedGenericResult = call._capturedGenericResult,
            _foreignDeclarationSignature = call._foreignDeclarationSignature,
            _resolvedMethodTypeParams = call._resolvedMethodTypeParams,
        };
    }
}
