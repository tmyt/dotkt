// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// CLR interop emission: @Clr native calls, property/event access, ctor picking, BCL-intrinsic handlers.
sealed partial class Emitter
{
    Type EmitNativeClrSafeCastValue(JsonElement e)
    {
        // `x as? T` for value T -> `T?`: isinst boxed-T, then unbox+wrap, else empty Nullable<T>.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
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
        _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
        _il.MarkLabel(done);
        return nt;
    }

    Type EmitNativeClrNullableNull(JsonElement e)
    {
        // `null` typed as Int? -> a Nullable<T> with HasValue=false. NOT ldnull: a value type has no null reference.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, loc);
        return nt;
    }

    Type EmitNativeClrNullableWrap(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
        return nt;
    }

    Type EmitNativeClrNullableHasValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        EmitExpr(e.GetProperty("e"));
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Call, nt.GetProperty("HasValue").GetGetMethod());
        return typeof(bool);
    }

    Type EmitNativeClrNullableValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
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
        _il.Emit(OpCodes.Call, nt.GetProperty("Value").GetGetMethod());
        return elem;
    }

    Type EmitNativeClrTypeOf(JsonElement e)
    {
        var t = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, t);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
        return typeof(Type);
    }

    Type EmitNativeClrGetType(JsonElement e)
    {
        var got = EmitExpr(e.GetProperty("e"));
        if (got != null && NeedsBoxToRef(got)) _il.Emit(OpCodes.Box, got);
        _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType"));
        return typeof(Type);
    }

    Type EmitNativeClrEnumValue(JsonElement e)
    {
        _il.Emit(OpCodes.Ldc_I4, e.GetProperty("ordinal").GetInt32());
        return NativeType(e.GetProperty("type"));
    }

    Type EmitNativeClrEnumOrdinal(JsonElement e)
    {
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Conv_I4);
        return typeof(int);
    }

    Type EmitNativeClrEnumValues(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
        _il.Emit(OpCodes.Call, typeof(Enum).GetMethod("GetValues", new[] { typeof(Type) }));
        _il.Emit(OpCodes.Castclass, et.MakeArrayType());
        return et.MakeArrayType();
    }

    Type EmitNativeClrEnumParse(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
        EmitExpr(e.GetProperty("arg"));
        _il.Emit(OpCodes.Call, typeof(Enum).GetMethod("Parse", new[] { typeof(Type), typeof(string) }));
        _il.Emit(OpCodes.Unbox_Any, et);
        return et;
    }

    Type EmitClrNew(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => { try { return ClrRef(a); } catch { return (Type)null; } }).ToArray();
        var args = e.GetProperty("args");
        // `new List<R>()` where R is the enclosing generic FUNCTION's type parameter: List<R> is a
        // TypeBuilderInstantiation whose .GetConstructor/.GetConstructors throw — resolve the ctor on the open generic
        // definition (its params are non-generic for the cases we hit: no-arg, capacity), emit the args against those
        // params, and re-anchor via TypeBuilder.GetConstructor. (Mirrors GenericMethod for member access.)
        if (IsTbInstantiation(type))
        {
            var openDef = type.GetGenericTypeDefinition();
            // GetConstructor(argTypes) throws ArgumentException when argTypes contains a TypeBuilder (a generic collection
            // constructed with an EMITTED element type, e.g. `new HashSet<EmittedType>()`) -> null it and fall through to
            // PickOpenCtor (the exact mirror of the EmitClrCall ArgumentException catch).
            ConstructorInfo directCtor = null;
            if (argTypes.All(t => t != null)) try { directCtor = openDef.GetConstructor(argTypes); } catch (ArgumentException) { }
            var openCtor = directCtor
                // ABI substitution (@Clr concrete collections, ArrayList->System.List): a Kotlin arg type doesn't EXACTLY
                // match the BCL ctor param (Collection->IReadOnlyCollection vs the List(IEnumerable<T>) ctor). Fall back to
                // arity + structural assignability (IReadOnlyCollection IS IEnumerable).
                ?? PickOpenCtor(openDef, argTypes, args.GetArrayLength())
                ?? throw new NotSupportedException($"no matching ctor on the open def of {type.FullName} with {args.GetArrayLength()} arg(s)");
            EmitArgs(args, openCtor.GetParameters());
            _il.Emit(OpCodes.Newobj, TypeBuilder.GetConstructor(type, openCtor));
            return type;
        }
        // Exact match first; else an assignability pick (the @Clr concrete-collection ABI: a `Collection<T>` arg lowered
        // to IReadOnlyCollection<T> matches List's `IEnumerable<T>` ctor, disambiguating it from the `int` capacity ctor
        // — an exact GetConstructor misses because the param type differs); else arity-based selection (matters when a
        // lambda arg's type was erased to `object` by the façade — the real delegate param is recovered here).
        // GetConstructor throws ArgumentException when argTypes contains an EMITTED TypeBuilder ("Type must be a type
        // provided by the runtime") — precise ctor argTypes can now resolve to emitted stdlib types; null it and let the
        // assignability/arity fallbacks (which tolerate emitted types) resolve. Mirrors the Tb-instantiation catch above.
        ConstructorInfo exact = null;
        if (argTypes.All(t => t != null))
            try { exact = type.GetConstructor(argTypes); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { }
        var ci = exact
                 ?? PickCtorByAssignable(type, argTypes, args.GetArrayLength())
                 ?? PickClrCtor(type, args);
        if (ci == null) throw new NotSupportedException($"no matching constructor for {type.FullName} with {args.GetArrayLength()} arg(s)");
        EmitArgs(args, ci.GetParameters());
        _il.Emit(OpCodes.Newobj, ci);
        return type;
    }

    // A `new` node's `argTypes` (kotc's resolved ctor param types, pure Kotlin FQNs) -> the EXACT ctor on an
    // external/reflected type, when the field is present, same-arity, and every entry resolves. Null tells the caller to
    // fall back to arity-based selection: a mismatched count (prepended enclosing/capture args) or an unresolvable entry
    // must not force a wrong pick, and `GetConstructor` itself returns null when nothing matches the signature.
    ConstructorInfo NewCtorBySig(Type type, JsonElement e, int argc)
    {
        if (!e.TryGetProperty("argTypes", out var atEl) || atEl.ValueKind != JsonValueKind.Array) return null;
        if (atEl.GetArrayLength() != argc) return null;
        Type[] argTypes;
        try { argTypes = atEl.EnumerateArray().Select(a => ClrRef(a)).ToArray(); } catch { return null; }
        if (argTypes.Any(t => t == null)) return null;
        try { return type.GetConstructor(argTypes); } catch { return null; }
    }

    // When exact GetConstructor fails, pick the UNIQUE same-arity ctor on a (constructed/reflected) type whose params
    // ACCEPT the KNOWN arg types by assignability — the @Clr collection ABI (a `Collection<T>` arg lowered to
    // IReadOnlyCollection<T> is assignable to List's `IEnumerable<T>` ctor param, but NOT to its `int` capacity ctor).
    // Null when arg types are unknown or the assignable match is not unique — the caller then falls back to arity scoring.
    ConstructorInfo PickCtorByAssignable(Type type, Type[] argTypes, int n)
    {
        if (argTypes.Length != n || argTypes.Any(t => t == null)) return null;
        ConstructorInfo hit = null;
        try
        {
            foreach (var c in type.GetConstructors().Where(c => c.GetParameters().Length == n))
            {
                var ps = c.GetParameters();
                if (!Enumerable.Range(0, n).All(i => ParamAccepts(ps[i].ParameterType, argTypes[i]))) continue;
                if (hit != null) return null;   // ambiguous
                hit = c;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException) { return null; }   // emitted/Tb types
        return hit;
    }

    // Pick a ctor on an open generic def by arity + STRUCTURAL assignability (a Kotlin arg whose @Clr type derives from
    // the BCL ctor's param generic def, e.g. IReadOnlyCollection<T> for a List(IEnumerable<T>) ctor). For the @Clr
    // concrete-collection bindings where the Kotlin and BCL signatures aren't identical (Codex's ABI caveat).
    ConstructorInfo PickOpenCtor(Type openDef, Type[] argTypes, int n)
    {
        var cands = openDef.GetConstructors().Where(c => c.GetParameters().Length == n).ToList();
        if (cands.Count <= 1) return cands.FirstOrDefault();
        foreach (var c in cands)
        {
            var ps = c.GetParameters();
            if (Enumerable.Range(0, n).All(i => ParamAccepts(ps[i].ParameterType, argTypes[i]))) return c;
        }
        return cands.FirstOrDefault();
    }

    // Whether a ctor/method param of (possibly open-generic) type `param` accepts an arg of type `arg`: exact assignable,
    // same generic def, or `arg`'s generic def derives from `param`'s generic def (IReadOnlyCollection<> : IEnumerable<>).
    static bool ParamAccepts(Type param, Type arg)
    {
        if (arg == null) return true;                 // unknown arg type -> don't reject
        try { if (param.IsAssignableFrom(arg)) return true; } catch { }
        if (param.IsGenericType && arg.IsGenericType)
        {
            var pdef = param.GetGenericTypeDefinition();
            var adef = arg.GetGenericTypeDefinition();
            if (adef == pdef) return true;
            try { if (adef.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == pdef)) return true; } catch { }
        }
        return false;
    }

    /** Pick a ctor by arity when exact type match fails; among equal-arity ctors prefer the one whose delegate-typed
     *  params match the arity of the lambda (newDelegate/newClosure) args — disambiguates ThreadStart (`()->`) from
     *  ParameterizedThreadStart (`(object)->`). */
    ConstructorInfo PickClrCtor(Type type, JsonElement args)
    {
        int n = args.GetArrayLength();
        var cands = type.GetConstructors().Where(c => c.GetParameters().Length == n).ToList();
        if (cands.Count == 0) return n == 0 ? type.GetConstructor(Type.EmptyTypes) : null;
        if (cands.Count == 1) return cands[0];
        return cands.OrderByDescending(c =>
        {
            var ps = c.GetParameters(); int score = 0, i = 0;
            foreach (var a in args.EnumerateArray())
            {
                var p = ps[i++].ParameterType;
                if (a.TryGetProperty("k", out var k) && (k.GetString() == "newDelegate" || k.GetString() == "newClosure")
                    && typeof(System.Delegate).IsAssignableFrom(p) && a.TryGetProperty("funcType", out var ft))
                {
                    var invoke = p.GetMethod("Invoke");
                    if (invoke != null && invoke.GetParameters().Length == FuncArityOf(ft)) score += 2;
                }
            }
            return score;
        }).First();
    }

    // Delegate arity of a `funcType` slot — a structured `{t:"fn",params:[...]}` node (funcType is ALWAYS an `fn`
    // node now, #37 #49; the `func:`/`sfunc:` string form is retired). Matches how FuncType builds the CLR delegate
    // (from `fn.Params`), so the score compares against the same arity the emitted delegate's Invoke carries.
    static int FuncArityOf(JsonElement ft) =>
        ft.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(ft) is DotKt.Bir.TypeNode.Fn fn ? fn.Params.Length
        : 0;

    // Does a candidate overload's parameter accept the resolved arg type? A null (un-resolvable) arg or an open generic
    // param binds anything. `object` accepts every ref (and boxed value). Two reference types: accept only if PROVABLY
    // assignable — an emitted TypeBuilder arg makes IsAssignableFrom throw OR return false; either way it is not provably
    // assignable to a concrete BCL class (only to `object`), so we reject, steering a `dotkt$CharSequence` to
    // `Append(object)` (ToStrings) rather than `Append(String)` (reinterprets the object -> corruption). A value-type is
    // matched by identity (no implicit numeric widening in the fallback pick).
    static bool ParamAcceptsArg(Type param, Type arg)
    {
        if (arg == null || param.IsGenericParameter || param.ContainsGenericParameters) return true;
        if (param == typeof(object)) return true;
        if (!param.IsValueType && !arg.IsValueType)
        {
            try { return param.IsAssignableFrom(arg); } catch { return false; }
        }
        return param == arg;
    }

    Type EmitClrCall(JsonElement e, bool instance, bool deref = true)
    {
        // `ClrRef` (not `ResolveType`) so a method on a constructed generic .NET type (`Collection<int>`) resolves.
        var type = ClrRef(e.GetProperty("type"));
        var name = e.GetProperty("method").GetString();
        // `argTypes` entries are structured TypeNodes (post type-flip) OR legacy strings — keep the JsonElements and
        // resolve via ClrRef(JsonElement) (dispatches both). Reading them as `.GetString()` crashed on the structured
        // form (InvalidOperationException: element is Object, not String).
        var argSpecs = e.GetProperty("argTypes").EnumerateArray().ToList();
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        MethodInfo mi = null;
        // Exact overload resolution when every arg type resolves (ClrRef handles array:/clrg:/nullable:/func: too,
        // so e.g. `array:object` -> object[] selects String.Format(string, params object[]) over (string, object)).
        var resolved = argSpecs.Select(a => { try { return ClrRef(a); } catch { return (Type)null; } }).ToArray();
        try
        {
            if (resolved.All(x => x != null))
                try { mi = type.GetMethod(name, flags, null, resolved, null); }
                // Overloads that collapse to the SAME CLR signature (e.g. IntArray.sum & Array<out Int>.sum -> sum(int[])
                // under the primitive/boxed dual-representation) make GetMethod ambiguous -> pick the EXACT-param match
                // (also prefers the concrete overload over a generic `T[]` one, which doesn't param-equal `int[]`).
                catch (AmbiguousMatchException) {
                    mi = type.GetMethods(flags).FirstOrDefault(m => m.Name == name
                        && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(resolved));
                }
                // A TypeBuilder in `resolved` (a generic collection of an emitted element type, e.g.
                // ICollection<EmittedType>.Add(EmittedType)) makes GetMethod throw ArgumentException ("Type must be a
                // type provided by the runtime"). Null it out so the name+arity fallback re-anchors on the constructed type.
                catch (ArgumentException) { mi = null; }
            // Fall back to name + arity — e.g. a generic-parameter arg type (`Add(T)` on `Collection<int>`) that
            // doesn't name a plain .NET type; on the constructed type GetMethods returns the substituted overload.
            // When SEVERAL overloads share the arity (StringBuilder.Append has ~19 one-arg overloads) an arbitrary
            // FirstOrDefault can pick a param the arg is NOT assignable to — e.g. a non-String `dotkt$CharSequence`
            // into `Append(String)` reinterprets the object as a string -> memory corruption. So, when the arg types
            // resolved, keep only overloads whose every param ACCEPTS the resolved arg (is assignable-from it), then
            // prefer the MOST-SPECIFIC (fewest `object` params): a real String still binds `Append(String)`, while a
            // synthetic/emitted ref (a `dotkt$CharSequence` adapter) binds `Append(object)` which ToStrings it.
            if (mi == null)
            {
                var cand = type.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == argSpecs.Count).ToList();
                if (cand.Count > 1)
                {
                    var ok = cand.Where(m => m.GetParameters().Select(p => p.ParameterType).Zip(resolved, ParamAcceptsArg).All(b => b)).ToList();
                    if (ok.Count > 0) cand = ok.OrderBy(m => m.GetParameters().Count(p => p.ParameterType == typeof(object))).ToList();
                }
                mi = cand.FirstOrDefault();
            }
        }
        catch (NotSupportedException) { }
        // A constructed generic type whose arg is an emitted generic parameter (TypeBuilderInstantiation) refuses
        // reflection — re-anchor the open definition's method onto the constructed type via TypeBuilder.GetMethod.
        if (mi == null && type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            try {
            var open = type.GetGenericTypeDefinition();
            var typeArgs = type.GetGenericArguments();
            var om = open.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argSpecs.Count);
            if (om != null) mi = TypeBuilder.GetMethod(type, om);
            // An inherited INTERFACE member (`IList<T>.Add` lives on the base `ICollection<T>`): interface GetMethods
            // doesn't include base-interface methods, so walk the (transitively-flattened) base interfaces, find the
            // declaring one, construct it with this type's args (shared type parameters) and re-anchor. See item 3.
            else mi = ResolveInheritedIfaceMethod(open, typeArgs, name, argSpecs.Count, flags);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException) { mi = null; }
        }
        // Last resort: a UNIQUELY-named method (covers e.g. a `params`/vararg method called with one array arg whose
        // static argType — `object` — didn't match the `T[]` param, so neither exact nor arity resolution hit).
        if (mi == null)
        {
            // A generic TypeBuilder instantiation throws on GetMethods — enumerate the open def + re-anchor via GetMethod.
            // Every reflection step here can throw on a TypeBuilderInstantiation (GetMethods/GetGenericTypeDefinition ->
            // NotSupportedException "Derived classes must provide an implementation"); any such failure must leave mi == null
            // so we fall through to dynamic dispatch (below) rather than aborting the emit.
            try {
                MethodInfo[] all; bool reanchor = false;
                try { all = type.GetMethods(flags); }
                catch (NotSupportedException) { all = type.GetGenericTypeDefinition().GetMethods(flags); reanchor = true; }
                var named = all.Where(m => m.Name == name).ToList();
                if (named.Count == 1) mi = reanchor ? TypeBuilder.GetMethod(type, named[0]) : named[0];
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException) { mi = null; }
        }
        // `Array<T>.Clone()` (@ClrIntrinsic("Clone")): a generic array receiver erases to `object`, whose Clone is protected, so
        // resolution fails — but the runtime value is always a System.Array. Resolve Array.Clone and (below) cast the
        // receiver to System.Array before the callvirt. Returns object; the stdlib `as Array<T>` re-types it.
        bool arrayCloneFallback = false;
        if (mi == null && name == "Clone" && argSpecs.Count == 0)
        {
            mi = typeof(System.Array).GetMethod("Clone", BindingFlags.Public | BindingFlags.Instance);
            arrayCloneFallback = mi != null;
        }
        // An unbound Kotlin member that became a clrInstance because its receiver type is @Clr-substituted (e.g.
        // MutableCollection.removeAll/addAll on ICollection -- no BCL equivalent by that name) -> dynamic dispatch.
        // GATED to an INTERFACE owner (the clrInstance analog of the callInstance path's `OwnerHasClrInterface` gate):
        // the runtime value implements that BCL interface under a different concrete type, so `recv.GetType().GetMethod`
        // resolves the real slot. A NON-interface owner (a concrete BCL class) that missed static resolution is a
        // bir2cir Rule-4 ROUTING MISS -- reflection would silently return null -> opaque runtime NRE -- so it must throw
        // at EMIT instead of falling to dynamic dispatch. (bir2cir now refuses lowercase members on non-interface CLR
        // owners upstream; this is the defense-in-depth twin of that compile-time refusal.)
        if (mi == null && instance && type.IsInterface && e.TryGetProperty("recv", out _)) return EmitDynamicCall(e);
        if (mi == null) throw new NotSupportedException($"clrInstance method not resolved: {type}.{name}/{argSpecs.Count} (no BCL match; dynamic-dispatch fallback is gated to interface owners -- a routing MISS on a concrete BCL owner)");
        // A generic BCL method (`System.Array.Fill<T>(T[],T,int,int)`) resolved as its open DEFINITION must be
        // instantiated with the call's type args (threaded by bir2cir from the @ClrIntrinsic generic Kotlin callee),
        // or the emitted MethodSpec stays open -> "method/type not fully instantiated" at run. Non-generic targets
        // (Array.Clone) leave IsGenericMethodDefinition false, so this is a no-op there.
        if (mi.IsGenericMethodDefinition
            && e.TryGetProperty("typeArgs", out var clrTa) && clrTa.ValueKind == JsonValueKind.Array && clrTa.GetArrayLength() > 0)
            mi = mi.MakeGenericMethod(clrTa.EnumerateArray().Select(a => MapType(a)).ToArray());
        // A value-type receiver's instance method needs a managed pointer (e.g. struct Vec2.Mag2()).
        if (instance)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv"));
            else { EmitExpr(e.GetProperty("recv")); if (arrayCloneFallback && !typeof(System.Array).IsAssignableFrom(type)) _il.Emit(OpCodes.Castclass, typeof(System.Array)); }
        }
        EmitArgs(e.GetProperty("args"), mi.GetParameters());
        EmitInstanceCall(mi, instance, type);
        // A `ref T`-returning method used as a value -> dereference the managed pointer (value copy). The live-ref
        // form (`byrefOf(m())`, behind `var x by byref(m())`) passes deref:false to keep the pointer.
        if (mi.ReturnType.IsByRef)
        {
            if (!deref) return mi.ReturnType;
            var elem = mi.ReturnType.GetElementType();
            _il.Emit(OpCodes.Ldobj, elem);
            return elem;
        }
        // TypeBuilder.GetMethod re-anchors the call onto the instantiation but leaves the method's RETURN type open
        // (e.g. `Task<Vec>::GetAwaiter()` reports `TaskAwaiter`1<!0>`, not `<Vec>`). The IL token is correct, but the
        // STATIC type we hand back must be the substituted one or the caller mis-types its temp/local — so trust the
        // BIR `ret` hint, which already carries the substituted type. (Only when the reflected return is still open.)
        if ((mi.ReturnType.IsGenericParameter || mi.ReturnType.ContainsGenericParameters)
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
        return mi.ReturnType;
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
    // legacy string token via the shorthand/prefix path below.
    Type NativeType(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? MapType(DotKt.Bir.TypeNode.Read(e)) : NativeType(e.GetString());

    Type NativeType(string spec)
    {
        if (spec == null) return typeof(object);
        if (spec.StartsWith("clr:", StringComparison.Ordinal) ||
            spec.StartsWith("clrg:", StringComparison.Ordinal) ||
            spec.StartsWith("array:", StringComparison.Ordinal) ||
            spec.StartsWith("func:", StringComparison.Ordinal) ||
            spec.StartsWith("nullable:", StringComparison.Ordinal) ||
            spec.StartsWith("byref:", StringComparison.Ordinal) ||
            spec.StartsWith("gp:", StringComparison.Ordinal) ||
            spec.StartsWith("@", StringComparison.Ordinal))
            return MapType(spec);
        return spec switch
        {
            "void" or "int" or "long" or "double" or "float" or "bool" or "char" or "string" or
            "uint" or "ulong" or "byte" or "ushort" or "short" or "sbyte" or "object" => MapType(spec),
            _ => ClrRef(ClrOwnerSpec(spec)),
        };
    }

    static string ClrOwnerSpec(string owner) =>
        owner.StartsWith("clr:", StringComparison.Ordinal) || owner.StartsWith("clrg:", StringComparison.Ordinal)
            ? owner
            : "clr:" + owner;

    static string NativeOwnerSpec(JsonElement node, JsonElement member) =>
        node.TryGetProperty("ownerType", out var ownerType) && ownerType.ValueKind != JsonValueKind.Null
            ? SlotName(ownerType)
            : ClrOwnerSpec(SlotName(member.GetProperty("owner")));

    // ClrRef (generic-aware type resolution) that returns null instead of throwing.
    Type TryResolveClr(string spec) { try { return ClrRef(spec); } catch { return null; } }

    // MapType (structured-TypeNode resolution) that returns null instead of throwing.
    Type TryMapType(JsonElement e) { try { return MapType(e); } catch { return null; } }

    // ResolveType but returns null instead of throwing (for optional/best-effort overload resolution).
    static Type TryResolveType(string name)
    {
        try { return ResolveType(name); } catch (NotSupportedException) { return null; }
    }

    Type EmitClrPropGet(JsonElement e)
    {
        var typeName = SlotName(e.GetProperty("type"));
        var propName = e.GetProperty("name").GetString();
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        var getter = PropAccessor(type, propName, getter: true);
        if (getter == null)
        {
            // Not a .NET property. A DotKt custom-accessor property is a plain `get_<name>` METHOD (no PropertyDef) ->
            // call it. (A backing-field property is a public FIELD -> field access below.)
            MethodInfo gm;
            try
            {
                gm = type.GetMethod("get_" + propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }
            catch (NotSupportedException)
            {
                // A constructed generic over a TypeBuilder (TypeBuilderInstantiation) can't resolve members directly:
                // resolve the getter on the open generic def, then re-anchor it to the constructed type via GetMethod.
                gm = null;
                if (type.IsConstructedGenericType)
                {
                    var openGm = type.GetGenericTypeDefinition().GetMethod("get_" + propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (openGm != null) gm = TypeBuilder.GetMethod(type, openGm);
                }
            }
            if (gm != null)
            {
                if (!isStatic && !gm.IsStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
                EmitInstanceCall(gm, !isStatic && !gm.IsStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
                return gm.ReturnType;
            }
            // A .NET FIELD surfaced as a Kotlin property (facadegen records static/const fields, public instance fields,
            // and Kotlin backing-field properties). Emit a field access instead of a getter call.
            FieldInfo fld;
            try
            {
                fld = type.GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            }
            catch (NotSupportedException)
            {
                // Backing field of a constructed generic over a TypeBuilder: resolve on the open def + re-anchor.
                fld = null;
                if (type.IsConstructedGenericType)
                {
                    var openFld = type.GetGenericTypeDefinition().GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                    if (openFld != null) fld = TypeBuilder.GetField(type, openFld);
                }
            }
            if (fld == null)
                throw new InvalidOperationException($"ilemit: no readable property OR field '{propName}' on .NET type '{type}' (spec '{typeName}'). Available properties: [{PropList(type)}]");
            // A `const` (literal) field has no storage — `ldsfld` is invalid (and a memberref to it fails). Inline its
            // value, exactly as C# does. Covers .NET consts surfaced by facadegen as `sprop` (e.g. WinRT constants).
            if (fld.IsLiteral) return EmitLiteralValue(fld.GetRawConstantValue(), fld.FieldType);
            if (!isStatic && !fld.IsStatic)
            {
                if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv"));
            }
            _il.Emit(fld.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, fld);
            return fld.FieldType;
        }
        // A property getter on a VALUE type (e.g. KeyValuePair.Key/.Value) needs the receiver by managed pointer.
        if (!isStatic)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv"));
        }
        EmitInstanceCall(getter, !isStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
        return getter.ReturnType;
    }

    Type EmitClrPropSet(JsonElement e)
    {
        var typeName = SlotName(e.GetProperty("type"));
        var propName = e.GetProperty("name").GetString();
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        var setter = PropAccessor(type, propName, getter: false);
        if (setter == null)
        {
            // A DotKt custom-accessor property's `set_<name>` METHOD (no PropertyDef) -> call it.
            var sm = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .FirstOrDefault(mm => mm.Name == "set_" + propName && mm.GetParameters().Length == 1);
            if (sm != null)
            {
                // A value-type receiver's setter takes `this` by managed pointer -> load its ADDRESS so the mutation
                // lands on the real struct (an addressable lvalue), not a spilled copy. Mirrors the getter path.
                if (!isStatic && !sm.IsStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
                EmitArgs2(new[] { e.GetProperty("value") }, sm.GetParameters());
                EmitInstanceCall(sm, !isStatic && !sm.IsStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
                return typeof(void);
            }
            // A writable .NET FIELD surfaced as a Kotlin (mutable) property -> field store.
            var fld = type.GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"ilemit: no writable property OR field '{propName}' on .NET type '{type}' (spec '{typeName}'). Available properties: [{PropList(type)}]");
            // `stfld` on a value-type receiver needs the struct's ADDRESS (managed pointer), not a copy.
            if (!isStatic && !fld.IsStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
            EmitNullableCoerced(e.GetProperty("value"), fld.FieldType);
            _il.Emit(fld.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, fld);
            return typeof(void);
        }
        // A property setter on a VALUE type takes `this` by managed pointer -> load the receiver ADDRESS.
        if (!isStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        EmitArgs2(new[] { e.GetProperty("value") }, setter.GetParameters());
        EmitInstanceCall(setter, !isStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
        return typeof(void);
    }

    // `.NET event +=/-=` -> call the event's add/remove accessor with the handler bound as the event's OWN
    // delegate type (e.g. EventHandler), not the Func/Action the lambda would otherwise produce. The lifted
    // method's signature matches the delegate's Invoke (the FIR injector typed the handler from the event's
    // handler signature), so `ldftn`+`newobj <EventDelegate>(object, IntPtr)` is verifiable — exactly what
    // `button.Click += (s,e)=>{}` lowers to in C#.
    Type EmitClrEvent(JsonElement e, bool add)
    {
        var type = ClrRef(e.GetProperty("type"));
        var ev = type.GetEvent(e.GetProperty("event").GetString());
        var accessor = add ? ev.GetAddMethod() : ev.GetRemoveMethod();
        var delType = accessor.GetParameters()[0].ParameterType;   // == ev.EventHandlerType
        bool isStatic = e.GetProperty("static").GetBoolean();
        if (!isStatic) EmitExpr(e.GetProperty("recv"));
        EmitHandlerAsDelegate(e.GetProperty("handler"), delType);
        _il.Emit(isStatic ? OpCodes.Call : OpCodes.Callvirt, accessor);
        return typeof(void);
    }

    // Resolve a newClosure node's ctor + invoke, INSTANTIATING the closure generic when it is a generic definition.
    // A capturing closure over an enclosing type param (`{ seed }` in `generateSequence<T>`) is a GENERIC class;
    // left as its open definition the `newobj Closure`1::.ctor(!0)` operand is OPEN -> a TypeLoadException at run.
    // Close it with the node's explicit `typeArgs`, else (C13a: kotc/bir2cir omitted them for the non-`this`-capturing
    // form) with the enclosing params matched by NAME (the same resolution `MapType("gp:<name>")` uses). Shared by the
    // main newClosure emit and the delegate-arg binding path so neither can diverge.
    (ConstructorInfo Ctor, MethodInfo Invoke) ResolveClosure(JsonElement e)
    {
        var ct = _types[SlotName(e.GetProperty("closureType"))];
        ConstructorInfo ctor = ct.Ctor;
        MethodInfo invoke = ct.Methods[e.GetProperty("method").GetString()];
        Type constructed = null;
        if (e.TryGetProperty("typeArgs", out var taProp) && taProp.GetArrayLength() > 0)
            constructed = ct.TB.MakeGenericType(taProp.EnumerateArray().Select(a => MapType(a)).ToArray());
        else if (ct.TB.IsGenericTypeDefinition)
            constructed = ct.TB.MakeGenericType(ct.TB.GetGenericArguments().Select(gp => MapType("gp:" + gp.Name)).ToArray());
        if (constructed != null)
        {
            ctor = TypeBuilder.GetConstructor(constructed, ct.Ctor);
            invoke = TypeBuilder.GetMethod(constructed, invoke);
        }
        return (ctor, invoke);
    }

    // Bind a lambda handler (newDelegate = non-capturing, newClosure = capturing) into a SPECIFIC delegate type.
    // Mirrors the newDelegate/newClosure cases but uses `want` (the event's delegate type) for the ctor.
    void EmitHandlerAsDelegate(JsonElement h, Type want)
    {
        var ctor = DelegateCtor(want);
        // The delegate's Invoke return type: when it is a real (non-void) type while the lambda's NATURAL delegate is
        // void-returning (a Unit body maps to void), binding the void method-pointer into the ctor is not verifiable
        // -> self-build the natural void delegate and wrap it in a Unit-return adapter. InvokeRetOf (not want.GetMethod/
        // InvokeOf) so a TypeBuilder-arg `want` (`Func<Res,Unit>`) yields the CLOSED return type (`kotlin.Unit`) — a
        // by-name lookup throws and InvokeOf's ReturnType comes back unsubstituted.
        var invokeRet = InvokeRetOf(want);
        var k = h.GetProperty("k").GetString();
        if (invokeRet != typeof(void) && (k == "newDelegate" || k == "newClosure")
            && FuncRetType(h.GetProperty("funcType")) == typeof(void))
        {
            var ft = EmitExpr(h);                             // the lambda's natural void delegate, on the stack
            _il.Emit(OpCodes.Ldftn, UnitWrapAdapter(ft, invokeRet, FuncArgTypes(h.GetProperty("funcType")).ToArray()));
            _il.Emit(OpCodes.Newobj, ctor);
            return;
        }
        switch (k)
        {
            case "newDelegate":
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, FindStatic(h.GetProperty("method").GetString()));
                _il.Emit(OpCodes.Newobj, ctor);
                break;
            case "newClosure":
                var (cctor, cinvoke) = ResolveClosure(h);
                foreach (var c in h.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, cctor);
                _il.Emit(OpCodes.Ldftn, cinvoke);
                _il.Emit(OpCodes.Newobj, ctor);
                break;
            default:
                // A stored handler value (a Func/Action local/field). Re-wrap it into the event's delegate
                // type via its Invoke — `new EventDelegate(value.Invoke)`. Two wrappers around the SAME stored
                // value share target+method, so Delegate equality holds and `-=` removes the right handler.
                var src = EmitExpr(h);                       // stack: the stored delegate value
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldvirtftn, src.GetMethod("Invoke"));
                _il.Emit(OpCodes.Newobj, ctor);
                break;
        }
    }

}
