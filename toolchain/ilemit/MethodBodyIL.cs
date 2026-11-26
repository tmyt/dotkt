using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

// A label or exception-region boundary can start a new basic block. In between, no instruction
// can be reached after an unconditional transfer. In particular, never write a conditional arm's
// store and branch to its join after a return: stack-recomputing writers can propagate that
// impossible predecessor's stack depth into the reachable block.
sealed class MethodBodyIL(ILGenerator target) : ILGenerator
{
    internal bool CanEmit { get; private set; } = true;
    HashSet<Label> _unreachedForward;

    bool Take(OpCode op)
    {
        if (!CanEmit) return false;
        CanEmit = op.FlowControl is not (FlowControl.Return or FlowControl.Throw or FlowControl.Branch);
        return true;
    }

    public override int ILOffset => target.ILOffset;
    public override LocalBuilder DeclareLocal(Type type, bool pinned) => target.DeclareLocal(type, pinned);
    public override Label DefineLabel() => target.DefineLabel();
    // An emitter-owned forward target has all its incoming edges by the time it is marked.
    // Loop headers and general CIR labels may receive later back edges: use DefineLabel for those.
    internal Label DefineForwardLabel()
    {
        var label = DefineLabel();
        (_unreachedForward ??= []).Add(label);
        return label;
    }
    public override void MarkLabel(Label label)
    {
        target.MarkLabel(label);
        if (_unreachedForward?.Remove(label) != true) CanEmit = true;
    }
    public override Label BeginExceptionBlock() { CanEmit = true; return target.BeginExceptionBlock(); }
    public override void BeginCatchBlock(Type type) { target.BeginCatchBlock(type); CanEmit = true; }
    public override void BeginExceptFilterBlock() { target.BeginExceptFilterBlock(); CanEmit = true; }
    public override void BeginFinallyBlock() { target.BeginFinallyBlock(); CanEmit = true; }
    public override void BeginFaultBlock() { target.BeginFaultBlock(); CanEmit = true; }
    public override void EndExceptionBlock() { target.EndExceptionBlock(); CanEmit = true; }

    public override void Emit(OpCode op) { if (Take(op)) target.Emit(op); }
    public override void Emit(OpCode op, byte value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, short value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, int value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, long value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, float value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, double value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, string value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, Type value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, FieldInfo value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, MethodInfo value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, ConstructorInfo value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, SignatureHelper value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, LocalBuilder value) { if (Take(op)) target.Emit(op, value); }
    public override void Emit(OpCode op, Label value)
    {
        if (!Take(op)) return;
        _unreachedForward?.Remove(value);
        target.Emit(op, value);
    }
    public override void Emit(OpCode op, Label[] values)
    {
        if (!Take(op)) return;
        if (_unreachedForward != null) foreach (var label in values) _unreachedForward.Remove(label);
        target.Emit(op, values);
    }
    public override void EmitCall(OpCode op, MethodInfo method, Type[] optional)
    { if (Take(op)) target.EmitCall(op, method, optional); }
    public override void EmitCalli(OpCode op, CallingConvention convention, Type result, Type[] args)
    { if (Take(op)) target.EmitCalli(op, convention, result, args); }
    public override void EmitCalli(OpCode op, CallingConventions convention, Type result, Type[] args, Type[] optional)
    { if (Take(op)) target.EmitCalli(op, convention, result, args, optional); }

    public override void BeginScope() => target.BeginScope();
    public override void EndScope() => target.EndScope();
    public override void UsingNamespace(string name) => target.UsingNamespace(name);
}
