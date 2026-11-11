// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotKt.Bir;
using DotKt.Cir;
using System.Globalization;

// CLR interop emission: @Clr native calls, property/event access, ctor picking, BCL-intrinsic handlers.
sealed partial class Emitter
{
    Type EmitNativeClrSafeCastValue(CirSafeCastValue e)
    {
        // `x as? T` for value T -> `T?`: isinst boxed-T, then unbox+wrap, else empty Nullable<T>.
        var elem = NativeType(e.Elem);
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var res = _il.DeclareLocal(nt);
        var has = _il.DefineLabel();
        var done = _il.DefineLabel();
        EmitExpr(e.E);
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
        EmitConstructor(_il, OpCodes.Newobj, RequiredRef<ConstructorInfo>(e.CtorRef, "a nullable conversion"));
        _il.MarkLabel(done);
        return nt;
    }

    Type EmitNativeClrNullableNull(CirNullableNull e)
    {
        // `null` typed as Int? -> a Nullable<T> with HasValue=false. NOT ldnull: a value type has no null reference.
        var elem = NativeType(e.Elem);
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, loc);
        return nt;
    }

    Type EmitNativeClrNullableWrap(CirNullableWrap e)
    {
        var elem = NativeType(e.Elem);
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        EmitExpr(e.E);
        EmitConstructor(_il, OpCodes.Newobj, RequiredRef<ConstructorInfo>(e.CtorRef, "a nullable conversion"));
        return nt;
    }

    Type EmitNativeClrNullableHasValue(CirNullableHasValue e)
    {
        var elem = NativeType(e.Elem);
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        EmitExpr(e.E);
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        EmitMethod(_il, OpCodes.Call, RequiredRef<MethodInfo>(e.HasValueRef, "a nullable conversion"));
        return Bcl("System.Boolean");
    }

    Type EmitNativeClrNullableValue(CirNullableValue e)
    {
        var elem = NativeType(e.Elem);
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var src = EmitExpr(e.E);
        // REDUNDANT unwrap: the source already holds the non-nullable `elem`, not a `Nullable<elem>`. The 2.4.0
        // frontend emits a nested `nullableValue{ nullableValue{ x } }` for a safe-call member access, so the outer
        // node lands on an already-unwrapped value. `stloc` of a raw `elem` into a `Nullable<elem>` local would
        // REINTERPRET its bytes as the {HasValue,value} struct layout -> `.Value` reads garbage (a Char 'x' -> 0).
        // The stack already holds `elem` -- nothing to do.
        if (SameEmittedType(src, elem)) return elem;
        // Any source that is neither the unwrapped `elem` nor the `Nullable<elem>` we expect would be
        // silently byte-reinterpreted by the `stloc` below. Fail loud so a future frontend shape surfaces
        // at emit time rather than as another garbage value.
        if (src != null && !SameEmittedType(src, nt))
            throw new NotSupportedException($"nullableValue: source {src} is neither {elem} nor {nt}");
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        EmitMethod(_il, OpCodes.Call, RequiredRef<MethodInfo>(e.ValueRef, "a nullable conversion"));
        return elem;
    }

    // Persisted/local signature views may be distinct System.Type wrappers for the same emitted type (notably a
    // constructed Nullable<local enum>). Reference equality therefore is not a sufficient validation. The member-ref
    // provenance identity retains assembly identity and recursively retains generic arguments, so unlike FullName it
    // still refuses same-named types supplied by different referenced assemblies.
    static bool SameEmittedType(Type left, Type right) =>
        ReferenceEquals(left, right)
        || left != null && right != null
            && string.Equals(ProvenanceTypeIdentity(left), ProvenanceTypeIdentity(right), StringComparison.Ordinal);

    Type EmitNativeClrTypeOf(CirClassReference e)
    {
        var t = NativeType(e.Type);
        _il.Emit(OpCodes.Ldtoken, t);
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Type.FromHandle"));
        return Bcl("System.Type");
    }

    Type EmitNativeClrGetType(CirGetType e)
    {
        var got = EmitExpr(e.E);
        if (got != null && NeedsBoxToRef(got)) _il.Emit(OpCodes.Box, got);
        EmitMethod(_il, OpCodes.Callvirt, WellKnown<MethodInfo>("Object.GetType"));
        return Bcl("System.Type");
    }

    Type EmitNativeClrEnumValue(CirEnumValue e)
    {
        if (e.PhysicalValue.IsPresent && e.Underlying.IsPresent)
        {
            EmitEnumConstantBits(e.Underlying.Value, e.PhysicalValue.Value);
        }
        else
        {
            // A locally-declared Kotlin basic enum has contiguous 0..N values by construction.
            _il.Emit(OpCodes.Ldc_I4, e.Ordinal.Value);
        }
        return NativeType(e.Type);
    }

    Type EmitNativeClrEnumOrdinal(CirEnumOrdinal e)
    {
        if (e.Values.IsPresent)
        {
            var et = NativeType(e.Type.Value);
            var local = _il.DeclareLocal(et);
            EmitExpr(e.E);
            _il.Emit(OpCodes.Stloc, local);
            var done = _il.DefineLabel();
            foreach (var value in e.Values.Value)
            {
                var next = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, local);
                EmitEnumConstantBits(
                    value.Underlying, value.PhysicalValue);
                _il.Emit(OpCodes.Ceq);
                _il.Emit(OpCodes.Brfalse, next);
                _il.Emit(OpCodes.Ldc_I4, value.Ordinal);
                _il.Emit(OpCodes.Br, done);
                _il.MarkLabel(next);
            }
            _il.Emit(OpCodes.Ldc_I4_M1);
            _il.MarkLabel(done);
            return Bcl("System.Int32");
        }
        if (e.Type.IsPresent)
            throw new InvalidDataException("ilemit: typed enum ordinal has no physical declaration map");
        // The typeless local-basic form has contiguous physical values and needs only Conv_I4.
        EmitExpr(e.E);
        _il.Emit(OpCodes.Conv_I4);
        return Bcl("System.Int32");
    }

    Type EmitNativeClrEnumValues(CirEnumValues e)
    {
        var et = NativeType(e.Type);
        if (e.Values.IsPresent)
        {
            _il.Emit(OpCodes.Ldc_I4, e.Values.Value.Length);
            _il.Emit(OpCodes.Newarr, et);
            var index = 0;
            foreach (var value in e.Values.Value)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldc_I4, index++);
                EmitEnumConstantBits(
                    value.Underlying, value.PhysicalValue);
                EmitStelem(Bcl(value.Underlying));
            }
            return et.MakeArrayType();
        }
        _il.Emit(OpCodes.Ldtoken, et);
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Type.FromHandle"));
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Enum.GetValues"));
        _il.Emit(OpCodes.Castclass, et.MakeArrayType());
        return et.MakeArrayType();
    }

    static object EnumConstantValue(string underlying, string text) => underlying switch
    {
        "System.SByte" => sbyte.Parse(text, CultureInfo.InvariantCulture),
        "System.Byte" => byte.Parse(text, CultureInfo.InvariantCulture),
        "System.Int16" => short.Parse(text, CultureInfo.InvariantCulture),
        "System.UInt16" => ushort.Parse(text, CultureInfo.InvariantCulture),
        "System.Int32" => int.Parse(text, CultureInfo.InvariantCulture),
        "System.UInt32" => uint.Parse(text, CultureInfo.InvariantCulture),
        "System.Int64" => long.Parse(text, CultureInfo.InvariantCulture),
        "System.UInt64" => ulong.Parse(text, CultureInfo.InvariantCulture),
        "System.Char" => (char)ushort.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException($"ilemit: illegal CLR enum underlying type '{underlying}'"),
    };

    void EmitEnumConstantBits(string underlying, string text)
    {
        switch (underlying)
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
            case "System.SByte":
            case "System.Byte":
            case "System.Int16":
            case "System.UInt16":
            case "System.Char":
            case "System.Int32":
                _il.Emit(OpCodes.Ldc_I4, int.Parse(text, CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidDataException($"ilemit: illegal CLR enum underlying type '{underlying}'");
        }
    }

    Type EmitNativeClrEnumParse(CirEnumParse e)
    {
        var et = NativeType(e.Type);
        _il.Emit(OpCodes.Ldtoken, et);
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Type.FromHandle"));
        EmitExpr(e.Arg);
        EmitMethod(_il, OpCodes.Call, WellKnown<MethodInfo>("Enum.Parse"));
        _il.Emit(OpCodes.Unbox_Any, et);
        return et;
    }

    Type EmitClrNew(CirNewClr e)
    {
        var type = ClrRef(e.Type);
        var args = e.Args;
        // bir2cir already resolved the constructor and carried its complete scalar memberRef. Link that exact
        // declaration; there is no argument-applicability or arity fallback here.
        var ctor = LinkClrCtor(type, e.MemberRef);
        EmitArgs(args, ParametersOf(ctor));
        EmitConstructor(_il, OpCodes.Newobj, ctor);
        return type;
    }

    // PrimaryFromRef already anchors the declaration onto the exact use-site owner from the scalar reference.
    /// <summary>The constructor a `newClr` or a base delegation names. A lookup, not a choice.</summary>
    ConstructorInfo LinkClrCtor(Type type, MemberRefNode reference)
    {
        if (PrimaryFromRef(reference) is ConstructorInfo referenced) return referenced;
        throw new InvalidOperationException(
            $"ilemit: construction of {type?.FullName} carries no resolved constructor reference. Every external member "
            + "arrives named; a node without one is an earlier-layer drop");
    }

    // The scalar reference supplies the complete declaration identity. This helper only projects that identity into
    // the target metadata universe; it does not construct or rank a candidate set from the call node.
    /// <summary>The method a `clr*` call names. A lookup, not a choice.</summary>
    readonly record struct ClrMethodTarget(MemberRefNode Reference, bool LocalAccessor,
        OptionalField<TypeNode> LocalOwner, TypeNode[] Signature);

    MethodInfo LinkClrMethod(Type type, string name, ClrMethodTarget target, bool instance)
    {
        if (PrimaryFromRef(target.Reference) is MethodInfo referenced) return referenced;
        if (target.LocalAccessor)
        {
            var sig = target.Signature
                ?? throw new InvalidOperationException(
                    $"ilemit: local accessor {type?.FullName}.{name} carries no exact signature");
            return ResolveMethod(ParseOwnerSlot(target.LocalOwner.Value), name, out _, sig, 0);
        }
        throw new InvalidOperationException(
            $"ilemit: clr{(instance ? "Instance" : "Static")} call to {type?.FullName}.{name} carries no resolved "
            + "member reference. Every external member arrives named; a node without one is an earlier-layer drop");
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
            default: throw new NotSupportedException($"ilemit: unknown clrInstance dispatch '{dispatch}' on {mi.DeclaringType}.{mi.Name}; bir2cir must emit call, callvirt, or constrained");
        }
    }

    Type EmitClrCall(CirNativeStatic e, bool deref = true) =>
        EmitClrCall(e.Type, e.Method,
            new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)),
            e.TypeArgs, e.Args, e.Ret, null, null, instance: false, deref: deref);

    Type EmitClrCall(CirNativeInstance e, bool deref = true) =>
        EmitClrCall(e.Type, e.Method,
            new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)),
            e.TypeArgs, e.Args, e.Ret, e.Recv, e.Dispatch, instance: true, deref: deref);

    Type EmitClrCall(TypeNode owner, string name, ClrMethodTarget target, OptionalField<TypeNode[]> typeArguments,
        ICirExpression[] args, OptionalField<TypeNode> ret, ICirExpression receiver, string dispatch, bool instance, bool deref)
    {
        // `ClrRef` (not `ResolveType`) so a method on a constructed generic .NET type (`Collection<int>`) resolves.
        var type = ClrRef(owner);
        // bir2cir already resolved the overload and carried the complete memberRef; ilemit only links that identity.
        var mi = LinkClrMethod(type, name, target, instance);
        // A generic BCL method (`System.Array.Fill<T>(T[],T,int,int)`) resolved as its open DEFINITION must be
        // instantiated with the call's type args (threaded by bir2cir from the @ClrIntrinsic generic Kotlin callee),
        // or the emitted MethodSpec stays open -> "method/type not fully instantiated" at run. Non-generic targets
        // leave IsGenericMethodDefinition false, so this is a no-op there.
        if (mi.IsGenericMethodDefinition
            && typeArguments.HasValue && typeArguments.Value.Length > 0)
            mi = ConstructedMethod(mi, typeArguments.Value.Select(MapType).ToArray());
        // A value-type receiver's instance method needs a managed pointer (e.g. struct Vec2.Mag2()); the `constrained.`
        // dispatch (below) likewise needs the receiver ADDRESS. (A generic `Array<T>.clone()` is already retargeted by
        // bir2cir to the `System.Array` owner, so its `T[]` receiver is statically assignable — no cast needed here.)
        if (instance)
        {
            if (IsValueType(type)) EmitAddr(receiver);
            else EmitExpr(receiver);
        }
        EmitArgs(args, ParametersOf(mi));
        // Dispatch is a bir2cir DECISION carried on the node (call | callvirt | constrained) — computed from the
        // resolved MethodInfo's IsVirtual/IsFinal + the owner value-type-ness + the `super` (issue #14 base-slot) flag.
        // ilemit no longer derives it from reflected `mi.IsVirtual/IsFinal`. A static call is unconditionally `call`.
        if (instance)
        {
            // `dispatch` is a REQUIRED bir2cir decision — a missing one is a producer defect, NOT a silent `callvirt`
            // default (which on a value-type owner is unverifiable CallVirtOnValueType). Fail loud (consume-only doctrine).
            EmitClrDispatch(mi, dispatch, type);
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
            && ret.IsPresent)
        {
            // The `ret` hint is a structured TypeNode. A method-scope Tv return
            // (`IReadOnlyList<T>::get_Item` on the non-generic
            // `_CollectionsKt.firstOrNull<T>`) reflects as the OPEN interface param `!0` (type-scope, position 0) —
            // a standalone `!0` token in a non-generic method body is INVALID metadata (`box !0` -> BadImageFormat at
            // JIT). MapType resolves the structured Tv against `_curMethodParams` to the method's own `!!T`, so the
            // caller boxes/consumes the correct method-scope token.
            Type hinted = TryMapType(ret.Value);
            if (hinted != null) return hinted;
        }
        return methodReturn;
    }

    // A type slot for an IL-opcode context (newarr elem / conv / default).
    Type NativeType(TypeNode type) => MapType(type);

    // MapType (structured-TypeNode resolution) that returns null instead of throwing.
    Type TryMapType(TypeNode type) { try { return MapType(type); } catch { return null; } }

    // ResolveType but returns null instead of throwing (for optional/best-effort overload resolution).
    Type TryResolveType(string name)
    {
        try { return ResolveType(name); } catch (NotSupportedException) { return null; }
    }

    // W1-S3 (#46 / #121) CONSUME-ONLY property GET. bir2cir (ClrMemberResolution) resolved the member against the ref.dll
    // and stamped a `member` discriminator: "accessor" (a real .NET property — carries the
    // resolved accessor memberRef + `dispatch`) or "field" (a public/const field). ilemit no longer reclassifies
    // property vs get_ method vs field, and no longer derives dispatch from the reflected accessor.
    Type EmitClrPropGet(CirPropertyGet e)
    {
        var type = ClrRef(e.Type);
        var isStatic = e.Static;
        if (e.Member == "accessor")
        {
            var getter = LinkClrMethod(type, e.Accessor.Value, new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), instance: !isStatic);
            if (!isStatic) { if (IsValueType(type)) EmitAddr(e.Recv.Value); else EmitExpr(e.Recv.Value); }
            if (isStatic) EmitMethod(_il, OpCodes.Call, getter);
            else EmitClrDispatch(getter, RequireDispatch(e.Dispatch, type, "clrPropGet"), type);
            return ReturnTypeOf(getter);
        }
        // A .NET FIELD surfaced as a Kotlin property (bir2cir resolved member:"field"). A `const` (literal) field has no
        // storage — inline its value (a mechanical raw-constant fetch, not a KIND decision), exactly as C# does.
        // The reference first: a field is a member like any other, and re-finding it by name is the selection
        // this change exists to remove — a derived type's static field can hide a base instance field of the
        // same name, and a name lookup cannot tell which one bir2cir resolved.
        var fld = RequiredRef<FieldInfo>(e.MemberRef.OrDefault(), "an external field access");
        if (fld.IsLiteral) return EmitLiteralValue(fld.GetRawConstantValue(), FieldTypeOf(fld));
        if (!isStatic && !fld.IsStatic) { if (IsValueType(type)) EmitAddr(e.Recv.Value); else EmitExpr(e.Recv.Value); }
        MaybeVolatile(fld, e.Volatile.OrDefault());
        EmitField(_il, fld.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, fld);
        return FieldTypeOf(fld);
    }

    // W1-S3 (#46 / #121) CONSUME-ONLY property SET (twin of EmitClrPropGet).
    Type EmitClrPropSet(CirPropertySet e)
    {
        var type = ClrRef(e.Type);
        var isStatic = e.Static;
        if (e.Member == "accessor")
        {
            var setter = LinkClrMethod(type, e.Accessor.Value, new ClrMethodTarget(e.MemberRef.OrDefault(), e.LocalAccessor.OrDefault(), e.AccessorOwner.IsPresent ? e.AccessorOwner : e.Type, CallSignature(e.CalleeParams, e.Sig)), instance: !isStatic);
            if (!isStatic) { if (IsValueType(type)) EmitAddr(e.Recv.Value); else EmitExpr(e.Recv.Value); }
            EmitArgs2(new[] { e.Value }, ParametersOf(setter));
            if (isStatic) EmitMethod(_il, OpCodes.Call, setter);
            else EmitClrDispatch(setter, RequireDispatch(e.Dispatch, type, "clrPropSet"), type);
            return Bcl("System.Void");
        }
        // A writable .NET FIELD surfaced as a Kotlin (mutable) property -> field store.
        // The reference first: a field is a member like any other, and re-finding it by name is the selection
        // this change exists to remove — a derived type's static field can hide a base instance field of the
        // same name, and a name lookup cannot tell which one bir2cir resolved.
        var fld = RequiredRef<FieldInfo>(e.MemberRef.OrDefault(), "an external field access");
        if (!isStatic && !fld.IsStatic) { if (IsValueType(type)) EmitAddr(e.Recv.Value); else EmitExpr(e.Recv.Value); }
        EmitNullableCoerced(e.Value, FieldTypeOf(fld));
        MaybeVolatile(fld, e.Volatile.OrDefault());
        EmitField(_il, fld.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, fld);
        return Bcl("System.Void");
    }

    static string RequireDispatch(OptionalField<string> dispatch, Type type, string what)
    {
        if (dispatch.HasValue) return dispatch.Value;
        throw new InvalidOperationException($"ilemit: {what} on {type?.FullName} is missing its `dispatch` decision; bir2cir must carry call, callvirt, or constrained");
    }

    // `.NET event +=/-=` -> call the event's add/remove accessor with the handler bound as the event's OWN
    // delegate type (e.g. EventHandler).
    // W1-S3 (#46 / #121 / #113) CONSUME-ONLY event add/remove. bir2cir either bound a local synthesized accessor by
    // its complete signature or resolved the EventInfo off the ref.dll and stamped its memberRef plus `dispatch`.
    // ilemit LINKS that exact accessor (LinkClrMethod — hard-fails a missing/ambiguous slot, so the old unchecked
    // `GetEvent(...).GetAddMethod()` NRE on a missing/value-type/constructed-generic event is gone) and consumes the
    // carried dispatch.
    // bir2cir materializes the handler as the accessor's exact nominal delegate once, before it reaches either add or
    // remove. ilemit neither re-resolves its Invoke nor reconstructs a delegate conversion from the value's shape.
    Type EmitClrEvent(TypeNode owner, bool isStatic, string accessorName, ClrMethodTarget target,
        OptionalField<string> carriedDispatch, OptionalField<ICirExpression> receiver, bool handlerExact, ICirExpression handler, bool add)
    {
        var type = ClrRef(owner);
        var accessor = LinkClrMethod(type, accessorName, target, instance: !isStatic);
        var dispatch = isStatic ? null : RequireDispatch(carriedDispatch, type, add ? "clrEventAdd" : "clrEventRemove");
        if (!isStatic)
        {
            if (IsValueType(type) || dispatch == "constrained") EmitAddr(receiver.Value);
            else EmitExpr(receiver.Value);
        }
        if (!handlerExact)
            throw new InvalidOperationException(
                "ilemit: CLR event handler is not the exact accessor delegate; bir2cir must materialize it");
        EmitExpr(handler);
        if (isStatic) EmitMethod(_il, OpCodes.Call, accessor);
        else EmitClrDispatch(accessor, dispatch, type);
        return Bcl("System.Void");
    }

    // Resolve a newClosure node's ctor + invoke, INSTANTIATING the closure generic when it is a generic definition.
    // A capturing closure over an enclosing type param (`{ seed }` in `generateSequence<T>`) is a GENERIC class;
    // left as its open definition the `newobj Closure`1::.ctor(!0)` operand is OPEN -> a TypeLoadException at run.
    // Close it with the node's explicit `typeArgs`. Shared by the main newClosure emit and delegate-arg binding.
    (ConstructorInfo Ctor, MethodInfo Invoke) ResolveClosure(CirClosure e)
    {
        var ct = _types[SlotName(e.ClosureType)];
        ConstructorInfo ctor = ct.Ctor;
        MethodInfo invoke = ct.Methods[e.Method];
        Type constructed = null;
        if (e.TypeArgs.IsPresent && e.TypeArgs.Value.Length > 0)
            constructed = ConstructedType(ct.TB, e.TypeArgs.Value.Select(MapType).ToArray());
        else if (ct.TB.IsGenericTypeDefinition)
            throw new InvalidOperationException("generic CIR closure has no explicit type arguments");
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
    // delegate + field are already resolved. Declaration markers and event-read dialect nodes never reach this layer;
    // the retained stdlib ClrEvent runtime class uses ordinary class/member emission.
    void EmitClrEventAccessorImpl(CirEventAccessorBody e)
    {
        var kind = e.Kind;
        var fieldName = e.Name;
        var d = MapType(e.DelegateType);
        var isStatic = e.Static;
        // S5 (#113): a missing backing field is a bir2cir/kotc synthesis defect — a legible breadcrumb, not an opaque miss.
        if (_curTi == null || !_curTi.Fields.TryGetValue(fieldName, out var field))
            throw new InvalidOperationException($"ilemit: clrEventAccessorImpl backing field '{fieldName}' is absent on '{_curTi?.TB?.Name}'; bir2cir ClrEventImplBinding must insert `<E>$delegate`");
        if (kind == "raise")
        {
            EmitClrEventRaise(field, d, isStatic,
                RequiredRef<MethodInfo>(e.InvokeRef.OrDefault(), "clrEventAccessorImpl raise"));
            return;
        }
        EmitClrEventCas(e, field, d, isStatic, add: kind == "add");
    }

    // The lock-free add/remove CAS loop over the backing delegate field (C#'s field-like-event accessor shape):
    //   D cur = this.field; do { D cmp = cur; D upd = (D)Delegate.Combine/Remove(cmp, value);
    //       cur = Interlocked.CompareExchange<D>(ref this.field, upd, cmp); } while (cur != cmp);
    // Instance: arg0 = this, arg1 = value. Static: arg0 = value. Reference comparison, not delegate equality.
    void EmitClrEventCas(CirEventAccessorBody node, FieldInfo field, Type d, bool isStatic, bool add)
    {
        // The three members this loop runs through are named by the pass that authored the accessor.
        var combine = RequiredRef<MethodInfo>((add ? node.CombineRef : node.RemoveRef).OrDefault(), "clrEventAccessorImpl");
        var cmpx = ConstructedMethod(RequiredRef<MethodInfo>(node.CompareExchangeRef.OrDefault(), "clrEventAccessorImpl"), d);
        var cur = _il.DeclareLocal(d); var cmp = _il.DeclareLocal(d); var upd = _il.DeclareLocal(d);
        var retry = _il.DefineLabel();
        if (!isStatic) _il.Emit(OpCodes.Ldarg_0);
        EmitField(_il, isStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, field); _il.Emit(OpCodes.Stloc, cur);
        _il.MarkLabel(retry);
        _il.Emit(OpCodes.Ldloc, cur); _il.Emit(OpCodes.Stloc, cmp);
        _il.Emit(OpCodes.Ldloc, cmp); _il.Emit(isStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
        EmitMethod(_il, OpCodes.Call, combine); _il.Emit(OpCodes.Castclass, d); _il.Emit(OpCodes.Stloc, upd);
        if (!isStatic) _il.Emit(OpCodes.Ldarg_0);
        EmitField(_il, isStatic ? OpCodes.Ldsflda : OpCodes.Ldflda, field);
        _il.Emit(OpCodes.Ldloc, upd); _il.Emit(OpCodes.Ldloc, cmp);
        EmitMethod(_il, OpCodes.Call, cmpx); _il.Emit(OpCodes.Stloc, cur);
        _il.Emit(OpCodes.Ldloc, cur); _il.Emit(OpCodes.Ldloc, cmp); _il.Emit(OpCodes.Bne_Un, retry);
    }

    // `raise_<E>(args...)`: snapshot the backing field, and if non-null invoke it with the raise method's own params.
    // Raise parameters start at arg0 for statics, arg1 for instances. Null check = field?.Invoke.
    void EmitClrEventRaise(FieldInfo field, Type d, bool isStatic, MethodInfo invoke)
    {
        var handler = _il.DeclareLocal(d);
        var done = _il.DefineLabel();
        if (!isStatic) _il.Emit(OpCodes.Ldarg_0);
        EmitField(_il, isStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, field); _il.Emit(OpCodes.Stloc, handler);
        _il.Emit(OpCodes.Ldloc, handler); _il.Emit(OpCodes.Brfalse, done);
        _il.Emit(OpCodes.Ldloc, handler);
        var n = ParametersOf(invoke).Length;
        for (int i = 0; i < n; i++) _il.Emit(OpCodes.Ldarg, checked((short)(i + (isStatic ? 0 : 1))));
        EmitMethod(_il, OpCodes.Callvirt, invoke);
        // A raise is a statement even when the selected physical delegate returns a value.
        if (ReturnTypeOf(invoke) != Bcl("System.Void")) _il.Emit(OpCodes.Pop);
        _il.MarkLabel(done);
    }


}
