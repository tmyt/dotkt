// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

sealed partial class Emitter
{
    // Synthesize a CLR-native async state machine (strategy B) for a `suspend fun`. Builds a struct
    // `<class>_<name>__sm : IAsyncStateMachine` (state + AsyncTaskMethodBuilder + cpsFields + awaiter caches),
    // emits MoveNext from the CPS-linearized `steps`, and fills the kickoff `mb` (Create/Start/return Task).
    // Proven shape — see docs/coroutine-il.md PoC. Capability bar = linear / loop / branch / direct-suspend-call.
    void EmitCoroutine(TypeInfo ti, MethodBuilder mb, JsonElement m)
    {
        var rs = m.GetProperty("resultType").GetString();
        bool unit = rs == "void";
        Type resultT = unit ? null : MapType(rs);
        Type builderT = unit ? typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder)
                             : typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder<>).MakeGenericType(resultT);
        var iasm = typeof(System.Runtime.CompilerServices.IAsyncStateMachine);
        var steps = m.GetProperty("steps").EnumerateArray().ToList();

        // ---- struct SM : IAsyncStateMachine ----
        // Nest the SM in its OWNER (non-generic owners only — a nested type of a generic owner would inherit its
        // type params): a nested type can reach the owner's PRIVATE/PROTECTED members, so a `suspend fun` whose body
        // touches a protected member (e.g. a protected member-extension) no longer throws MethodAccessException from
        // the SM. Generic owners keep the top-level SM (the generic-owner suspend path is unchanged).
        var smName = (mb.Name + "__sm" + (_smCounter++));
        var sm = ti.IsGeneric
            ? _mod.DefineType(ti.TB.Name + "_" + smName,
                TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, typeof(ValueType))
            : ti.TB.DefineNestedType(smName,
                TypeAttributes.NestedAssembly | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, typeof(ValueType));
        sm.AddInterfaceImplementation(iasm);
        var fState = sm.DefineField("<>1__state", typeof(int), FieldAttributes.Public);
        var fBuilder = sm.DefineField("<>t__builder", builderT, FieldAttributes.Public);

        var coFields = new Dictionary<string, FieldInfo>();
        var cpsDefs = m.GetProperty("cpsFields").EnumerateArray().ToList();
        foreach (var f in cpsDefs)
            coFields[f.GetProperty("name").GetString()] = sm.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);

        // Instance coroutine (e.g. a capturing suspend lambda's closure `invoke`): capture the receiver so resume
        // can reach the declaring type's fields (the lambda's captured vars). `this` in MoveNext reads this field.
        var fThis = mb.IsStatic ? null : sm.DefineField("<>4__this", ti.TB, FieldAttributes.Public);

        // One awaiter cache field + type per suspension point (keyed by state). Task<Tk> -> TaskAwaiter<Tk>.
        var awaiterType = new Dictionary<int, Type>();
        var awaiterField = new Dictionary<int, FieldBuilder>();
        foreach (var st in steps)
            if (st.GetProperty("k").GetString() == "coSuspend")
            {
                int k = st.GetProperty("state").GetInt32();
                var art = st.GetProperty("resultType").GetString();
                var at = art == "void" ? typeof(System.Runtime.CompilerServices.TaskAwaiter)
                    : typeof(System.Runtime.CompilerServices.TaskAwaiter<>).MakeGenericType(MapType(art));
                awaiterType[k] = at;
                awaiterField[k] = sm.DefineField("<>u__" + k, at, FieldAttributes.Public);
            }

        // SetStateMachine(IAsyncStateMachine) { <>t__builder.SetStateMachine(value); }
        var setSm = sm.DefineMethod("SetStateMachine",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(void), new[] { iasm });
        {
            var il = setSm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldflda, fBuilder); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, GenM(builderT, "SetStateMachine"));
            il.Emit(OpCodes.Ret);
        }
        sm.DefineMethodOverride(setSm, iasm.GetMethod("SetStateMachine"));

        // ---- MoveNext ----
        var moveNext = sm.DefineMethod("MoveNext",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(void), Type.EmptyTypes);
        sm.DefineMethodOverride(moveNext, iasm.GetMethod("MoveNext"));
        {
            _il = moveNext.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            _methodRetType = typeof(void);
            _coFields = coFields;
            _coThis = fThis;
            PrescanCfgLabels(m.GetProperty("steps"));   // a non-suspending while inside a suspend fun lowers to CFG

            // labels: one resume + one "after" per suspension; one per coLabel id; awaiter local per suspension.
            var resume = new Dictionary<int, Label>();
            var after = new Dictionary<int, Label>();
            var awaiterLocal = new Dictionary<int, LocalBuilder>();
            var coLabel = new Dictionary<int, Label>();
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                if (kind == "coSuspend")
                {
                    int k = st.GetProperty("state").GetInt32();
                    resume[k] = _il.DefineLabel(); after[k] = _il.DefineLabel();
                    awaiterLocal[k] = _il.DeclareLocal(awaiterType[k]);
                }
                else if (kind == "coLabel" || kind == "coGoto" || kind == "coCondGoto")
                {
                    int id = st.GetProperty("id").GetInt32();
                    if (!coLabel.ContainsKey(id)) coLabel[id] = _il.DefineLabel();
                }
            }

            // try-around-await: a suspension state inside a try can't be branched to from outside the protected
            // region. Map each in-try state to its try's landing label; the outer dispatch jumps THERE, and an
            // inner dispatch (emitted at coTryBegin, inside the try) re-branches to the actual resume point.
            var tryStart = new Dictionary<int, Label>();
            var tryStates = new Dictionary<int, List<int>>();
            var stateTry = new Dictionary<int, int>();
            {
                int open = -1;
                foreach (var st in steps)
                {
                    var kind = st.GetProperty("k").GetString();
                    if (kind == "coTryBegin") { int id = st.GetProperty("id").GetInt32(); open = id; tryStart[id] = _il.DefineLabel(); tryStates[id] = new List<int>(); }
                    else if (kind == "coTryEnd") open = -1;
                    else if (kind == "coSuspend" && open >= 0) { int k = st.GetProperty("state").GetInt32(); stateTry[k] = open; tryStates[open].Add(k); }
                }
            }
            _coExit = _il.DefineLabel(); _coTryDepth = 0;

            // dispatch: jump to the resume point for the saved state (state -1/-2 fall through to the start). An
            // in-try state jumps to its try's landing label instead (the inner dispatch then resumes inside it).
            foreach (var kv in resume)
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState);
                EmitLdcI4(kv.Key);
                _il.Emit(OpCodes.Beq, stateTry.TryGetValue(kv.Key, out var otid) ? tryStart[otid] : kv.Value);
            }

            var tryEnd = new Dictionary<int, Label>();
            bool fell = true;   // does the previous step fall through to here? (false after a return/unconditional goto)
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                switch (kind)
                {
                    case "coTryBegin":
                    {
                        int id = st.GetProperty("id").GetInt32();
                        _il.MarkLabel(tryStart[id]);
                        _il.Emit(OpCodes.Nop);                       // landing pad OUTSIDE the region (legal branch target)
                        tryEnd[id] = _il.BeginExceptionBlock();
                        _coTryDepth++;
                        // inner dispatch: resume to the suspension point that lives inside THIS try.
                        foreach (var k in tryStates[id])
                        {
                            _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState);
                            EmitLdcI4(k); _il.Emit(OpCodes.Beq, resume[k]);
                        }
                        break;
                    }
                    case "coCatchBegin":
                    {
                        int id = st.GetProperty("id").GetInt32();
                        if (fell) _il.Emit(OpCodes.Leave, tryEnd[id]);   // close the try body / previous catch
                        var ct = MapType(st.GetProperty("excType").GetString());
                        _il.BeginCatchBlock(ct);
                        var el = _il.DeclareLocal(ct);                   // bind the caught exception to the catch var
                        _locals[st.GetProperty("var").GetString()] = el;
                        _il.Emit(OpCodes.Stloc, el);
                        break;
                    }
                    case "coTryEnd":
                        EmitCoTryEnd(st, tryEnd[st.GetProperty("id").GetInt32()], fell);
                        break;
                    case "coSuspend":
                        EmitCoSuspend(st, fState, fBuilder, builderT, sm, awaiterType, awaiterField, awaiterLocal, resume, after, coFields);
                        break;
                    case "coLabel": _il.MarkLabel(coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coGoto": _il.Emit(OpCodes.Br, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coCondGoto":
                        EmitExpr(st.GetProperty("cond"));
                        _il.Emit(OpCodes.Brfalse, coLabel[st.GetProperty("id").GetInt32()]);   // goto when cond is false
                        break;
                    case "coReturn":
                        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(-2); _il.Emit(OpCodes.Stfld, fState);
                        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, fBuilder);
                        if (!unit && st.TryGetProperty("value", out var rv) && rv.ValueKind != JsonValueKind.Null)
                        {
                            var gt = EmitExpr(rv);
                            if (gt != null && NeedsBoxToRef(gt) && !resultT.IsValueType && !resultT.IsGenericParameter) _il.Emit(OpCodes.Box, gt);
                            _il.Emit(OpCodes.Call, GenM(builderT, "SetResult"));
                        }
                        else _il.Emit(OpCodes.Call, GenM(builderT, "SetResult"));
                        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Ret);
                        break;
                    case "coUnsupported":
                        throw new NotSupportedException("coroutine feature not supported by the .NET backend: " + st.GetProperty("of").GetString());
                    default:
                        EmitStmt(st);
                        break;
                }
                fell = !(kind == "coReturn" || kind == "coGoto");
            }
            _il.MarkLabel(_coExit);
            _il.Emit(OpCodes.Ret);   // single exit; suspension/return inside a try `leave` here, others `ret` directly
            _coFields = null;
            _coThis = null;
        }

        // ---- kickoff body (the original method `mb`): start the machine, return its Task ----
        {
            _il = mb.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            var locSm = _il.DeclareLocal(sm);
            int ai = mb.IsStatic ? 0 : 1;
            if (fThis != null) { _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Stfld, fThis); }
            foreach (var p in m.GetProperty("params").EnumerateArray())
            {
                var pn = p.GetProperty("name").GetString();
                _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldarg, ai++); _il.Emit(OpCodes.Stfld, coFields[pn]);
            }
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Call, GenM(builderT, "Create")); _il.Emit(OpCodes.Stfld, fBuilder);
            _il.Emit(OpCodes.Ldloca, locSm); EmitLdcI4(-1); _il.Emit(OpCodes.Stfld, fState);
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldflda, fBuilder); _il.Emit(OpCodes.Ldloca, locSm);
            _il.Emit(OpCodes.Call, GenM(builderT, "Start").MakeGenericMethod(sm));
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldflda, fBuilder);
            _il.Emit(OpCodes.Call, GenM(builderT, "get_Task"));
            _il.Emit(OpCodes.Ret);
        }

        sm.CreateType();
    }

    // Continuation-core state machine (Path B / B2-as-generalization, docs §13b): a CLASS implementing
    // DotKt.Coroutines.Continuation<object>, driven by ResumeWith -> InvokeSuspend (label switch). The default
    // Task sink (future{}, via NewRoot<T>) is the kickoff. Selected by "coClass":true (opt-in `@KCont` while the
    // struct/Task IAsyncStateMachine path remains the default). Reuses the same coSuspend/coLabel/coGoto/coReturn
    // step stream as the struct form; only the lowered runtime form differs.
    // A field/ctor on the (possibly generic) state-machine type: on a constructed generic SM, accesses go through
    // TypeBuilder.GetField/GetConstructor(constructed, def); on a non-generic SM, the def itself.
    static FieldInfo SmField(Type inst, FieldBuilder def) => inst.IsGenericType ? TypeBuilder.GetField(inst, def) : def;
    static ConstructorInfo SmCtor(Type inst, ConstructorBuilder def) => inst.IsGenericType ? TypeBuilder.GetConstructor(inst, def) : def;

    // Resolve a (unique-by-name) method on a possibly TypeBuilder-instantiated generic type. When the result type of
    // a `suspend fun` is a USER type, AsyncTaskMethodBuilder<UserT>/Task<UserT>/TaskAwaiter<UserT> are
    // TypeBuilderInstantiations, whose GetMethod throws "use TypeBuilder.GetMethod instead" — so re-anchor the open
    // definition's method onto the instantiation. Baked instantiations / non-generic types resolve directly. This is
    // the method-side counterpart of SmCtor; it unblocks member `suspend fun`s returning a user class.
    static MethodInfo GenM(Type t, string name)
    {
        try { return t.GetMethod(name); }
        catch (NotSupportedException) { return TypeBuilder.GetMethod(t, t.GetGenericTypeDefinition().GetMethod(name)); }
    }

    // Emit parameter NAMES into the metadata (DefineParameter is 1-based; 0 = return). ilemit otherwise defines
    // methods by type only, so the names are lost — and facadegen falls back to arg0/arg1, which blocks named-argument
    // calls across an assembly boundary. The names come straight from the BIR params.
    void DefineParamNames(MethodBuilder mb, JsonElement m) => DefineParamNames(mb.DefineParameter, m);
    void DefineParamNames(ConstructorBuilder cb, JsonElement m) => DefineParamNames(cb.DefineParameter, m);
    void DefineParamNames(Func<int, ParameterAttributes, string, ParameterBuilder> defineParam, JsonElement m)
    {
        if (!m.TryGetProperty("params", out var ps)) return;
        int i = 1;
        foreach (var p in ps.EnumerateArray())
        {
            var name = (p.TryGetProperty("name", out var nn) ? nn.GetString() : null) ?? "";
            bool vararg = p.TryGetProperty("vararg", out var vv) && vv.GetBoolean();
            bool hasDefault = p.TryGetProperty("default", out var dflt);
            // A nullable reference parameter needs a [Nullable(2)] override against the type's non-null default, so the
            // parameter builder must exist even if it otherwise carries no name/vararg/default. (A value-type `X?` is the
            // structural Nullable<X> instead; the [Nullable] on it is simply ignored by readers — harmless.)
            bool nullable = p.TryGetProperty("nullable", out var pn) && pn.GetBoolean();
            // PARAMETER-level custom attributes (e.g. [ClrRefArgument], which bir2cir reads from the ref.dll to pass the
            // arg by reference). Stripped in the runtime build (kotc emits none), so this rides only the ref.dll.
            JsonElement pattrs = default;
            bool hasAttrs = !_stripMetadata && p.TryGetProperty("attrs", out pattrs) && pattrs.GetArrayLength() > 0;
            if (name.Length == 0 && !vararg && !hasDefault && !nullable && !hasAttrs) { i++; continue; }
            // A constant default -> [Optional] + DefaultParameterValue, so a cross-module caller can omit the arg.
            var attrs = hasDefault ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : ParameterAttributes.None;
            var pb = defineParam(i, attrs, name.Length > 0 ? name : null);
            // `vararg xs: T` -> [ParamArray] so the .NET signature is a params array (a C# OR Kotlin consumer can spread).
            if (vararg) pb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
            if (hasDefault) { try { pb.SetConstant(ConstArgValue(dflt)); } catch { } }
            if (nullable) ApplyNullable(pb);
            // Apply each param attribute whose type this assembly can encode (in-assembly emitted type or a clr:-imported
            // one); an attr referencing a type not in `_types` is skipped (BuildCab would KeyNotFound) — the same "the CLR
            // layer decides what is encodable" policy the method-level attr path uses.
            if (hasAttrs)
                foreach (var a in pattrs.EnumerateArray())
                {
                    var an = a.GetProperty("attr").GetString();
                    if (!an.StartsWith("clr:", StringComparison.Ordinal) && !_types.ContainsKey(an)) continue;
                    var cab = BuildCab(a); if (cab != null) pb.SetCustomAttribute(cab);
                }
            i++;
        }
    }

    // Close a coroutine try region (shared by the struct & class SM forms). A `finally` around a suspension is NOT
    // emitted as a CLR finally clause (a suspend `leave`s the .try, which would run a real finally on every
    // suspend); instead the finally body runs explicitly on the normal-exit path and in a synthesized catch-all
    // that rethrows (T10). v1: fall-through try body only — a `return` inside the try skips the finally.
    void EmitCoTryEnd(JsonElement st, Label tryEndL, bool fell)
    {
        if (st.TryGetProperty("finally", out var fin) && fin.GetArrayLength() > 0)
        {
            if (fell) { foreach (var f in fin.EnumerateArray()) EmitStmt(f); _il.Emit(OpCodes.Leave, tryEndL); }
            _il.BeginCatchBlock(ResolveType("System.Exception"));
            _il.Emit(OpCodes.Pop);                                  // discard the caught exception (we rethrow)
            foreach (var f in fin.EnumerateArray()) EmitStmt(f);
            _il.Emit(OpCodes.Rethrow);
            _il.EndExceptionBlock();
        }
        else
        {
            if (fell) _il.Emit(OpCodes.Leave, tryEndL);
            _il.EndExceptionBlock();
        }
        _coTryDepth--;
    }

    // The single ctor of a constructed generic reflected type, re-anchored via TypeBuilder.GetConstructor when a
    // type arg is an emitted generic param / TypeBuilder (e.g. TypedCont<T> in a generic suspend fun whose result
    // is the method's own type param T — reflection can't resolve members on such an instantiation).
    static ConstructorInfo CtorOf(Type constructed) =>
        constructed.GetGenericArguments().Any(a => a is TypeBuilder || a.IsGenericParameter)
            ? TypeBuilder.GetConstructor(constructed, constructed.GetGenericTypeDefinition().GetConstructors()[0])
            : constructed.GetConstructors()[0];

    void EmitCoroutineClass(TypeInfo ti, MethodBuilder mb, JsonElement m)
    {
        var rs = m.GetProperty("resultType").GetString();
        bool unitResult = rs == "void";   // a `suspend fun … : Unit` surfaces as a non-generic Task (RootUnit sink)
        var steps = m.GetProperty("steps").EnumerateArray().ToList();

        var contObj = ResolveType(CoContinuation).MakeGenericType(typeof(object));
        var resObj = ResolveType("kotlin.Result`1").MakeGenericType(typeof(object));
        var ctxType = ResolveType(CoContext);
        var builders = ResolveType(CoBuilders);
        var fSuspended = ResolveType(CoIntrinsics).GetField("COROUTINE_SUSPENDED");
        var mResSuccess = resObj.GetMethod("Success");
        var mResFailure = resObj.GetMethod("Failure");
        var mResIsFailure = resObj.GetMethod("get_IsFailure");
        var mResExOrNull = resObj.GetMethod("get_ExceptionOrNull");
        var mResGetOrThrow = resObj.GetMethod("GetOrThrow");
        var mContResume = contObj.GetMethod("ResumeWith");
        var mContGetCtx = contObj.GetMethod("get_Context");

        var sm = _mod.DefineType(ti.TB.Name + "_" + mb.Name + "__sm",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, typeof(object));
        // Generic suspend fun/member -> a generic state-machine type mirroring the enclosing type params plus the
        // method's type params (so `gp:T`-typed cps fields resolve to the SM's own params; the kickoff instantiates
        // sm<classT, methodT>). See docs §13f.
        var sTps = m.TryGetProperty("typeParams", out var stpC) && stpC.GetArrayLength() > 0 ? (JsonElement?)stpC : null;
        Dictionary<string, GenericTypeParameterBuilder> smMap = null;
        string[] smNames = null;
        GenericTypeParameterBuilder[] smGps = null;
        var classTpNames = ti.IsGeneric ? ti.TypeParams.Keys.ToArray() : Array.Empty<string>();
        var methodTpNames = sTps != null ? TpNames(sTps.Value) : Array.Empty<string>();
        smNames = classTpNames.Concat(methodTpNames).ToArray();
        if (smNames.Length > 0)
        {
            smGps = sm.DefineGenericParameters(smNames);
            smMap = new Dictionary<string, GenericTypeParameterBuilder>();
            for (int gi = 0; gi < smNames.Length; gi++) smMap[smNames[gi]] = smGps[gi];
        }
        sm.AddInterfaceImplementation(contObj);
        // Inside a GENERIC SM's own methods, references to its own fields/methods must go through the
        // self-instantiation sm<itsOwnParams> (Reflection.Emit rule), else "type is not fully instantiated".
        Type selfInst = smGps == null ? (Type)sm : sm.MakeGenericType(smGps.Cast<Type>().ToArray());
        FieldInfo SelfF(FieldBuilder f) => smGps == null ? f : TypeBuilder.GetField(selfInst, f);
        var savedTP = _curTypeParams; var savedMP = _curMethodParams;
        _curTypeParams = smMap; _curMethodParams = null;   // `gp:T` inside the SM resolves to the SM's own params
        // Field DEFINITIONS (open generic). The kickoff resolves these against sm<methodT>; the SM's own method
        // bodies use the self-instantiated (SelfF) forms below.
        var fStateD = sm.DefineField("<>1__state", typeof(int), FieldAttributes.Public);
        var fCompletionD = sm.DefineField("<>completion", contObj, FieldAttributes.Public);
        var fParamD = sm.DefineField("<>param", typeof(object), FieldAttributes.Public);
        var fErrD = sm.DefineField("<>err", typeof(Exception), FieldAttributes.Public);
        var coDefs = new Dictionary<string, FieldBuilder>();
        foreach (var f in m.GetProperty("cpsFields").EnumerateArray())
            coDefs[f.GetProperty("name").GetString()] = sm.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);
        var fThisD = mb.IsStatic ? null : sm.DefineField("<>4__this", ti.TB, FieldAttributes.Public);
        // Self-instantiated views used inside the SM's own methods (= the defs when non-generic).
        FieldInfo fState = SelfF(fStateD), fCompletion = SelfF(fCompletionD), fParam = SelfF(fParamD), fErr = SelfF(fErrD);
        FieldInfo fThis = fThisD == null ? null : SelfF(fThisD);
        var coFields = new Dictionary<string, FieldInfo>();
        foreach (var kv in coDefs) coFields[kv.Key] = SelfF(kv.Value);

        var ctor = sm.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        { var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Ret); }

        // CoroutineContext get_Context => <>completion.Context
        var getCtx = sm.DefineMethod("get_Context", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.SpecialName, ctxType, Type.EmptyTypes);
        { var il = getCtx.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCompletion); il.Emit(OpCodes.Callvirt, mContGetCtx); il.Emit(OpCodes.Ret); }
        sm.DefineMethodOverride(getCtx, mContGetCtx);

        // object InvokeSuspend(): the label-switch body. Returns the result value, or COROUTINE_SUSPENDED.
        var invoke = sm.DefineMethod("InvokeSuspend", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(object), Type.EmptyTypes);
        {
            _il = invoke.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            _methodRetType = typeof(object);
            _coFields = coFields; _coThis = fThis;
            PrescanCfgLabels(m.GetProperty("steps"));
            var outcome = _il.DeclareLocal(typeof(object));

            var resume = new Dictionary<int, Label>();
            var coLabel = new Dictionary<int, Label>();
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                if (kind == "coSuspend" || kind == "coSuspendIntrinsic") resume[st.GetProperty("state").GetInt32()] = _il.DefineLabel();
                else if (kind == "coLabel" || kind == "coGoto" || kind == "coCondGoto") { int id = st.GetProperty("id").GetInt32(); if (!coLabel.ContainsKey(id)) coLabel[id] = _il.DefineLabel(); }
            }
            var tryStart = new Dictionary<int, Label>();
            var tryStates = new Dictionary<int, List<int>>();
            var stateTry = new Dictionary<int, int>();
            { int open = -1; foreach (var st in steps) { var kind = st.GetProperty("k").GetString();
                if (kind == "coTryBegin") { int id = st.GetProperty("id").GetInt32(); open = id; tryStart[id] = _il.DefineLabel(); tryStates[id] = new List<int>(); }
                else if (kind == "coTryEnd") open = -1;
                else if ((kind == "coSuspend" || kind == "coSuspendIntrinsic") && open >= 0) { int k = st.GetProperty("state").GetInt32(); stateTry[k] = open; tryStates[open].Add(k); } } }
            _coExit = _il.DefineLabel(); _coTryDepth = 0;

            foreach (var kv in resume)
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); EmitLdcI4(kv.Key);
                _il.Emit(OpCodes.Beq, stateTry.TryGetValue(kv.Key, out var otid) ? tryStart[otid] : kv.Value);
            }

            var tryEnd = new Dictionary<int, Label>();
            bool fell = true;
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                switch (kind)
                {
                    case "coTryBegin": { int id = st.GetProperty("id").GetInt32(); _il.MarkLabel(tryStart[id]); _il.Emit(OpCodes.Nop);
                        tryEnd[id] = _il.BeginExceptionBlock(); _coTryDepth++;
                        foreach (var k in tryStates[id]) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); EmitLdcI4(k); _il.Emit(OpCodes.Beq, resume[k]); }
                        break; }
                    case "coCatchBegin": { int id = st.GetProperty("id").GetInt32(); if (fell) _il.Emit(OpCodes.Leave, tryEnd[id]);
                        var ct = MapType(st.GetProperty("excType").GetString()); _il.BeginCatchBlock(ct);
                        var el = _il.DeclareLocal(ct); _locals[st.GetProperty("var").GetString()] = el; _il.Emit(OpCodes.Stloc, el); break; }
                    case "coTryEnd": EmitCoTryEnd(st, tryEnd[st.GetProperty("id").GetInt32()], fell); break;
                    case "coSuspend": EmitCoSuspendClass(st, fState, fParam, fErr, resume, coFields, builders, fSuspended, outcome); break;
                    case "coSuspendIntrinsic": EmitCoSuspendIntrinsicClass(st, fState, fParam, fErr, resume, coFields, fSuspended, outcome); break;
                    case "coLabel": _il.MarkLabel(coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coGoto": _il.Emit(OpCodes.Br, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coCondGoto": EmitExpr(st.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coReturn":
                        if (st.TryGetProperty("value", out var rv) && rv.ValueKind != JsonValueKind.Null) { var gt = EmitExpr(rv); if (gt != null && (gt.IsValueType || gt.IsGenericParameter)) _il.Emit(OpCodes.Box, gt); }   // box value types AND generic params (T)
                        else _il.Emit(OpCodes.Ldnull);
                        _il.Emit(OpCodes.Stloc, outcome);
                        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Br, _coExit);
                        break;
                    case "coUnsupported": throw new NotSupportedException("coroutine feature not supported by the .NET backend: " + st.GetProperty("of").GetString());
                    default: EmitStmt(st); break;
                }
                fell = !(kind == "coReturn" || kind == "coGoto");
            }
            _il.MarkLabel(_coExit);
            _il.Emit(OpCodes.Ldloc, outcome);
            _il.Emit(OpCodes.Ret);
            _coFields = null; _coThis = null;
        }

        // void ResumeWith(Result<object>): unpack the result, drive InvokeSuspend, route the outcome to <>completion.
        var resumeWith = sm.DefineMethod("ResumeWith", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig, typeof(void), new[] { resObj });
        {
            var il = resumeWith.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarga_S, (byte)1); il.Emit(OpCodes.Call, mResExOrNull); il.Emit(OpCodes.Stfld, fErr);
            var setNull = il.DefineLabel(); var afterParam = il.DefineLabel();
            il.Emit(OpCodes.Ldarga_S, (byte)1); il.Emit(OpCodes.Call, mResIsFailure); il.Emit(OpCodes.Brtrue, setNull);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarga_S, (byte)1); il.Emit(OpCodes.Call, mResGetOrThrow); il.Emit(OpCodes.Stfld, fParam); il.Emit(OpCodes.Br, afterParam);
            il.MarkLabel(setNull); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, fParam);
            il.MarkLabel(afterParam);
            var lOut = il.DeclareLocal(typeof(object)); var lFaulted = il.DeclareLocal(typeof(bool)); var lEx = il.DeclareLocal(typeof(Exception));
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, smGps == null ? (MethodInfo)invoke : TypeBuilder.GetMethod(selfInst, invoke)); il.Emit(OpCodes.Stloc, lOut);
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Stloc, lEx);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCompletion); il.Emit(OpCodes.Ldloc, lEx); il.Emit(OpCodes.Call, mResFailure); il.Emit(OpCodes.Callvirt, mContResume);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, lFaulted);
            il.EndExceptionBlock();
            var ret = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, lFaulted); il.Emit(OpCodes.Brtrue, ret);
            il.Emit(OpCodes.Ldloc, lOut); il.Emit(OpCodes.Ldsfld, fSuspended); il.Emit(OpCodes.Beq, ret);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCompletion); il.Emit(OpCodes.Ldloc, lOut); il.Emit(OpCodes.Call, mResSuccess); il.Emit(OpCodes.Callvirt, mContResume);
            il.MarkLabel(ret); il.Emit(OpCodes.Ret);
        }
        sm.DefineMethodOverride(resumeWith, mContResume);
        _curTypeParams = savedTP; _curMethodParams = savedMP;

        // kickoff: build the SM (sm<methodT> when generic), copy params/this, bind a NewRoot<T> sink, drive
        // ResumeWith(success(null)), return root.Task. Runs in the METHOD's generic context (mb's own type params).
        {
            _curTypeParams = ti.TypeParams; _curMethodParams = sTps != null ? _methodTypeParams[mb] : null;
            Type smInst = smMap == null ? sm : sm.MakeGenericType(smNames.Select(n =>
                _methodTypeParams.TryGetValue(mb, out var mm) && mm.TryGetValue(n, out var mgp) ? (Type)mgp : ti.TypeParams[n]).ToArray());
            _il = mb.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            var locSm = _il.DeclareLocal(smInst);
            _il.Emit(OpCodes.Newobj, SmCtor(smInst, ctor)); _il.Emit(OpCodes.Stloc, locSm);
            int ai = mb.IsStatic ? 0 : 1;
            if (fThisD != null) { _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Stfld, SmField(smInst, fThisD)); }
            foreach (var p in m.GetProperty("params").EnumerateArray())
            {
                var pn = p.GetProperty("name").GetString();
                _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldarg, ai++); _il.Emit(OpCodes.Stfld, SmField(smInst, coDefs[pn]));
            }
            var newRoot = unitResult ? builders.GetMethod("NewRootUnit") : builders.GetMethod("NewRoot").MakeGenericMethod(MapType(rs));
            var emptyCtx = ResolveType(CoEmptyContext).GetField("Instance");
            var locRoot = _il.DeclareLocal(newRoot.ReturnType);
            _il.Emit(OpCodes.Ldsfld, emptyCtx); _il.Emit(OpCodes.Call, newRoot); _il.Emit(OpCodes.Stloc, locRoot);
            _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldloc, locRoot); _il.Emit(OpCodes.Stfld, SmField(smInst, fCompletionD));
            _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldnull); _il.Emit(OpCodes.Call, mResSuccess); _il.Emit(OpCodes.Callvirt, mContResume);
            _il.Emit(OpCodes.Ldloc, locRoot); _il.Emit(OpCodes.Callvirt, newRoot.ReturnType.GetMethod("get_Task")); _il.Emit(OpCodes.Ret);
            _curTypeParams = savedTP; _curMethodParams = savedMP;
        }

        sm.CreateType();
    }

    // A suspension point in the class form: register the awaited Task to resume this continuation (AwaitOnto), set
    // the resume state, and return COROUTINE_SUSPENDED; on resume, rethrow a faulted result or unbox <>param.
    void EmitCoSuspendClass(JsonElement st, FieldInfo fState, FieldInfo fParam, FieldInfo fErr,
        Dictionary<int, Label> resume, Dictionary<string, FieldInfo> coFields, Type builders, FieldInfo fSuspended, LocalBuilder outcome)
    {
        int k = st.GetProperty("state").GetInt32();
        var taskType = EmitExpr(st.GetProperty("awaitable"));
        var lTask = _il.DeclareLocal(taskType); _il.Emit(OpCodes.Stloc, lTask);
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
        bool genericTask = taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>);
        MethodInfo awaitOnto = genericTask
            ? builders.GetMethods().First(mm => mm.Name == "AwaitOnto" && mm.IsGenericMethodDefinition).MakeGenericMethod(taskType.GetGenericArguments()[0])
            : builders.GetMethods().First(mm => mm.Name == "AwaitOnto" && !mm.IsGenericMethodDefinition);
        _il.Emit(OpCodes.Ldloc, lTask); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Call, awaitOnto);
        _il.Emit(OpCodes.Ldsfld, fSuspended); _il.Emit(OpCodes.Stloc, outcome);
        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Br, _coExit);

        _il.MarkLabel(resume[k]);
        var noErr = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Brfalse, noErr);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Throw);
        _il.MarkLabel(noErr);
        var assignTo = st.GetProperty("assignTo").ValueKind == JsonValueKind.Null ? null : st.GetProperty("assignTo").GetString();
        var resType = st.GetProperty("resultType").GetString();
        if (assignTo != null && resType != "void")
        {
            var tk = MapType(resType);
            if (coFields.TryGetValue(assignTo, out var destF))
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stfld, destF);
            }
            else
            {
                var tmp = _il.DeclareLocal(tk); _locals[assignTo] = tmp;
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stloc, tmp);
            }
        }
    }

    // `sequence { yield(…) }` -> a state machine implementing DotKt.Sequences.ISeqStep<elem> (MoveNext advances to
    // the next yield; Current holds it), wrapped by Seq.Of into a lazy IEnumerable<elem>. The yield SM reuses the
    // coYield/coLabel/coGoto/coCondGoto step stream. Emitted inline at the call site (state is saved/restored so the
    // enclosing method's IL emission resumes afterward). See docs §13h.
    Type EmitSequenceSm(JsonElement e)
    {
        var elem = MapType(e.GetProperty("elem").GetString());
        var steps = e.GetProperty("steps").EnumerateArray().ToList();
        var iseq = ResolveType(CoSeqStep).MakeGenericType(elem);

        var sm = _mod.DefineType("<>dotkt_SeqSm" + (_seqCounter++),
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, typeof(object));
        sm.AddInterfaceImplementation(iseq);
        var fState = sm.DefineField("<>state", typeof(int), FieldAttributes.Public);
        var fCurrent = sm.DefineField("<>current", elem, FieldAttributes.Public);
        var coFields = new Dictionary<string, FieldInfo>();
        foreach (var f in e.GetProperty("cpsFields").EnumerateArray())
            coFields[f.GetProperty("name").GetString()] = sm.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);

        var ctor = sm.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        { var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Ret); }

        var getCur = sm.DefineMethod("get_Current", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.SpecialName, elem, Type.EmptyTypes);
        { var il = getCur.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCurrent); il.Emit(OpCodes.Ret); }
        sm.DefineMethodOverride(getCur, iseq.GetMethod("get_Current"));

        var mv = sm.DefineMethod("MoveNext", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig, typeof(bool), Type.EmptyTypes);
        sm.DefineMethodOverride(mv, iseq.GetMethod("MoveNext"));

        // Save the enclosing method's emit state (the shared _il / locals / coField context is reused for the SM body).
        var sIl = _il; var sFields = _coFields; var sThis = _coThis; var sRet = _methodRetType;
        var sCfg = _cfgLabels; var sExit = _coExit; var sTryDepth = _coTryDepth;
        var sLocals = new Dictionary<string, LocalBuilder>(_locals);
        var sArgs = new Dictionary<string, int>(_args); var sArgTypes = new Dictionary<string, Type>(_argTypes);
        {
            _il = mv.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear(); _methodRetType = typeof(bool);
            _coFields = coFields; _coThis = null;
            PrescanCfgLabels(e.GetProperty("steps"));
            var resume = new Dictionary<int, Label>();
            var coLabel = new Dictionary<int, Label>();
            var enumFields = new Dictionary<int, FieldInfo>();   // coYieldAll: per-step IEnumerator<elem> field
            var ienumerable = ResolveType("System.Collections.Generic.IEnumerable`1").MakeGenericType(elem);
            var ienumerator = ResolveType("System.Collections.Generic.IEnumerator`1").MakeGenericType(elem);
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                if (kind == "coYield") resume[st.GetProperty("state").GetInt32()] = _il.DefineLabel();
                else if (kind == "coYieldAll") { int k2 = st.GetProperty("state").GetInt32(); resume[k2] = _il.DefineLabel(); enumFields[k2] = sm.DefineField("<>e" + k2, ienumerator, FieldAttributes.Public); }
                else if (kind == "coLabel" || kind == "coGoto" || kind == "coCondGoto") { int id = st.GetProperty("id").GetInt32(); if (!coLabel.ContainsKey(id)) coLabel[id] = _il.DefineLabel(); }
            }
            var endL = _il.DefineLabel();
            // if (<>state == -1) return false;   (exhausted)
            var notDone = _il.DefineLabel();
            _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); _il.Emit(OpCodes.Ldc_I4_M1); _il.Emit(OpCodes.Bne_Un, notDone);
            _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ret);
            _il.MarkLabel(notDone);
            // dispatch to the resume point after the saved yield (state 0 = start, falls through)
            foreach (var kv in resume) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); EmitLdcI4(kv.Key); _il.Emit(OpCodes.Beq, kv.Value); }
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                switch (kind)
                {
                    case "coYield":
                    {
                        int k = st.GetProperty("state").GetInt32();
                        _il.Emit(OpCodes.Ldarg_0); var vt = EmitExpr(st.GetProperty("value"));
                        if (vt != null && (vt.IsValueType || vt.IsGenericParameter) && !elem.IsValueType && !elem.IsGenericParameter) _il.Emit(OpCodes.Box, vt);
                        _il.Emit(OpCodes.Stfld, fCurrent);
                        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
                        _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Ret);
                        _il.MarkLabel(resume[k]);
                        break;
                    }
                    case "coLabel": _il.MarkLabel(coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coGoto": _il.Emit(OpCodes.Br, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coCondGoto": EmitExpr(st.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coYieldAll":
                    {
                        // Yield every element of an IEnumerable<elem>. Get its enumerator into a field ONCE (the
                        // resume dispatch jumps PAST this init), then on each MoveNext call advance the inner
                        // enumerator: fe.MoveNext() ? (current = fe.Current; state = k; return true) : fall through.
                        int k = st.GetProperty("state").GetInt32();
                        var fe = enumFields[k];
                        _il.Emit(OpCodes.Ldarg_0);
                        EmitExpr(st.GetProperty("iterable"));
                        _il.Emit(OpCodes.Callvirt, ienumerable.GetMethod("GetEnumerator"));
                        _il.Emit(OpCodes.Stfld, fe);
                        _il.MarkLabel(resume[k]);
                        var afterAll = _il.DefineLabel();
                        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fe);
                        _il.Emit(OpCodes.Callvirt, ResolveType("System.Collections.IEnumerator").GetMethod("MoveNext"));
                        _il.Emit(OpCodes.Brfalse, afterAll);
                        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fe);
                        _il.Emit(OpCodes.Callvirt, ienumerator.GetMethod("get_Current"));
                        _il.Emit(OpCodes.Stfld, fCurrent);
                        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
                        _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Ret);
                        _il.MarkLabel(afterAll);
                        break;
                    }
                    case "coReturn": _il.Emit(OpCodes.Br, endL); break;   // `return` from the block ends the sequence
                    case "coUnsupported": throw new NotSupportedException("sequence feature not supported: " + st.GetProperty("of").GetString());
                    default: EmitStmt(st); break;
                }
            }
            _il.MarkLabel(endL);
            _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldc_I4_M1); _il.Emit(OpCodes.Stfld, fState);   // mark exhausted
            _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ret);
        }
        _il = sIl; _coFields = sFields; _coThis = sThis; _methodRetType = sRet;
        _cfgLabels = sCfg; _coExit = sExit; _coTryDepth = sTryDepth;
        _locals.Clear(); foreach (var kv in sLocals) _locals[kv.Key] = kv.Value;
        _args.Clear(); foreach (var kv in sArgs) _args[kv.Key] = kv.Value;
        _argTypes.Clear(); foreach (var kv in sArgTypes) _argTypes[kv.Key] = kv.Value;
        sm.CreateType();

        // call site: Seq.Of<elem>(new SeqSm())
        _il.Emit(OpCodes.Newobj, ctor);
        _il.Emit(OpCodes.Call, ResolveType(CoSeq).GetMethod("Of").MakeGenericMethod(elem));
        return ResolveType("System.Collections.Generic.IEnumerable`1").MakeGenericType(elem);
    }

    // The raw `suspendCoroutineUninterceptedOrReturn` leaf in the class form: set the resume state, run the block's
    // leading statements (which typically register `this` to be resumed), then evaluate its result — if it is
    // COROUTINE_SUSPENDED, suspend; otherwise resume synchronously with that value. On resume, rethrow a faulted
    // result or unbox <>param. State is set BEFORE the block runs, so a same-thread resume during registration is safe.
    void EmitCoSuspendIntrinsicClass(JsonElement st, FieldInfo fState, FieldInfo fParam, FieldInfo fErr,
        Dictionary<int, Label> resume, Dictionary<string, FieldInfo> coFields, FieldInfo fSuspended, LocalBuilder outcome)
    {
        int k = st.GetProperty("state").GetInt32();
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
        foreach (var pre in st.GetProperty("pre").EnumerateArray()) EmitStmt(pre);
        var gt = EmitExpr(st.GetProperty("value")); if (gt != null && (gt.IsValueType || gt.IsGenericParameter)) _il.Emit(OpCodes.Box, gt);   // box value types AND generic params (T)
        var vTmp = _il.DeclareLocal(typeof(object)); _il.Emit(OpCodes.Stloc, vTmp);
        var notSusp = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, vTmp); _il.Emit(OpCodes.Ldsfld, fSuspended); _il.Emit(OpCodes.Bne_Un, notSusp);
        _il.Emit(OpCodes.Ldsfld, fSuspended); _il.Emit(OpCodes.Stloc, outcome);
        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Br, _coExit);
        _il.MarkLabel(notSusp);                                  // synchronous return: stash the value as the resume param
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldloc, vTmp); _il.Emit(OpCodes.Stfld, fParam);

        _il.MarkLabel(resume[k]);
        var noErr = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Brfalse, noErr);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Throw);
        _il.MarkLabel(noErr);
        var assignTo = st.GetProperty("assignTo").ValueKind == JsonValueKind.Null ? null : st.GetProperty("assignTo").GetString();
        var resType = st.GetProperty("resultType").GetString();
        if (assignTo != null && resType != "void")
        {
            var tk = MapType(resType);
            if (coFields.TryGetValue(assignTo, out var destF))
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stfld, destF);
            }
            else
            {
                var tmp = _il.DeclareLocal(tk); _locals[assignTo] = tmp;
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stloc, tmp);
            }
        }
    }

    void EmitCoSuspend(JsonElement st, FieldBuilder fState, FieldBuilder fBuilder, Type builderT, TypeBuilder sm,
        Dictionary<int, Type> awaiterType, Dictionary<int, FieldBuilder> awaiterField, Dictionary<int, LocalBuilder> awaiterLocal,
        Dictionary<int, Label> resume, Dictionary<int, Label> after, Dictionary<string, FieldInfo> coFields)
    {
        int k = st.GetProperty("state").GetInt32();
        var at = awaiterType[k];
        var aLoc = awaiterLocal[k];

        // awaiter = (awaitable).GetAwaiter();
        var taskType = EmitExpr(st.GetProperty("awaitable"));
        _il.Emit(OpCodes.Callvirt, GenM(taskType, "GetAwaiter"));
        _il.Emit(OpCodes.Stloc, aLoc);
        // if (awaiter.IsCompleted) goto after;
        _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, GenM(at, "get_IsCompleted"));
        _il.Emit(OpCodes.Brtrue, after[k]);
        // suspend: state=k; <>u__k=awaiter; builder.AwaitUnsafeOnCompleted(ref awaiter, ref this); return;
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldloc, aLoc); _il.Emit(OpCodes.Stfld, awaiterField[k]);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, fBuilder);
        _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Call, GenM(builderT, "AwaitUnsafeOnCompleted").MakeGenericMethod(at, sm));
        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Ret);   // `ret` is illegal inside a .try
        // resume: awaiter = <>u__k; <>u__k = default; state = -1;
        _il.MarkLabel(resume[k]);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, awaiterField[k]); _il.Emit(OpCodes.Stloc, aLoc);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, awaiterField[k]); _il.Emit(OpCodes.Initobj, at);
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(-1); _il.Emit(OpCodes.Stfld, fState);
        // after: <assignTo> = awaiter.GetResult();
        _il.MarkLabel(after[k]);
        var assignTo = st.GetProperty("assignTo").ValueKind == JsonValueKind.Null ? null : st.GetProperty("assignTo").GetString();
        var getResult = GenM(at, "GetResult");
        bool voidResult = getResult.ReturnType == typeof(void);
        if (assignTo != null && coFields.TryGetValue(assignTo, out var destF))
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, getResult);
            _il.Emit(OpCodes.Stfld, destF);
        }
        else if (assignTo != null && !voidResult)
        {
            // A non-field temp (e.g. `return await(...)`): a fresh IL local read by the following coReturn.
            var tmp = _il.DeclareLocal(GenM(at, "GetResult").ReturnType);
            _locals[assignTo] = tmp;
            _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, getResult);
            _il.Emit(OpCodes.Stloc, tmp);
        }
        else
        {
            _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, getResult);
            if (!voidResult) _il.Emit(OpCodes.Pop);
        }
    }
}
