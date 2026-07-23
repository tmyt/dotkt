// AUTO-SPLIT concern: emitted-base default-interface-method (DIM) methodimpl wiring — part of the `Emitter` partial
// class (see Program.cs for the overview). When an EMITTED interface `I : J` provides a DEFAULT (bodied) override of a
// method declared/inherited from an emitted base interface `J`/`K`, the CLR needs an explicit methodimpl on I linking
// I's DIM to EACH inherited base slot — a bodied `newslot virtual` interface method self-implements only its OWN slot,
// and a methodimpl on the topmost slot does NOT transitively cover the intermediate one. Without these rows every class
// that implements I fails to load ("Method '<m>' ... does not have an implementation"). The sibling REFERENCED (.NET)
// base path lives in Emitter.Assembly.cs's interface loop; this file handles the same-assembly (emitted) bases.
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;

sealed partial class Emitter
{
    // For interface `ti` and one of its DIRECT emitted base interfaces `firstBase`, emit a private-final methodimpl
    // bridge for every base-declared method that `ti` DEFAULTS (name in `bodied`), transitively up the emitted base
    // chain (so a DIM two levels up — `ContinuationInterceptor.get` over both `Element::get` and `CoroutineContext::get`
    // — wires BOTH slots). `_curTypeParams` is already `EffectiveTps(ti)` at the call site.
    void EmitEmittedBaseDimImpls(TypeInfo ti, DotKt.Bir.TypeNode.Fqn firstBase, HashSet<string> bodied, HashSet<string> seen)
    {
        var work = new Queue<DotKt.Bir.TypeNode.Fqn>();
        work.Enqueue(firstBase);
        var visited = new HashSet<string>();
        while (work.Count > 0)
        {
            var baseFqn = work.Dequeue();
            if (!visited.Add(baseFqn.Name)) continue;
            var (dopen, constructed) = ParseOwnerT(baseFqn);
            if (!_types.TryGetValue(dopen, out var baseTi) || baseTi.Def.ValueKind != JsonValueKind.Object) continue;
            // The base's instantiation args at THIS implementer (`ClosedRange<T>` referenced from `ClosedFloatingPointRange
            // <T>` -> [Tv{type,0}]); re-anchors the base method's own type-var slots to `ti`'s params.
            var specArgs = baseFqn.Args ?? System.Array.Empty<DotKt.Bir.TypeNode>();
            // Transitively wire the base's OWN emitted bases too (args substituted through this hop). LIMITATION: only
            // EMITTED transitive bases are enqueued — an EXTERNAL (.NET) interface reachable ONLY through an emitted base
            // is not wired here (the sibling external path in Emitter.Assembly.cs handles only ti's DIRECT external
            // bases). No current stdlib chain mixes the two (they are all-emitted or direct-external); a future one would
            // fail LOUD at an implementer's load, not silently.
            if (baseTi.Def.TryGetProperty("interfaces", out var baseIfs))
                foreach (var bi in baseIfs.EnumerateArray())
                    if (ReadFqn(bi) is DotKt.Bir.TypeNode.Fqn bi0 && SubstTv(bi0, specArgs) is DotKt.Bir.TypeNode.Fqn biF && _types.ContainsKey(biF.Name))
                        work.Enqueue(biF);
            if (!baseTi.Def.TryGetProperty("methods", out var baseMs)) continue;
            foreach (var bmDef in baseMs.EnumerateArray())
            {
                if (!bmDef.TryGetProperty("name", out var bmn) || !bmDef.TryGetProperty("params", out _)) continue;
                var name = bmn.GetString();
                if (!bodied.Contains(name)) continue;   // `ti` does not DEFAULT this base method -> nothing to re-wire
                // The base method's params/ret with each Tv{type,i} re-anchored to `ti`'s args -> the overload key `ti`'s
                // own DIM is registered under (a method-scope tv collapses to `gp:T` on both sides, so they agree).
                var subSig = name + "(" + string.Join(",", bmDef.GetProperty("params").EnumerateArray()
                    .Select(p => SigCanon(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs)))) + ")";
                if (!ti.MethodsBySig.TryGetValue(subSig, out var dim) || dim.Attributes.HasFlag(MethodAttributes.Abstract)) continue;
                if (!seen.Add(dopen + "::" + subSig)) continue;   // diamond de-dup (per ECMA: no duplicate methodimpl rows)
                var baseSlot = baseTi.MethodsBySig.TryGetValue(SigKey(name, bmDef), out var bs) ? bs
                             : (baseTi.Methods.TryGetValue(name, out var bs2) ? bs2 : null);
                if (baseSlot == null) continue;

                // Signature (typeParams, constraints, params, RETURN) is sourced from the BASE decl — never the DIM — so a
                // COVARIANT override (a narrower return) yields a decl-exact bridge that upcasts on the stack. The
                // constraints are mirrored from the base def; the CLR requires methodimpl constraint EQUIVALENCE after
                // substituting the interface instantiation. LIMITATION: the constraint types are applied UNSUBSTITUTED,
                // so a method-type-param bound that itself names a TYPE-scope var (`fun <E : T> m()`) would mis-resolve —
                // no such DIM'd generic method exists in the stdlib (the coroutine `get<E : Element>` bound is concrete);
                // a future one fails LOUD at load, not silently.
                var genTps = bmDef.TryGetProperty("typeParams", out var mtp) && mtp.GetArrayLength() > 0 ? (JsonElement?)mtp : null;
                MethodBuilder bridge; MethodInfo dimCall;
                Type ifaceRet; Type[] paramTypes;
                if (genTps != null)
                {
                    // Generic arm: builder + generic params must exist before MapType (they anchor a method-scope tv). A
                    // resolve failure gives the already-defined bridge a throwing body + skips the methodimpl (a bodyless
                    // orphan would crash the bake).
                    bridge = ti.TB.DefineMethod("dotkt$dimimpl$" + name + "$" + (_covarBridge++),
                        MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig);
                    var genNames = TpNames(genTps.Value);
                    var gps = bridge.DefineGenericParameters(genNames);
                    var map = new Dictionary<string, GenericTypeParameterBuilder>();
                    for (int gi = 0; gi < genNames.Length; gi++) map[genNames[gi]] = gps[gi];
                    _methodTypeParams[bridge] = map;
                    var savedMp = _curMethodParams; _curMethodParams = map;
                    ApplyConstraints(genTps.Value, map, false);
                    try
                    {
                        ifaceRet = bmDef.TryGetProperty("ret", out var rt) ? MapType(SubstTv(DotKt.Bir.TypeNode.Read(rt), specArgs)) : typeof(void);
                        paramTypes = bmDef.GetProperty("params").EnumerateArray().Select(p => MapType(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs))).ToArray();
                    }
                    catch (Exception ex)
                    {
                        _curMethodParams = savedMp;
                        throw new InvalidOperationException(
                            $"cannot materialize emitted-base DIM bridge {ti.TB.FullName}.{name}: {ex.Message}", ex);
                    }
                    bridge.SetReturnType(ifaceRet);
                    bridge.SetParameters(paramTypes);
                    _curMethodParams = savedMp;
                    dimCall = dim.MakeGenericMethod(gps.Cast<Type>().ToArray());
                }
                else
                {
                    // Non-generic arm: resolve the signature BEFORE defining the bridge (a MapType failure is a clean skip).
                    try
                    {
                        ifaceRet = bmDef.TryGetProperty("ret", out var rt) ? MapType(SubstTv(DotKt.Bir.TypeNode.Read(rt), specArgs)) : typeof(void);
                        paramTypes = bmDef.GetProperty("params").EnumerateArray().Select(p => MapType(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs))).ToArray();
                    }
                    catch { continue; }
                    bridge = ti.TB.DefineMethod("dotkt$dimimpl$" + name + "$" + (_covarBridge++),
                        MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                        ifaceRet, paramTypes);
                    dimCall = ti.IsGeneric ? TypeBuilder.GetMethod(ti.TB.MakeGenericType(ti.TB.GetGenericArguments()), dim) : (MethodInfo)dim;
                }
                StampCompilerGenerated(bridge);   // #68: ilemit-authored generated member
                var il = bridge.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                for (int i = 0; i < paramTypes.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Callvirt, dimCall);   // `this`'s most-specific DIM (a class override still wins); base slot != this slot, so no recursion
                il.Emit(OpCodes.Ret);
                var declSlot = constructed != null ? TypeBuilder.GetMethod(constructed, baseSlot) : (MethodInfo)baseSlot;
                ti.TB.DefineMethodOverride(bridge, declSlot);
            }
        }
    }
}
