// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Globalization;

// CLR interop emission: @Clr native calls, property/event access, ctor picking, BCL-intrinsic handlers.
sealed partial class Emitter
{
    Type EmitNativeClrSafeCastValue(JsonElement e)
    {
        // `x as? T` for value T -> `T?`: isinst boxed-T, then unbox+wrap, else empty Nullable<T>.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var res = _il.DeclareLocal(nt);
        var has = _il.DefineLabel();
        var done = _il.DefineLabel();
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Isinst, elem);
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Brtrue, has);
        _il.Emit(OpCodes.Pop);
        _il.Emit(OpCodes.Ldloca, res);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, res);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(has);
        _il.Emit(OpCodes.Unbox_Any, elem);
        EmitConstructor(_il, OpCodes.Newobj, RequiredRef<ConstructorInfo>(e, "ctorRef", "a nullable conversion"));
        _il.MarkLabel(done);
        return nt;
    }

    Type EmitNativeClrNullableNull(JsonElement e)
    {
        // `null` typed as Int? -> a Nullable<T> with HasValue=false. NOT ldnull: a value type has no null reference.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, loc);
        return nt;
    }

    Type EmitNativeClrNullableWrap(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        EmitExpr(e.GetProperty("e"));
        EmitConstructor(_il, OpCodes.Newobj, RequiredRef<ConstructorInfo>(e, "ctorRef", "a nullable conversion"));
        return nt;
    }

    Type EmitNativeClrNullableHasValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        EmitExpr(e.GetProperty("e"));
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        EmitMethod(_il, OpCodes.Call, RequiredRef<MethodInfo>(e, "hasValueRef", "a nullable conversion"));
        return Bcl("System.Boolean");
    }

    Type EmitNativeClrNullableValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var src = EmitExpr(e.GetProperty("e"));
        // REDUNDANT unwrap: the source already holds the non-nullable `elem`, not a `Nullable<elem>`. The 2.4.0
        // frontend emits a nested `nullableValue{ nullableValue{ x } }` for a safe-call member access, so the outer
        // node lands on an already-unwrapped value. `stloc` of a raw `elem` into a `Nullable<elem>` local would
        // REINTERPRET its bytes as the {HasValue,value} struct layout -> `.Value` reads garbage (a Char 'x' -> 0).
        // The stack already holds `elem` -- nothing to do.
        if (src == elem) return elem;
        // Any source that is neither the unwrapped `elem` nor the `Nullable<elem>` we expect would be
        // silently byte-reinterpreted by the `stloc` below. Fail loud so a future frontend shape surfaces
        // at emit time rather than as another garbage value.
        if (src != null && src != nt)
            throw new NotSupportedException($"nullableValue: source {src} is neither {elem} nor {nt}");
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        EmitMethod(_il, OpCodes.Call, RequiredRef<MethodInfo>(e, "valueRef", "a nullable conversion"));
        return elem;
    }

    Type EmitNativeClrTypeOf(JsonElement e)
    {
        var t = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, t);
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Type.FromHandle"));
        return Bcl("System.Type");
    }

    Type EmitNativeClrGetType(JsonElement e)
    {
        var got = EmitExpr(e.GetProperty("e"));
        if (got != null && NeedsBoxToRef(got)) _il.Emit(OpCodes.Box, got);
        EmitMethod(_il, OpCodes.Callvirt, WellKnown<MethodInfo>("Object.GetType"));
        return Bcl("System.Type");
    }

    Type EmitNativeClrEnumValue(JsonElement e)
    {
        if (e.TryGetProperty("physicalValue", out var pv)
            && e.TryGetProperty("underlying", out var ut))
        {
            var text = pv.GetString();
            switch (ut.GetString())
            {
                case "System.Int64":
                    _il.Emit(OpCodes.Ldc_I8, long.Parse(text, CultureInfo.InvariantCulture));
                    break;
                case "System.UInt64":
                    _il.Emit(OpCodes.Ldc_I8, unchecked((long)ulong.Parse(text, CultureInfo.InvariantCulture)));
                    break;
                case "System.UInt32":
                    _il.Emit(OpCodes.Ldc_I4, unchecked((int)uint.Parse(text, CultureInfo.InvariantCulture)));
                    break;
                case "System.Byte":
                case "System.UInt16":
                    _il.Emit(OpCodes.Ldc_I4, int.Parse(text, CultureInfo.InvariantCulture));
                    break;
                default:
                    _il.Emit(OpCodes.Ldc_I4, int.Parse(text, CultureInfo.InvariantCulture));
                    break;
            }
        }
        else
        {
            // A locally-declared Kotlin basic enum has contiguous 0..N values by construction.
            _il.Emit(OpCodes.Ldc_I4, e.GetProperty("ordinal").GetInt32());
        }
        return NativeType(e.GetProperty("type"));
    }

    Type EmitNativeClrEnumOrdinal(JsonElement e)
    {
        // A LOCAL value-type enum's ordinal == its underlying value (kotc assigns contiguous 0..n) -> a plain Conv_I4.
        // A REFERENCED .NET enum (#107 — the node carries the enum `type`) may have sparse/negative/aliased values, so
        // its Kotlin ordinal (the DECLARATION INDEX) is Array.IndexOf(Enum.GetValues(t), value), NOT the underlying int.
        if (e.TryGetProperty("type", out var tp))
        {
            var et = NativeType(tp);
            _il.Emit(OpCodes.Ldtoken, et);
            EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Type.FromHandle"));
            EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Enum.GetValues"));
            EmitExpr(e.GetProperty("e"));
            _il.Emit(OpCodes.Box, et);
            EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Array.IndexOf"));
            return Bcl("System.Int32");
        }
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Conv_I4);
        return Bcl("System.Int32");
    }

    Type EmitNativeClrEnumValues(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Type.FromHandle"));
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Enum.GetValues"));
        _il.Emit(OpCodes.Castclass, et.MakeArrayType());
        return et.MakeArrayType();
    }

    Type EmitNativeClrEnumParse(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Type.FromHandle"));
        EmitExpr(e.GetProperty("arg"));
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Enum.Parse"));
        _il.Emit(OpCodes.Unbox_Any, et);
        return et;
    }

    Type EmitClrNew(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var args = e.GetProperty("args");
        // bir2cir already resolved the constructor and carried its complete scalar memberRef. Link that exact
        // declaration; there is no argument-applicability or arity fallback here.
        var openCtor = LinkClrCtor(type, e, out var tb);
        if (tb)
        {
            // A generic collection constructed with an EMITTED element type (`new HashSet<EmittedType>()`) is a
            // TypeBuilderInstantiation whose members can't be reflected — the winner was matched on the OPEN def. Emit
            // the args against the SUBSTITUTED param types (so a delegate/closure arg's rewrap target is the CLOSED
            // param `Func<Box>`, not the open `Func<T>`), then re-anchor the ctor.
            var classArgs = type.GetGenericArguments();
            var openPs = openCtor.GetParameters();
            int ai = 0;
            foreach (var a in args.EnumerateArray()) { EmitArg(a, SubstituteIfaceArgs(openPs[ai].ParameterType, classArgs)); ai++; }
            RequireArgCount(ai, openPs.Length, openCtor.ToString());
            EmitConstructor(_il, OpCodes.Newobj, AnchorOn(type, openCtor));
            return type;
        }
        EmitArgs(args, openCtor.GetParameters());
        EmitConstructor(_il, OpCodes.Newobj, openCtor);
        return type;
    }

    // The scalar reference names the open declaration. A TypeBuilderInstantiation is re-anchored mechanically after
    // lookup; that operation changes no member-selection decision.
    /// <summary>The constructor a `newClr` or a base delegation names. A lookup, not a choice.</summary>
    ConstructorInfo LinkClrCtor(Type type, JsonElement e, out bool tb, string carrier = "memberRef",
        bool includeNonPublic = false)
    {
        tb = IsTbInstantiation(type);
        if (PrimaryFromRef(e, carrier) is ConstructorInfo referenced) return referenced;
        throw new InvalidOperationException(
            $"ilemit: construction of {type?.FullName} carries no resolved `{carrier}`. Every external member "
            + "arrives named; a node without one is an earlier-layer drop (#370)");
    }

    // The scalar reference supplies the complete declaration identity. This helper only projects that identity into
    // the target metadata universe; it does not construct or rank a candidate set from the call node.
    /// <summary>The method a `clr*` call names. A lookup, not a choice.</summary>
    MethodInfo LinkClrMethod(Type type, string name, JsonElement e, bool instance)
    {
        if (PrimaryFromRef(e, "memberRef") is MethodInfo referenced) return referenced;
        throw new InvalidOperationException(
            $"ilemit: clr{(instance ? "Instance" : "Static")} call to {type?.FullName}.{name} carries no resolved "
            + "member reference. Every external member arrives named; a node without one is an earlier-layer drop (#370)");
    }

    static bool IsPublicOrProtected(MethodBase member) =>
        member.IsPublic || member.IsFamily || member.IsFamilyOrAssembly;

    static string TypeIdentity(Type declaring)
    {
        try { if (declaring.IsGenericType && !declaring.IsGenericTypeDefinition) declaring = declaring.GetGenericTypeDefinition(); }
        catch { }
        return declaring.FullName ?? declaring.Name;
    }

    static Type[] SafeInterfaces(Type t) { try { return t.GetInterfaces(); } catch { return Array.Empty<Type>(); } }

    // The declaration-side clrOverrideRef names the exact MethodImpl target. A generic base's resolved declaration is
    // re-anchored mechanically onto the constructed base before DefineMethodOverride.
    /// <summary>The base virtual an external override implements. A lookup, not a choice.</summary>
    MethodInfo LinkOverrideBase(Type baseT, string name, JsonElement m, Type derivedTb)
    {
        if (PrimaryFromRef(m, "clrOverrideRef") is MethodInfo referenced) return referenced;
        throw new InvalidOperationException(
            $"ilemit: override of {baseT?.FullName}.{name} carries no resolved `clrOverrideRef`. Every external "
            + "member arrives named; a node without one is an earlier-layer drop (#370)");
    }

    // The bir2cir clrInstance `dispatch` decision (call | callvirt | constrained) -> the IL opcode. bir2cir computed it
    // from the resolved MethodInfo IsVirtual/IsFinal + owner value-type-ness + the `super` base-slot flag (issue #14);
    // ilemit no longer re-derives it. `constrained.` prefixes the callvirt with the receiver type (== the node `type`).
    void EmitClrDispatch(MethodInfo mi, string dispatch, Type recvType)
    {
        switch (dispatch)
        {
            case "call": EmitMethod(_il, OpCodes.Call, mi); break;
            case "callvirt": EmitMethod(_il, OpCodes.Callvirt, mi); break;
            case "constrained": _il.Emit(OpCodes.Constrained, recvType); EmitMethod(_il, OpCodes.Callvirt, mi); break;
            default: throw new NotSupportedException($"ilemit: unknown clrInstance dispatch '{dispatch}' on {mi.DeclaringType}.{mi.Name} (bir2cir must emit call|callvirt|constrained — W1-S2 #46)");
        }
    }

    Type EmitClrCall(JsonElement e, bool instance, bool deref = true)
    {
        // `ClrRef` (not `ResolveType`) so a method on a constructed generic .NET type (`Collection<int>`) resolves.
        var type = ClrRef(e.GetProperty("type"));
        var name = e.GetProperty("method").GetString();
        // bir2cir already resolved the overload and carried the complete memberRef; ilemit only links that identity.
        var mi = LinkClrMethod(type, name, e, instance);
        // A generic BCL method (`System.Array.Fill<T>(T[],T,int,int)`) resolved as its open DEFINITION must be
        // instantiated with the call's type args (threaded by bir2cir from the @ClrIntrinsic generic Kotlin callee),
        // or the emitted MethodSpec stays open -> "method/type not fully instantiated" at run. Non-generic targets
        // leave IsGenericMethodDefinition false, so this is a no-op there.
        if (mi.IsGenericMethodDefinition
            && e.TryGetProperty("typeArgs", out var clrTa) && clrTa.ValueKind == JsonValueKind.Array && clrTa.GetArrayLength() > 0)
            mi = ConstructedMethod(mi, clrTa.EnumerateArray().Select(a => MapType(a)).ToArray());
        // A value-type receiver's instance method needs a managed pointer (e.g. struct Vec2.Mag2()); the `constrained.`
        // dispatch (below) likewise needs the receiver ADDRESS. (A generic `Array<T>.clone()` is already retargeted by
        // bir2cir to the `System.Array` owner, so its `T[]` receiver is statically assignable — no cast needed here.)
        if (instance)
        {
            if (IsValueType(type)) EmitAddr(e.GetProperty("recv"));
            else EmitExpr(e.GetProperty("recv"));
        }
        EmitArgs(e.GetProperty("args"), ParametersOf(mi));
        // Dispatch is a bir2cir DECISION carried on the node (call | callvirt | constrained) — computed from the
        // resolved MethodInfo's IsVirtual/IsFinal + the owner value-type-ness + the `super` (issue #14 base-slot) flag.
        // ilemit no longer derives it from reflected `mi.IsVirtual/IsFinal`. A static call is unconditionally `call`.
        if (instance)
        {
            // `dispatch` is a REQUIRED bir2cir decision — a missing one is a producer defect, NOT a silent `callvirt`
            // default (which on a value-type owner is unverifiable CallVirtOnValueType). Fail loud (consume-only doctrine).
            if (!e.TryGetProperty("dispatch", out var dEl) || dEl.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"ilemit: clrInstance {type?.FullName}.{name} is missing its `dispatch` decision (bir2cir must carry call|callvirt|constrained — W1-S2 #46)");
            EmitClrDispatch(mi, dEl.GetString(), type);
        }
        else
            EmitMethod(_il, OpCodes.Call, mi);
        // A `ref T`-returning method used as a value -> dereference the managed pointer (value copy). The live-ref
        // form (`byrefOf(m())`, behind `var x by byref(m())`) passes deref:false to keep the pointer.
        var methodReturn = ReturnTypeOf(mi);
        if (methodReturn.IsByRef)
        {
            if (!deref) return methodReturn;
            var elem = methodReturn.GetElementType();
            _il.Emit(OpCodes.Ldobj, elem);
            return elem;
        }
        // TypeBuilder.GetMethod re-anchors the call onto the instantiation but leaves the method's RETURN type open
        // (e.g. `Task<Vec>::GetAwaiter()` reports `TaskAwaiter`1<!0>`, not `<Vec>`). The IL token is correct, but the
        // STATIC type we hand back must be the substituted one or the caller mis-types its temp/local — so trust the
        // BIR `ret` hint, which already carries the substituted type. (Only when the reflected return is still open.)
        if ((methodReturn.IsGenericParameter || methodReturn.ContainsGenericParameters)
            && e.TryGetProperty("ret", out var rh))
        {
            // Post type-flip the `ret` hint is a STRUCTURED TypeNode (`{"t":"tv","scope":"method",...}`); the legacy
            // form was a bare string. A method-scope Tv return (`IReadOnlyList<T>::get_Item` on the non-generic
            // `_CollectionsKt.firstOrNull<T>`) reflects as the OPEN interface param `!0` (type-scope, position 0) —
            // a standalone `!0` token in a non-generic method body is INVALID metadata (`box !0` -> BadImageFormat at
            // JIT). MapType resolves the structured Tv against `_curMethodParams` to the method's own `!!T`, so the
            // caller boxes/consumes the correct method-scope token. (String hint keeps the legacy ClrRef path.)
            Type hinted = rh.ValueKind == JsonValueKind.String ? TryResolveClr(rh.GetString())
                        : rh.ValueKind == JsonValueKind.Object ? TryMapType(rh) : null;
            if (hinted != null) return hinted;
        }
        return methodReturn;
    }

    Type[] NativeParameterTypes(JsonElement member) =>
        member.GetProperty("parameterTypes").EnumerateArray()
            .Select(t => TryResolveNativeType(t.GetString()))
            .ToArray();

    Type TryResolveNativeType(string spec)
    {
        try { return NativeType(spec); }
        catch { return null; }
    }

    // A type slot for an IL-opcode context (newarr elem / conv / default): a structured node resolves via MapType, a
    // bare type-IDENTITY string (a CLR-shorthand primitive, or a bare FQN) via the identity path below (the legacy
    // grammar prefixes are retired, #48).
    Type NativeType(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? MapType(DotKt.Bir.TypeNode.Read(e)) : NativeType(e.GetString());

    Type NativeType(string spec)
    {
        if (spec == null) return Bcl("System.Object");
        return spec switch
        {
            "void" or "int" or "long" or "double" or "float" or "bool" or "char" or "string" or
            "uint" or "ulong" or "byte" or "ushort" or "short" or "sbyte" or "object" => MapType(spec),
            _ => ClrRef(spec),
        };
    }

    static string NativeOwnerSpec(JsonElement node, JsonElement member) =>
        node.TryGetProperty("ownerType", out var ownerType) && ownerType.ValueKind != JsonValueKind.Null
            ? SlotName(ownerType)
            : SlotName(member.GetProperty("owner"));

    // ClrRef (generic-aware type resolution) that returns null instead of throwing.
    Type TryResolveClr(string spec) { try { return ClrRef(spec); } catch { return null; } }

    // MapType (structured-TypeNode resolution) that returns null instead of throwing.
    Type TryMapType(JsonElement e) { try { return MapType(e); } catch { return null; } }

    // ResolveType but returns null instead of throwing (for optional/best-effort overload resolution).
    Type TryResolveType(string name)
    {
        try { return ResolveType(name); } catch (NotSupportedException) { return null; }
    }

    // W1-S3 (#46 / #121) CONSUME-ONLY property GET. bir2cir (ClrMemberResolution) resolved the member against the ref.dll
    // and stamped a `member` discriminator: "accessor" (a real .NET property — carries the
    // resolved accessor memberRef + `dispatch`) or "field" (a public/const field). ilemit no longer reclassifies
    // property vs get_ method vs field, and no longer derives dispatch from the reflected accessor.
    Type EmitClrPropGet(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        if (ClrMemberKind(e) == "accessor")
        {
            var getter = LinkClrMethod(type, e.GetProperty("accessor").GetString(), e, instance: !isStatic);
            if (!isStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
            if (isStatic) EmitMethod(_il, OpCodes.Call, getter);
            else EmitClrDispatch(getter, RequireDispatch(e, type, "clrPropGet"), type);   // call | callvirt | constrained (bir2cir decision)
            return ReturnTypeOf(getter);
        }
        // A .NET FIELD surfaced as a Kotlin property (bir2cir resolved member:"field"). A `const` (literal) field has no
        // storage — inline its value (a mechanical raw-constant fetch, not a KIND decision), exactly as C# does.
        // The reference first: a field is a member like any other, and re-finding it by name is the selection
        // this change exists to remove — a derived type's static field can hide a base instance field of the
        // same name, and a name lookup cannot tell which one bir2cir resolved.
        var fld = RequiredRef<FieldInfo>(e, "memberRef", "an external field access");
        if (fld.IsLiteral) return EmitLiteralValue(fld.GetRawConstantValue(), FieldTypeOf(fld));
        if (!isStatic && !fld.IsStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        MaybeVolatile(fld, e);
        EmitField(_il, fld.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, fld);
        return FieldTypeOf(fld);
    }

    // W1-S3 (#46 / #121) CONSUME-ONLY property SET (twin of EmitClrPropGet).
    Type EmitClrPropSet(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        if (ClrMemberKind(e) == "accessor")
        {
            var setter = LinkClrMethod(type, e.GetProperty("accessor").GetString(), e, instance: !isStatic);
            if (!isStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
            EmitArgs2(new[] { e.GetProperty("value") }, ParametersOf(setter));
            if (isStatic) EmitMethod(_il, OpCodes.Call, setter);
            else EmitClrDispatch(setter, RequireDispatch(e, type, "clrPropSet"), type);
            return Bcl("System.Void");
        }
        // A writable .NET FIELD surfaced as a Kotlin (mutable) property -> field store.
        // The reference first: a field is a member like any other, and re-finding it by name is the selection
        // this change exists to remove — a derived type's static field can hide a base instance field of the
        // same name, and a name lookup cannot tell which one bir2cir resolved.
        var fld = RequiredRef<FieldInfo>(e, "memberRef", "an external field access");
        if (!isStatic && !fld.IsStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        EmitNullableCoerced(e.GetProperty("value"), FieldTypeOf(fld));
        MaybeVolatile(fld, e);
        EmitField(_il, fld.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, fld);
        return Bcl("System.Void");
    }

    // The bir2cir `member` discriminator ("accessor" | "field"), a REQUIRED W1-S3 decision. A missing one is a producer
    // defect (ilemit must not re-derive the member kind), not a silent field fallback.
    static string ClrMemberKind(JsonElement e)
    {
        if (e.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
        throw new InvalidOperationException($"ilemit: clr property/field node is missing its `member` discriminator (bir2cir ClrMemberResolution must carry accessor|field — W1-S3 #46/#121)");
    }

    static string RequireDispatch(JsonElement e, Type type, string what)
    {
        if (e.TryGetProperty("dispatch", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
        throw new InvalidOperationException($"ilemit: {what} on {type?.FullName} is missing its `dispatch` decision (bir2cir must carry call|callvirt|constrained — W1-S3 #46)");
    }

    // `.NET event +=/-=` -> call the event's add/remove accessor with the handler bound as the event's OWN
    // delegate type (e.g. EventHandler), not the Func/Action the lambda would otherwise produce. The lifted
    // method's signature matches the delegate's Invoke (the reference KLIB typed the handler from the event's
    // handler signature), so `ldftn`+`newobj <EventDelegate>(object, IntPtr)` is verifiable — exactly what
    // `button.Click += (s,e)=>{}` lowers to in C#.
    // W1-S3 (#46 / #121 / #113) CONSUME-ONLY event add/remove. bir2cir (ClrMemberResolution) resolved the EventInfo off
    // the ref.dll and stamped the add/remove accessor memberRef plus `dispatch`.
    // ilemit LINKS the exact accessor (LinkClrMethod — hard-fails a missing/ambiguous slot, so the old unchecked
    // `GetEvent(...).GetAddMethod()` NRE on a missing/value-type/constructed-generic event is gone) and consumes the
    // carried dispatch. The handler delegate type flows from the resolved accessor's first param (== EventHandlerType).
    Type EmitClrEvent(JsonElement e, bool add)
    {
        var type = ClrRef(e.GetProperty("type"));
        bool isStatic = e.GetProperty("static").GetBoolean();
        var accessor = LinkClrMethod(type, e.GetProperty("accessor").GetString(), e, instance: !isStatic);
        var delType = ParametersOf(accessor)[0].ParameterType;   // == the event's EventHandlerType
        if (!isStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        if (e.TryGetProperty("handlerExact", out var exact) && exact.GetBoolean())
            EmitExpr(e.GetProperty("handler"));
        else
            EmitHandlerAsDelegate(e.GetProperty("handler"), delType, e);
        if (isStatic) EmitMethod(_il, OpCodes.Call, accessor);
        else EmitClrDispatch(accessor, RequireDispatch(e, type, add ? "clrEventAdd" : "clrEventRemove"), type);
        return Bcl("System.Void");
    }

    // Resolve a newClosure node's ctor + invoke, INSTANTIATING the closure generic when it is a generic definition.
    // A capturing closure over an enclosing type param (`{ seed }` in `generateSequence<T>`) is a GENERIC class;
    // left as its open definition the `newobj Closure`1::.ctor(!0)` operand is OPEN -> a TypeLoadException at run.
    // Close it with the node's explicit `typeArgs`, else (C13a: kotc/bir2cir omitted them for the non-`this`-capturing
    // form) with the enclosing params matched by NAME (GenericParamByName). Shared by the
    // main newClosure emit and the delegate-arg binding path so neither can diverge.
    (ConstructorInfo Ctor, MethodInfo Invoke) ResolveClosure(JsonElement e)
    {
        var ct = _types[SlotName(e.GetProperty("closureType"))];
        ConstructorInfo ctor = ct.Ctor;
        MethodInfo invoke = ct.Methods[e.GetProperty("method").GetString()];
        Type constructed = null;
        if (e.TryGetProperty("typeArgs", out var taProp) && taProp.GetArrayLength() > 0)
            constructed = ConstructedType(ct.TB, taProp.EnumerateArray().Select(a => MapType(a)).ToArray());
        else if (ct.TB.IsGenericTypeDefinition)
            constructed = ConstructedType(ct.TB, ct.TB.GetGenericArguments().Select(gp => GenericParamByName(gp.Name)).ToArray());
        if (constructed != null)
        {
            ctor = AnchorConstructor(constructed, ct.Ctor);
            invoke = AnchorMethod(constructed, invoke);
        }
        return (ctor, invoke);
    }

    // §4.2/§4.3 (#187) — the body of a SYNTHESIZED field-like .NET event accessor. bir2cir (ClrEventImplBinding) resolved
    // the concrete delegate `D` off the ref.dll and inserted the `<E>$delegate : D` backing field; ilemit emits pure CLR
    // codegen from the `clrEventAccessorImpl{kind, name:<field>, delegateType:D}` directive — the C# field-like-event shape:
    //   add/remove -> a lock-free CAS loop (Delegate.Combine/Remove + Interlocked.CompareExchange<D>);
    //   raise      -> `field?.Invoke(args...)` (the null-conditional makes a zero-subscriber raise a safe no-op).
    // The method's trailing `ret` is appended by EmitTrailingRet (all three are void). ilemit knows NO Kotlin here — the
    // delegate + field are already resolved. (§9 — ClrEvent<T>/clrEvent() never reach this layer.)
    void EmitClrEventAccessorImpl(JsonElement e)
    {
        var kind = e.GetProperty("kind").GetString();
        var fieldName = e.GetProperty("name").GetString();
        var d = MapType(e.GetProperty("delegateType"));
        // S5 (#113): a missing backing field is a bir2cir/kotc synthesis defect — a legible breadcrumb, not an opaque miss.
        if (_curTi == null || !_curTi.Fields.TryGetValue(fieldName, out var field))
            throw new InvalidOperationException($"ilemit: clrEventAccessorImpl backing field '{fieldName}' is absent on '{_curTi?.TB?.Name}' (bir2cir ClrEventImplBinding must insert `<E>$delegate` — #187/#113)");
        if (kind == "raise")
        {
            EmitClrEventRaise(field, d,
                RequiredRef<MethodInfo>(e, "invokeRef", "clrEventAccessorImpl raise"));
            return;
        }
        EmitClrEventCas(e, field, d, add: kind == "add");
    }

    // The lock-free add/remove CAS loop over the backing delegate field (C#'s field-like-event accessor shape):
    //   D cur = this.field; do { D cmp = cur; D upd = (D)Delegate.Combine/Remove(cmp, value);
    //       cur = Interlocked.CompareExchange<D>(ref this.field, upd, cmp); } while (cur != cmp);
    // arg0 = this, arg1 = the handler `value`. Reference comparison (bne.un), not delegate equality.
    void EmitClrEventCas(JsonElement node, FieldInfo field, Type d, bool add)
    {
        // The three members this loop runs through are named by the pass that authored the accessor.
        var combine = RequiredRef<MethodInfo>(node, add ? "combineRef" : "removeRef", "clrEventAccessorImpl");
        var cmpx = ConstructedMethod(RequiredRef<MethodInfo>(node, "compareExchangeRef", "clrEventAccessorImpl"), d);
        var cur = _il.DeclareLocal(d); var cmp = _il.DeclareLocal(d); var upd = _il.DeclareLocal(d);
        var retry = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); EmitField(_il, OpCodes.Ldfld, field); _il.Emit(OpCodes.Stloc, cur);
        _il.MarkLabel(retry);
        _il.Emit(OpCodes.Ldloc, cur); _il.Emit(OpCodes.Stloc, cmp);
        _il.Emit(OpCodes.Ldloc, cmp); _il.Emit(OpCodes.Ldarg_1);
        EmitMethod(_il, OpCodes.Call, combine); _il.Emit(OpCodes.Castclass, d); _il.Emit(OpCodes.Stloc, upd);
        _il.Emit(OpCodes.Ldarg_0); EmitField(_il, OpCodes.Ldflda, field);
        _il.Emit(OpCodes.Ldloc, upd); _il.Emit(OpCodes.Ldloc, cmp);
        EmitMethod(_il, OpCodes.Call, cmpx); _il.Emit(OpCodes.Stloc, cur);
        _il.Emit(OpCodes.Ldloc, cur); _il.Emit(OpCodes.Ldloc, cmp); _il.Emit(OpCodes.Bne_Un, retry);
    }

    // `raise_<E>(args...)`: snapshot the backing field, and if non-null invoke it with the raise method's own params.
    // arg0 = this; the raise params (== D.Invoke's params) start at arg1. The null check = C#'s field-like `field?.Invoke`.
    void EmitClrEventRaise(FieldInfo field, Type d, MethodInfo invoke)
    {
        var handler = _il.DeclareLocal(d);
        var done = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); EmitField(_il, OpCodes.Ldfld, field); _il.Emit(OpCodes.Stloc, handler);
        _il.Emit(OpCodes.Ldloc, handler); _il.Emit(OpCodes.Brfalse, done);
        _il.Emit(OpCodes.Ldloc, handler);
        var n = ParametersOf(invoke).Length;
        for (int i = 0; i < n; i++) _il.Emit(OpCodes.Ldarg, checked((short)(i + 1)));
        EmitMethod(_il, OpCodes.Callvirt, invoke);
        _il.MarkLabel(done);
    }

    // Bind a lambda handler (newDelegate = non-capturing, newClosure = capturing) into a SPECIFIC delegate type.
    // Mirrors the newDelegate/newClosure cases but uses `want` (the event's delegate type) for the ctor.
    void EmitHandlerAsDelegate(JsonElement h, Type want, JsonElement? eventNode = null)
    {
        // The delegate's Invoke return type: when it is a real (non-void) type while the lambda's NATURAL delegate is
        // void-returning (a Unit body maps to void), binding the void method-pointer into the ctor is not verifiable
        // -> self-build the natural void delegate and wrap it in a Unit-return adapter. InvokeRetOf (not want.GetMethod/
        // InvokeOf) so a TypeBuilder-arg `want` (`Func<Res,Unit>`) yields the CLOSED return type (`kotlin.Unit`) — a
        // by-name lookup throws and InvokeOf's ReturnType comes back unsubstituted.
        var invokeRet = InvokeRetOf(want);
        var k = h.GetProperty("k").GetString();
        if (invokeRet != Bcl("System.Void") && (k == "newDelegate" || k == "newClosure")
            && FuncRetType(h.GetProperty("funcType")) == Bcl("System.Void"))
        {
            var ft = EmitExpr(h);                             // the lambda's natural void delegate, on the stack
            EmitMethod(_il, OpCodes.Ldftn, UnitWrapAdapter(ft, invokeRet, FuncArgTypes(h.GetProperty("funcType")).ToArray(),
                PrimaryFromRef(h, "unitInstanceRef") as FieldInfo,
                RequiredRef<MethodInfo>(h, "invokeRef", "a void-to-Unit conversion")));
            EmitDelegateCtor(_il, want, h, eventNode);
            return;
        }
        switch (k)
        {
            case "newDelegate":
                // Mirrors Emitter.Expressions: mandatory calleeOwner selects the one file class and sig selects the
                // overload. Missing/misspelled ownership is malformed CIR, never a global name lookup (#204).
                var dname = h.GetProperty("method").GetString();
                var dsig = SigNodes(h);
                var dmethod = PrimaryFromRef(h, "memberRef") as MethodInfo
                    ?? FindCalleeOwnedStatic(h, "event newDelegate", dname, dsig, CalledMethodArity(h));
                var dtarget = h.TryGetProperty("typeArgs", out var dta) && dta.GetArrayLength() > 0
                    && dmethod.IsGenericMethodDefinition
                    ? ConstructedMethod(dmethod, dta.EnumerateArray().Select(x => MapType(x)).ToArray())
                    : dmethod;
                _il.Emit(OpCodes.Ldnull);
                EmitMethod(_il, OpCodes.Ldftn, dtarget);
                EmitDelegateCtor(_il, want, h, eventNode);
                break;
            case "newClosure":
                var (cctor, cinvoke) = ResolveClosure(h);
                foreach (var c in h.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                EmitConstructor(_il, OpCodes.Newobj, cctor);
                EmitMethod(_il, OpCodes.Ldftn, cinvoke);
                EmitDelegateCtor(_il, want, h, eventNode);
                break;
            default:
                // A stored handler value (a Func/Action local/field). Re-wrap it into the event's delegate
                // type via its Invoke — `new EventDelegate(value.Invoke)`. Two wrappers around the SAME stored
                // value share target+method, so Delegate equality holds and `-=` removes the right handler.
                var src = EmitExpr(h);                       // stack: the stored delegate value
                _il.Emit(OpCodes.Dup);
                if (eventNode == null)
                    throw new InvalidOperationException(
                        "ilemit: a stored delegate re-wrap is missing its resolved Invoke reference");
                EmitMethod(_il, OpCodes.Ldvirtftn,
                    RequiredRef<MethodInfo>(eventNode.Value, "invokeRef", "CLR event handler re-wrap"));
                EmitDelegateCtor(_il, want, eventNode.Value);
                break;
        }
    }

}
