using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    static readonly TypeNode Object = new TypeNode.Fqn("object");
    static readonly TypeNode Int = new TypeNode.Fqn("int");
    static readonly TypeNode String = new TypeNode.Fqn("string");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");
    static readonly TypeNode[] RuntimeSignature = { Type, Int, String, new TypeNode.Array(String), new TypeNode.Array(Type),
        new TypeNode.Array(Int), new TypeNode.Array(Type), new TypeNode.Array(Int), new TypeNode.Array(Int), new TypeNode.Array(Object) };

    internal static BirLocal Local(int index) => new() { Name = "p" + index };
    internal static BirCast Cast(TypeNode type, IBirExpression value) => new() { Type = type, E = OptionalField<IBirExpression>.Of(value) };
    static BirClassReference ClassRef(TypeNode type) => new() { Type = type };
    static BirConstant Number(int value) => new() { Type = Int, Value = new BirLiteral.IntegerLiteral(value) };
    static BirConstant Text(string value) => new() { Type = String, Value = new BirLiteral.TextLiteral(value) };
    static BirConstant Null() => new() { Type = Object, Value = new BirLiteral.NullLiteral() };
    static BirNewArray NewArray(TypeNode element, IEnumerable<IBirExpression> elements) => new() { Elem = element, Elems = elements.ToArray() };
    static IBirExpression Copy(IBirExpression expression) => (IBirExpression)((BirGraphValue)expression).DeepClone();

    // These are the four actual invocation dialects admitted by the existing
    // capture lowering, not an inference from a method name or result type.
    internal sealed class Invocation
    {
        internal readonly BirGraphValue Node;
        internal readonly string Method;
        internal readonly OptionalField<TypeNode[]> TypeArguments;
        internal readonly OptionalField<IBirExpression[]> Arguments;
        internal readonly BirMemberReference Member;
        internal readonly BirTypeParameter[] SelectedFrame;
        internal readonly TypeNode Owner;
        internal readonly IBirExpression Receiver;
        internal readonly bool Instance, Super;

        internal Invocation(BirGraphValue node)
        {
            Node = node;
            switch (node)
            {
                case BirCallStatic value:
                    Method = value.Method.OrDefault(); TypeArguments = value.TypeArgs; Arguments = value.Args;
                    Owner = value.OwnerType.OrDefault() ?? value.Owner.OrDefault() ?? value.CalleeOwner.OrDefault();
                    Member = value.MemberRef.OrDefault(); SelectedFrame = value._resolvedMethodTypeParams.OrDefault(); break;
                case BirCallInstance value:
                    Method = value.Method.OrDefault(); TypeArguments = value.TypeArgs; Arguments = value.Args;
                    Owner = value.OwnerType.OrDefault(); Receiver = value.Recv.OrDefault();
                    Member = value.MemberRef.OrDefault(); SelectedFrame = value._resolvedMethodTypeParams.OrDefault();
                    Instance = true; Super = value.Super.OrDefault(); break;
                case BirGenericStatic value:
                    Method = value.Method.OrDefault(); TypeArguments = value.TypeArgs; Arguments = value.Args; Owner = value.Type.OrDefault();
                    Member = value.MemberRef.OrDefault(); SelectedFrame = value._resolvedMethodTypeParams.OrDefault(); break;
                case BirGenericInstance value:
                    Method = value.Method.OrDefault(); TypeArguments = value.TypeArgs; Arguments = value.Args; Owner = value.Type.OrDefault();
                    Member = value.MemberRef.OrDefault(); SelectedFrame = value._resolvedMethodTypeParams.OrDefault(); Receiver = value.Recv.OrDefault();
                    Instance = true; Super = value.Super.OrDefault(); break;
                default: throw new InvalidOperationException("Not a captured invocation dialect");
            }
        }

        internal static bool Admits(BirGraphValue node) => node is BirCallStatic or BirCallInstance or BirGenericStatic or BirGenericInstance;

        internal BirGraphValue Forward(TypeNode.Fqn owner, int arity, TypeNode[] parameters, TypeNode result, bool local)
        {
            var copy = Node.DeepClone();
            var types = Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
            var arguments = parameters.Select((_, index) => (IBirExpression)Local(index + (Instance ? 1 : 0))).ToArray();
            switch (copy)
            {
                case BirCallStatic value:
                    value._resolvedMethodTypeParams = default; value.Sty = default; value.DynRet = default; value.Owner = owner; value.TypeArgs = types;
                    value.Ret = result; value.Args = arguments;
                    if (local) value.Sig = parameters; else { value.ResolvedMemberParams = default; value.ResolvedMemberReturn = result; }
                    break;
                case BirCallInstance value:
                    value._resolvedMethodTypeParams = default; value.Sty = default; value.DynRet = default; value.OwnerType = owner;
                    value.TypeArgs = types; value.Ret = result; value.Args = arguments; value.Recv = Local(0);
                    if (local) value.Sig = parameters; else { value.ResolvedMemberParams = default; value.ResolvedMemberReturn = result; }
                    break;
                case BirGenericStatic value:
                    value._resolvedMethodTypeParams = default; value.Sty = default; value.Type = owner; value.TypeArgs = types;
                    value.Ret = result; value.Args = arguments; value.ResolvedMemberParams = default; value.ResolvedMemberReturn = result; break;
                case BirGenericInstance value:
                    value._resolvedMethodTypeParams = default; value.Sty = default; value.Type = owner; value.TypeArgs = types;
                    value.Ret = result; value.Args = arguments; value.Recv = Local(0);
                    value.ResolvedMemberParams = default; value.ResolvedMemberReturn = result; break;
            }
            if (!local) new Invocation(copy).Member.DeclaringType = owner;
            return copy;
        }

        internal BirGraphValue SuperForward(TypeNode[] parameters, TypeNode result, int arity, bool local)
        {
            var copy = Node.DeepClone();
            var types = Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
            var arguments = parameters.Select((_, index) => (IBirExpression)Local(index)).ToArray();
            switch (copy)
            {
                case BirCallInstance value:
                    value._resolvedMethodTypeParams = default; value.Sty = default; value.DynRet = default;
                    value.Recv = new BirThis(); value.TypeArgs = types; value.Args = arguments; value.Ret = result;
                    if (!local) value.ResolvedMemberReturn = result; return value;
                case BirGenericInstance value:
                    value._resolvedMethodTypeParams = default; value.Sty = default; value.Recv = new BirThis();
                    value.TypeArgs = types; value.Args = arguments; value.Ret = result; value.ResolvedMemberReturn = result; return value;
                default: throw new InvalidOperationException("A captured super call must be an instance invocation");
            }
        }
    }

    internal static BirGraphValue AdapterForward(BirGraphValue forward, TypeNode.Fqn liftedReceiver,
        Func<TypeNode, TypeNode> classFrame)
    {
        var invocation = forward.DeepClone();
        var source = new Invocation(forward);
        TypeNode CloseParameter(TypeNode type) => Map(type, variable => variable.Scope == "type"
            ? liftedReceiver.Args?[variable.I] ?? throw new InvalidOperationException("Captured receiver has no owner frame") : variable);
        var arguments = Enumerable.Range(0, source.Arguments.Value.Length).Select(index => (IBirExpression)Cast(
            classFrame(CloseParameter(SignatureValueTypes.Of(source.Member.ParameterTypes.Value[index]))),
            new BirArrayGet { Array = new BirLocal { Name = "values" }, Index = Number(index + 1), Elem = Object })).ToArray();
        var receiver = new BirByRefLoad { Local = "receiver", Elem = classFrame(liftedReceiver) };
        switch (invocation)
        {
            case BirGenericInstance value:
                value.Type = classFrame(value.Type.Value); value.Ret = MapField(value.Ret); value.ResolvedMemberReturn = MapField(value.ResolvedMemberReturn);
                value.TypeArgs = value.TypeArgs.Value.Select(classFrame).ToArray(); value.Recv = receiver; value.Args = arguments; break;
            case BirCallInstance value:
                value.Ret = MapField(value.Ret); value.ResolvedMemberReturn = MapField(value.ResolvedMemberReturn);
                value.TypeArgs = value.TypeArgs.Value.Select(classFrame).ToArray(); value.Recv = receiver; value.Args = arguments; break;
            default: throw new InvalidOperationException("A captured receiver requires an instance invocation");
        }
        new Invocation(invocation).Member.DeclaringType = classFrame(liftedReceiver);
        return invocation;
        OptionalField<TypeNode> MapField(OptionalField<TypeNode> field) => field.HasValue ? OptionalField<TypeNode>.Of(classFrame(field.Value)) : field;
    }

    sealed record CaptureFacts(OptionalField<CapturedGenericArgument[]> Arguments, TypeNode Result, string Target);
    static CaptureFacts TakeCaptureFacts(BirGraphValue node)
    {
        switch (node)
        {
            case BirCallStatic value:
                var a = value._capturedGenericArgument; var r = value._capturedGenericResult.OrDefault(); var t = value._capturedGenericTarget.OrDefault();
                value._capturedGenericResult = default;
                if (a.HasValue) { value._capturedGenericArgument = default; value._capturedGenericTarget = default; }
                return new(a, r, t);
            case BirCallInstance value:
                var b = value._capturedGenericArgument; var s = value._capturedGenericResult.OrDefault(); var u = value._capturedGenericTarget.OrDefault();
                value._capturedGenericResult = default;
                if (b.HasValue) { value._capturedGenericArgument = default; value._capturedGenericTarget = default; }
                return new(b, s, u);
            case BirGenericStatic value:
                var c = value._capturedGenericArgument; var v = value._capturedGenericResult.OrDefault(); value._capturedGenericResult = default;
                if (c.HasValue) value._capturedGenericArgument = default; return new(c, v, null);
            case BirGenericInstance value:
                var d = value._capturedGenericArgument; var w = value._capturedGenericResult.OrDefault(); value._capturedGenericResult = default;
                if (d.HasValue) value._capturedGenericArgument = default; return new(d, w, null);
            case BirNativeStatic value:
                value._capturedGenericResult = default; if (value._capturedGenericArgument.HasValue) value._capturedGenericArgument = default; return null;
            case BirNativeInstance value:
                value._capturedGenericResult = default; if (value._capturedGenericArgument.HasValue) value._capturedGenericArgument = default; return null;
            case BirConstrainedCall value:
                value._capturedGenericResult = default; if (value._capturedGenericArgument.HasValue) value._capturedGenericArgument = default; return null;
            default: return null;
        }
    }
}
