using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// .NET-INTEROP CALL BINDING (A2 / #61): the Kotlin<->CLR binding for a reference-KLIB-projected .NET member call. kotc
// emits a PLAIN `callStatic`/`callInstance` by the .NET owner's FQN IDENTITY (`callStatic Kfc.App.get_Count`,
// `callInstance System.Text.StringBuilder.Append`) carrying only frontend FACTS — static-ness (callStatic vs
// callInstance), the accessor name (`get_X`/`set_X`), `typeArgs`, the `op_` name with the receiver already
// prepended, the constructed-generic owner IDENTITY (memberType supertype walk) — and does NOT decide the .NET call
// SHAPE. THIS pass resolves the owner FQN against the loaded .NET reference assemblies (ReferenceMetadataIndex's
// long-lived MetadataLoadContext) and, when it IS a reachable .NET type, reflects the member to bind the shape:
// static/instance method -> `clrStatic`/`clrInstance`; a `get_X`/`set_X` naming a .NET property OR field ->
// `clrPropGet`/`clrPropSet`; a generic method (`typeArgs` present) -> `clrGenericStatic`/`clrGenericInstance`; an
// indexer (`get_Item`/`set_Item`, an indexed property) or a synthetic member-extension accessor (no matching
// property/field) stays a plain instance method call. A `kotlin.*`/local/unresolvable owner is left untouched (the
// stdlib is bound by MemberCallSubstitution off the ref.dll; a local type is emitted here). CLR-ONLY vocabulary that
// has no plain-Kotlin form — `.NET events` (ClrEvent<T>), `byref`/`ClrRef<T>` — is NOT emitted as a plain call by
// kotc (kotc lowers it directly, as reference-KLIB-projected CLR vocab), so it never reaches this pass. Runs BEFORE
// ClrEventSubscriptionBinding/KClassMemberBinding/MemberCallSubstitution and before BirTypeLowering, so the shaped `clr*`
// nodes still carry pure-Kotlin type tokens that the subsequent lowering turns into the CLR forms — the CIR is
// byte-identical to what kotc used to emit directly (the shape decision merely moved down a layer). Bottom-up walk,
// mirroring ClrEventSubscriptionBinding/KClassMemberBinding.
//
// RESULT STAMPS (#304, spec §2.7). Every reshape here changes a node's SHAPE and not what it produces: the `clr*`
// node stands for the same call/read, resolved to its CLR member, and leaves the same value behind. So every
// result-type stamp the plain node carried is still TRUE of the reshaped one and travels with it — `sty` (the
// frontend's instantiated stamp) and `ret` alike. This is a contract, not a nicety: `bir-common/NodeType.cs` has no
// derivation arm for ANY `clr*` kind, so those two slots ARE the reshaped node's static type. A reshape that lands a
// stamp-less node therefore leaves an operand nothing downstream can type, and an operand with no static type LEFT of
// a suspension is a stage-0 refusal (SuspendOperandPlan) of source the frontend accepted — which is what a dropped
// stamp on a `.NET`-field read (ReshapeField) and on the generic branch each produced. `dynRet` deliberately does NOT
// travel: it is the UNBOUND Kotlin call's dynamic-dispatch channel (ilemit falls back to reflection on its presence),
// so on a node already bound to a concrete CLR slot it would be a dispatch instruction rather than a type fact — and
// `sty` carries the same instantiated type without it.
static class NetInteropBinding
{
    static ReferenceMetadataIndex _refs;

    // Mutates IN PLACE (like ShapeSynthesis): this runs in bir2cir's phase-1 per-file region where every pass edits
    // `bir.Root` in place (BirFile.Root is init-only, not reassignable). The node identity is preserved (its parent link
    // stays valid); only its `k` + field set change from a plain call to the CLR shape.
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) { _refs = refs; Walk(root); }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);   // children first (bottom-up)
            Reshape(obj);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) if (item != null) Walk(item);
        }
    }

    static void Reshape(JsonObject node)
    {
        var k = Str(node["k"]);
        // #73 M4-b: a FIELD read/write on a reference-KLIB-projected .NET owner. kotc emits a plain instance/static
        // field node by the .NET-FQN identity (no shape decision); the .NET member SHAPE is bound HERE — the same axis
        // #61 used for calls.
        // A `field`/`setField` whose owner resolves to a .NET type declaring a property OR field of that name (both, via
        // MemberIsPropertyOrField) -> clrPropGet/clrPropSet, whose EmitClrPropGet/Set is struct-receiver-safe + inlines a
        // const field (unlike the plain-field external Ldfld/Callvirt route) — matching the old kotc clrPropGet parity,
        // which reshaped unconditionally. A member the refs can't see (a non-.NET owner, or a name absent from the .NET
        // type) never resolves here -> the plain `field`/`setField` is left for ilemit's own handler.
        if (k is "field" or "setField" or "setFieldExpr"
            or "staticField" or "staticFieldSet" or "setStaticField" or "setStaticFieldExpr")
        {
            ReshapeField(node, write: k is not ("field" or "staticField"));
            return;
        }
        if (k == "clrEventGet" && node["companionCall"]?.GetValue<bool>() == true)
        {
            NormalizeCompanionEvent(node);
            return;
        }
        // #73 M4.4: a BOUND method reference `netObj::m`. kotc emits a NEUTRAL `newBoundDelegate` (the same kind it uses
        // for a Kotlin-owner bound ref) carrying the owner FQN identity + argTypes; bir2cir decides the SHAPE. When the
        // owner resolves to a .NET type off the refs -> the CLR bound-delegate dialect node `newBoundClrDelegate` (ilemit
        // binds the target by reflection). A Kotlin/local owner never resolves here -> the plain `newBoundDelegate` is
        // left for ilemit's own FindMethod-based handler. Byte-identical to kotc's former newBoundClrDelegate emit.
        if (k == "newBoundDelegate") { ReshapeBoundDelegate(node); return; }
        if (k == "newSuspendLambda") { NormalizeSuspendCompanionCapture(node); return; }
        // #178: the options-taking `kotlin.text.Regex` constructors. A plain `new` on a @ClrTypeAlias owner is bound to
        // newClr by MemberCallSubstitution — but `Regex(String, RegexOption)` / `Regex(String, Set<RegexOption>)` map to
        // the BCL `Regex(String, RegexOptions)`, and the DotKt enum / set neither RESOLVES to nor carries the numeric
        // value of the `[Flags] System...RegexOptions` int param. Convert the option arg to the RegexOptions bitmask HERE
        // (there is no pure-Kotlin ctor body to do it — the expect fixes options to the ctor). A no-op for every other
        // `new`, which flows to MemberCallSubstitution.TransformNew unchanged.
        if (k == "new") { ReshapeRegexCtorOptions(node); return; }
        if (k != "callStatic" && k != "callInstance") return;
        // dll2klib's metadata-only await declaration deliberately has no CLR
        // member to bind. Preserve the Kotlin call and its marker until
        // SuspendColdLowering resolves the awaiter pattern from the refs.
        if (node["clrAwaitBridge"]?.GetValue<bool>() == true) return;
        var ownerJson = node["ownerType"];
        // Peel Nullable/Oblivious/ByRef wrappers to reach the underlying .NET Fqn (a `List<Item>?` receiver's owner is
        // spelled `nullable(fqn List<Item>)`); the ORIGINAL wrapped node is preserved verbatim in the `type` slot below
        // (ilemit unwraps nullability when resolving the owner — byte-identical to the old kotc `clrInstance.type`).
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name);
        var reflectedOwner = _refs.TryCompanionMetadataCarrier(bare, out var metadataCarrier)
            ? metadataCarrier
            : bare;
        var method = Str(node["method"]);
        var companionCall = node["companionCall"]?.GetValue<bool>() == true;
        bool? companionStatic = null;
        if (companionCall)
        {
            if (_refs.TryCompanionIsStatic(bare, out var resolvedStatic))
                companionStatic = resolvedStatic;
        }
        var netType = _refs.ResolveNetType(bare, ownerFqnNode.Args?.Length ?? 0);
        Type dotKtEmittedType = null;
        // Compiler-generated companion carriers are deliberately absent from the ordinary source-visible DotKt owner
        // index. A validated companion association is its own, narrower authority to resolve that exact physical type.
        if (netType == null && companionStatic != null)
            dotKtEmittedType = _refs.ResolveCompanionMetadataCarrier(bare, ownerFqnNode.Args?.Length ?? 0);
        else if (netType == null && _refs.HasDotKtOwner(bare))
            dotKtEmittedType = _refs.ResolveRefType(reflectedOwner, ownerFqnNode.Args?.Length ?? 0);
        // Rich-enum values()/valueOf() are synthetic enum-owner statics, not companion declarations. The frontend
        // nevertheless marks their Kotlin call shape as companionCall. Admit only those two exact, structurally
        // verified signatures when no association carrier exists; arbitrary carrier-less DotKt calls still fail.
        var carrierlessRichEnumApi = companionCall && companionStatic == null && method != null &&
            _refs.HasDotKtOwner(bare) &&
            _refs.IsKotlinRichEnumStaticApi(bare, method, DeclarationArgs(node).Count);
        if (carrierlessRichEnumApi) companionStatic = true;
        if (companionCall && companionStatic == null)
            throw new InvalidOperationException(
                $"DotKt companion call owner '{bare}' has no trusted companion carrier");
        // An ordinary nested companion is a Kotlin declaration carrier, not an arbitrary CLR interop owner. Preserve
        // its call in Kotlin form so MemberCallSubstitution can either consume an authored CLR binding on the member
        // or leave an intrinsic-less method targeting the carrier's real Kotlin body. Reclassifying the entire carrier
        // as clrInstance here loses that distinction (an alias companion may contain both kinds).
        if (companionStatic == false) return;
        if (companionStatic != null)
        {
            netType = dotKtEmittedType ?? netType ??
                _refs.ResolveRefType(reflectedOwner, ownerFqnNode.Args?.Length ?? 0)
                ?? throw new InvalidOperationException(
                    $"companion call owner '{bare}' is absent from the selected references");
            // CIR must carry the exact nested TypeDef identity that bir2cir resolved. In particular,
            // `Outer<T>.$Companion<Capture>` is reflected as `Outer`1+$Companion`1`; ilemit cannot reconstruct the
            // outer segment's arity by appending a suffix to the source-style carrier token.
            if (metadataCarrier != null && ownerJson is JsonObject companionOwnerJson)
                companionOwnerJson["name"] = metadataCarrier;
        }
        // A basic Kotlin enum is emitted as a real CLR value-type enum. Its constants, inherited System.Enum/Object
        // members, value receiver address-taking, and constrained dispatch are therefore ordinary CLR ABI facts even
        // though the declaration came from Kotlin. Route that entire owner shape through this binder; rich Kotlin enums
        // remain class-like DotKt owners and stay on the Kotlin ABI path.
        if (netType == null && dotKtEmittedType?.IsEnum == true)
            netType = dotKtEmittedType;
        // A referenced DotKt owner normally stays on the Kotlin ABI path: reflecting every Kotlin member as raw CLR
        // would erase property/operator conventions that MemberCallSubstitution owns. One language ABI seam genuinely
        // needs the emitted metadata, though: `Comparable<T>.compareTo` implements CLR IComparable<T>.CompareTo and the
        // exported slot is therefore PascalCase. Admit ONLY that structurally-proven seam to the existing binder. A
        // standalone Kotlin `operator fun compareTo` still declares lowercase `compareTo` and does not match, while an
        // arbitrary package/type name is irrelevant. A Comparable class MAY also have an unrelated lowercase overload
        // (`compareTo(Int)`); inspect the resolved call signature so only the self-typed Comparable slot enters this
        // seam (#182). This keeps the decision in bir2cir and leaves ilemit an exact CIR method name, without
        // reclassifying the rest of a referenced Kotlin library as C#.
        if (netType == null && k == "callInstance" && method == "compareTo"
            && dotKtEmittedType is Type dotKtComparable
            && DeclaresPublicMethodNamed(dotKtComparable, "CompareTo")
            && IsComparableSelfCall(dotKtComparable, ownerFqnNode, node))
            netType = dotKtComparable;
        if (netType == null) return;   // not a reachable .NET-interop owner -> leave for the other binders
        var comparableSelfCall = k == "callInstance" && method == "compareTo"
            && IsComparableSelfCall(netType, ownerFqnNode, node);

        // kotc preserves Kotlin object/companion semantics in BIR. Resolve a referenced Kotlin enum constant's actual
        // static field shape here; real companions have already been classified by their trusted carrier metadata.
        // A DotKt-emitted Kotlin object does not enter this .NET binder, so its INSTANCE receiver remains.
        var propertyKind = Str(node["prop"]);
        var isStatic = companionStatic ?? (k == "callStatic"
            || (k == "callInstance" && netType.IsAbstract && netType.IsSealed)
            || (k == "callInstance" && propertyKind is "get" or "set"
                && MemberIsStaticPropertyOrField(netType, method)));
        var hasTypeArgs = node["typeArgs"] is JsonArray ta && ta.Count > 0;

        // A declaration loaded from a standard reference KLIB surfaces a CLR event as an ordinary read-only
        // `ClrEvent<T>` property. Recover its CLR shape here from the authoritative reference metadata.
        var projectedPropKind = Str(node["prop"]);
        var projectedEventName = projectedPropKind == "get" ? method : null;
        if (projectedEventName != null &&
            _refs.TryClrEventIsStatic(bare, projectedEventName, out var eventIsStatic))
        {
            var recv = node["recv"];
            // Captured BEFORE the Clear detaches them: the handle this reads is the same `ClrEvent<T>` value the
            // property-get produced, so its result stamps travel like every other reshape's (RESULT STAMPS above).
            var evSty = node["sty"];
            var evRet = node["ret"];
            node.Clear();
            node["k"] = "clrEventGet";
            node["type"] = eventIsStatic ? CloseStaticOwner(ownerJson, netType) : ownerJson?.DeepClone();
            node["name"] = projectedEventName;
            node["static"] = eventIsStatic;
            if (!eventIsStatic) node["recv"] = recv;
            if (evSty != null) node["sty"] = evSty;
            if (evRet != null) node["ret"] = evRet;
            return;
        }

        // Detach every current field (removing a key from a JsonObject detaches its value) so it can be re-added in the
        // CLR-shape order — byte-identical to what kotc used to emit directly, only the shape decision moved here.
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        // A declaration loaded from a regular Kotlin KLIB is not reference-KLIB-projected, so kotc emits the ordinary
        // external-member dialect: the declared parameter types are in `sig`, not `argTypes`/`shapeTypes`.
        // Once the owner resolves against the authoritative CLR reference set, both fields carry the same frontend
        // fact needed here. Accepting `sig` lets bir2cir own the CLR binding without teaching kotc a KLIB side channel.
        JsonNode TakeMemberSig(string preferred) => Take(preferred) ?? Take("sig") ?? new JsonArray();
        JsonNode TakeDeclaredSig() =>
            Take("sig") ?? Take("shapeTypes") ?? Take("argTypes") ?? new JsonArray();
        var owner = Take("ownerType");
        if (isStatic) owner = CloseStaticOwner(owner, netType);
        var args = Take("args") as JsonArray ?? new JsonArray();
        // A `super.X()` (issue #14) rides in as `"super":true` on the callInstance. kotc already forced this call
        // non-virtual, but that intent is dropped when we reshape to a CLR node. Carry the flag onto the produced
        // clrInstance/clrPropGet/clrPropSet/clrGenericInstance so ilemit emits a non-virtual `call` (a base-slot
        // dispatch like C#'s `base.M()`) instead of a `callvirt` that would re-dispatch to THIS class's override.
        var superNode = Take("super");
        void CarrySuper() { if (superNode != null) node["super"] = superNode; }
        // RESULT STAMPS (see the top of this file), re-added after the detach above. `sty` (#122) is carried HERE, once
        // for every branch, so a LATE consumer (StringCharSequenceBridge) recovers the reshaped node's Kotlin static
        // type even where the node is non-generic and has no `ret` at all — a String-typed .NET property, say. `ret`
        // rides in each branch's OWN key position, so it is carried by the branches through `CarryRet`, which leaves a
        // `ret` a branch has already written where that branch put it.
        if (Take("sty") is JsonNode styCarry) node["sty"] = styCarry;
        void CarryRet() { if (node["ret"] == null && Take("ret") is JsonNode retCarry) node["ret"] = retCarry; }

        // GENERIC .NET method: the presence of `typeArgs` (a frontend fact) is the signal. ilemit MakeGenericMethods it.
        // W1-S1 (#46/#44): retain the FIR-resolved declaration parameters as bir2cir's internal
        // `resolvedMemberParams` matching input. They stay structured and open over method type variables;
        // ClrMemberResolution consumes them to author one complete memberRef before CIR is serialized.
        if (hasTypeArgs)
        {
            node["k"] = isStatic ? "clrGenericStatic" : "clrGenericInstance";
            node["type"] = owner;
            node["method"] = method;
            node["typeArgs"] = Take("typeArgs");
            node["resolvedMemberParams"] = NormalizeMemberSig(TakeMemberSig("shapeTypes") as JsonArray);
            CarryRet();   // the generic branch's own result stamp (RESULT STAMPS above); ilemit reads the reflected
                          // definition's return type, so this is read inside bir2cir only — where it is the answer.
            if (!isStatic) node["recv"] = Take("recv");
            node["args"] = args;
            if (Take("suspendCall") is JsonNode sc1) node["suspendCall"] = sc1;
            if (!isStatic) CarrySuper();
            return;
        }

        // .NET ENUM consumed as a Kotlin Enum (#107): a reference-KLIB-projected .NET enum carries a synthetic `kotlin.Enum<Self>`
        // supertype (dll2klib `IsEnum` branch), so kotc resolves the INHERITED Kotlin Enum contract on a CONCRETE
        // .NET-enum receiver as a plain property-get by IDENTITY (`callInstance ownerType=System.DayOfWeek method=name/
        // ordinal prop=get`) — but System.Enum declares NEITHER as a .NET property (they'd fall through to a non-existent
        // `get_name`/`get_ordinal`). Bind them to the CLR enum semantics: `name` -> ToString() (the constant name, the
        // System.Enum override), `ordinal` -> the DECLARATION INDEX via `enumOrdinal` carrying the enum type (ilemit does
        // Array.IndexOf(Enum.GetValues(t), value) — Kotlin-faithful even for a sparse/negative/aliased .NET enum). The
        // GENERIC-receiver case (`e: T`, `T : Enum<T>`, owner kotlin.Enum) is handled separately by EnumMemberBinding.
        if (!isStatic && IsNetEnum(netType) && Str(v.TryGetValue("prop", out var pj) ? pj : null) == "get"
            && (method == "name" || method == "ordinal"))
        {
            var recv0 = Take("recv");
            var replacement = method == "name"
                ? new JsonObject { ["k"] = "objMethod", ["method"] = "ToString", ["recv"] = recv0 }
                : new JsonObject { ["k"] = "enumOrdinal", ["e"] = recv0, ["type"] = owner };
            node.Clear();
            foreach (var pair in replacement) node[pair.Key] = pair.Value?.DeepClone();
            return;
        }

        // PROPERTY ACCESSOR by the frontend get/set KIND (A2 step 3): kotc emits the BARE property NAME + a
        // `"prop":"get"/"set"` role (a frontend fact from correspondingPropertySymbol), not a CLR accessor name.
        // A real non-indexed Property/field of that bare name becomes clrPropGet/clrPropSet; otherwise a synthetic
        // accessor method receives its physical name through KotlinPropertyAccessors' one-way allocation rule.
        var propKind = Str(Take("prop"));
        // Standard KLIB metadata has an operator bit but no CLR-indexer side channel, so kotc emits plain operator
        // `get`/`set` calls. A real default indexed property and absence of a literal same-name method is sufficient
        // to recover the accessor without guessing its CLR name (custom IndexerName remains honored).
        if (propKind == null && method is "get" or "set" &&
            DefaultIndexerAccessor(netType, method == "set") is string projectedIndexerAccessor)
            method = projectedIndexerAccessor;
        // .NET DEFAULT INDEXED PROPERTY (A2 step 4): kotc emits the faithful Kotlin get/set operator identity
        // (`method:"get"/"set"`) + an index marker; it does NOT bake the `get_Item`/`set_Item` slot (WRONG for a custom
        // `[IndexerName]`). Resolve the .NET type's default indexed property off the refs (its DefaultMember/[IndexerName]
        // name) -> its `get_`/`set_` accessor method, then fall through to the PLAIN clrInstance method path — an indexer
        // is an INDEXED property, so MemberIsPropertyOrField excludes it and it stays a method call, byte-identical to the
        // old hardcoded `get_Item`/`set_Item` for the standard case.
        if (propKind == "index-get" || propKind == "index-set")
        {
            var isIxSet = propKind == "index-set";
            // A genuine .NET indexer -> its `get_`/`set_` accessor (a custom `[IndexerName]` honored via DefaultMember).
            // Otherwise (#133 case2): a reference-KLIB-projected DotKt owner (a Kotlin-emitted type — e.g. `class Arr<T>` with
            // `operator fun get/set`) has NO .NET indexer property; it emitted the PLAIN operator method the frontend
            // named (`method` = "get"/"set", dll2klib's clrName). Bind to that REAL method when the type declares it —
            // the get_Item/set_Item BCL fallback dangles on the Kotlin-emitted type. A DotKt owner declares the literal
            // `get`/`set`; a BCL indexer type declares `get_Item` (found by DefaultIndexerAccessor above), so this cleanly
            // separates the two. The get_Item/set_Item fallback survives for a BCL type whose indexer DefaultMember is
            // absent from the metadata (defensive; unchanged behavior).
            method = DefaultIndexerAccessor(netType, isIxSet)
                ?? (method != null && DeclaresPublicMethodNamed(netType, method) ? method : (isIxSet ? "set_Item" : "get_Item"));
        }
        else if (propKind == "get" || propKind == "set")
        {
            var isSet = propKind == "set";
            if (method != null && MemberIsPropertyOrField(netType, method))
            {
                if (!isSet)
                {
                    node["k"] = "clrPropGet";
                    node["type"] = owner;
                    node["name"] = method;
                    node["ret"] = Take("ret");
                    node["static"] = isStatic;
                    node["recv"] = isStatic ? null : Take("recv");
                    if (!isStatic) CarrySuper();
                    return;
                }
                node["k"] = "clrPropSet";
                node["type"] = owner;
                node["name"] = method;
                node["static"] = isStatic;
                node["recv"] = isStatic ? null : Take("recv");
                JsonNode setVal = null;
                if (args.Count > 0) { setVal = args[0]; args.RemoveAt(0); }
                node["value"] = setVal;
                if (!isStatic) CarrySuper();
                return;
            }
            // No matching .NET property/field -> a Kotlin synthetic accessor method. Apply the shared forward
            // allocation and fall through to the plain instance/static method path.
            method = KotlinPropertyAccessors.PhysicalName(method, propKind);
        }

        // .NET OPERATOR: kotc emits a .NET-type operator (`Vec2 + Vec2`, `-a`) as the PLAIN Kotlin operator identity
        // (`callInstance method="plus" recv:<a> args:[<b>]`) — it does NOT know the CLR `op_X` slot (layer purity).
        // Reconstruct the .NET static operator off the refs: map the Kotlin operator name to its `op_X` slot, confirm the
        // CLR type declares that `op_X` as a `public static` method (DON'T rewrite a Kotlin `plus` on a non-operator .NET
        // type), and emit `clrStatic op_X` with the receiver PREPENDED as the first arg (binary: [recv, arg]; unary
        // unaryMinus/unaryPlus/inc/dec: [recv] only). This is the exact node kotc used to emit directly (callStatic op_X,
        // receiver already prepended) -> byte-identical CIR. The receiver's type is the declaring .NET type = the owner,
        // mirroring kotc's old `birType(recv.type)` for argTypes[0].
        if (!isStatic && method != null && OperatorToNet.TryGetValue(method, out var opNet)
            && DeclaresPublicStaticMethod(netType, opNet))
        {
            var recv = Take("recv");
            var argTypes0 = TakeMemberSig("argTypes") as JsonArray ?? new JsonArray();
            var newArgTypes = new JsonArray { owner.DeepClone() };
            while (argTypes0.Count > 0) { var at = argTypes0[0]; argTypes0.RemoveAt(0); newArgTypes.Add(at); }
            var newArgs = new JsonArray { recv };
            while (args.Count > 0) { var a = args[0]; args.RemoveAt(0); newArgs.Add(a); }
            node["k"] = "clrStatic";
            node["type"] = owner;
            node["method"] = opNet;
            node["argTypes"] = newArgTypes;
            node["ret"] = Take("ret");
            node["args"] = newArgs;
            if (Take("suspendCall") is JsonNode scOp) node["suspendCall"] = scOp;
            return;
        }

        // #179 — a Kotlin `operator fun compareTo` on a reference-KLIB-projected DotKt owner that implements Comparable<Self>.
        // The DotKt class's CLR slot is the PascalCase `System.IComparable<T>.CompareTo` (dll2klib renamed the member +
        // restored the `kotlin.Comparable<Self>` supertype), but kotc emits the plain Kotlin name `compareTo` (a member
        // clrName is not a kotc channel), so a cross-module `c1 < c2` (the frontend resolves `<` to `compareTo`) would
        // dangle on a non-existent lowercase slot. The INSTANCE-slot analog of the plus->op_Addition rule above: rebind
        // `compareTo` -> `CompareTo` when the resolved call is the self-typed generic-IComparable slot. A verbatim
        // lowercase sibling with a different parameter type (for example compareTo(Int)) stays on the Kotlin ABI path,
        // while the self call still reaches this CLR seam (#182), then falls through to the plain clrInstance path.
        if (!isStatic && method == "compareTo"
            && DeclaresPublicMethodNamed(netType, "CompareTo")
            && comparableSelfCall)
            method = "CompareTo";

        // PLAIN static/instance method (incl. indexer get_Item/set_Item, member-extension synthetic accessor).
        node["k"] = isStatic ? "clrStatic" : "clrInstance";
        node["type"] = owner;
        node["method"] = method;
        // Resolve the CLR slot from the callee's complete declaration
        // signature. DefaultArgSplice has already materialized every omitted
        // positional or trailing optional argument from the selected
        // reference declaration; ilemit therefore receives a complete vector.
        node["argTypes"] = TakeDeclaredSig();
        node["ret"] = Take("ret");
        if (!isStatic) node["recv"] = Take("recv");
        node["args"] = args;
        if (Take("suspendCall") is JsonNode sc2) node["suspendCall"] = sc2;
        if (!isStatic) CarrySuper();
    }

    // #73 M4-b — bind an instance or static field access on a reference-KLIB-projected .NET owner to
    // clrPropGet/clrPropSet. Resolves the owner off the refs (skips kotlin.*/local owners); a name that is a real .NET
    // property OR field (MemberIsPropertyOrField matches both) is reshaped — EmitClrPropGet/Set falls through property
    // -> get_ accessor -> field, so it serves a genuine field too (with const-inlining + struct-safe receiver). This is
    // also required for direct IS_STATIC_PROPERTY projection: a CLR literal has no storage for a plain ldsfld.
    // A name the refs can't see stays plain.
    static void ReshapeField(JsonObject node, bool write)
    {
        var ownerJson = node["ownerType"];
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name);
        var companionCall = node["companionCall"]?.GetValue<bool>() == true;
        bool? companionStatic = null;
        if (companionCall)
        {
            if (_refs.TryCompanionIsStatic(bare, out var resolvedStatic))
                companionStatic = resolvedStatic;
        }
        var netType = companionStatic != null
            ? _refs.ResolveRefType(bare, ownerFqnNode.Args?.Length ?? 0)
            : _refs.ResolveNetType(bare, ownerFqnNode.Args?.Length ?? 0);
        if (companionCall && companionStatic == null)
        {
            var fieldName = Str(node["name"]);
            netType ??= _refs.HasDotKtOwner(bare)
                ? _refs.ResolveRefType(bare, ownerFqnNode.Args?.Length ?? 0)
                : null;
            if (netType == null || !_refs.IsKotlinRichEnumOwner(bare) ||
                !IsPublicStaticSelfField(netType, fieldName))
                throw new InvalidOperationException(
                    $"DotKt companion field owner '{bare}' has no trusted companion carrier");
            companionStatic = true;
        }
        if (netType == null)
        {
            if (companionCall)
                throw new InvalidOperationException(
                    $"companion field owner '{bare}' is absent from the selected references");
            return;
        }
        var name = Str(node["name"]);
        if (name == null || !MemberIsPropertyOrField(netType, name)) return;
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        var isStatic = companionStatic ??
            (netType.IsAbstract && netType.IsSealed || MemberIsStaticPropertyOrField(netType, name));
        node["k"] = write ? "clrPropSet" : "clrPropGet";
        node["type"] = isStatic ? CloseStaticOwner(Take("ownerType"), netType) : Take("ownerType");
        node["name"] = Take("name");
        node["static"] = isStatic;
        if (!isStatic) node["recv"] = Take("recv");
        if (write) node["value"] = Take("value");
        // A READ produces the field's value, so the plain `field` node's result stamps are still true of the
        // clrPropGet and travel with it (RESULT STAMPS at the top of this file). Without them a `.NET` field read is a
        // node NOTHING can type — `v.X + suspending()` was a stage-0 refusal of source the frontend accepted (#304).
        // A WRITE leaves no value; there is no result to stamp.
        if (write) return;
        if (Take("sty") is JsonNode fieldSty) node["sty"] = fieldSty;
        if (Take("ret") is JsonNode fieldRet) node["ret"] = fieldRet;
    }

    // A projected static on a generic CLR TypeDef has no enclosing type argument in Kotlin syntax, but its CIL
    // MemberRef parent must still be a TypeSpec. Close the exact reflected owner selected above, rather than using a
    // name-only arity table (Task and Task<T>, for example, deliberately share the same source FQN).
    internal static JsonNode CloseStaticOwner(JsonNode ownerJson, Type netType)
    {
        if (TypeJson.Read(ownerJson) is not TypeNode.Fqn { Args: null } owner ||
            netType == null || !netType.IsGenericTypeDefinition)
            return ownerJson?.DeepClone();
        var arity = netType.GetGenericArguments().Length;
        if (arity == 0) return ownerJson?.DeepClone();
        return TypeJson.Write(new TypeNode.Fqn(owner.Name,
            Enumerable.Repeat<TypeNode>(new TypeNode.Fqn("kotlin.Any"), arity).ToArray()));
    }

    static void NormalizeCompanionEvent(JsonObject node)
    {
        var owner = TypeJson.Read(node["type"]) as TypeNode.Fqn;
        var bare = owner == null ? null : ReferenceMetadataIndex.BareOwnerFqn(owner.Name);
        if (bare == null) throw new InvalidOperationException("companion event has no owner");
        bool isStatic;
        if (!_refs.TryCompanionIsStatic(bare, out isStatic))
            throw new InvalidOperationException(
                $"DotKt companion event owner '{bare}' has no trusted companion carrier");
        node.Remove("companionCall");
        node["static"] = isStatic;
        if (isStatic) node.Remove("recv");
    }

    // #73 M4.4 — reshape a BOUND method-ref `newBoundDelegate` on a reference-KLIB-projected .NET owner to the CLR
    // `newBoundClrDelegate` dialect node (ilemit resolves the target by reflection over the .NET type). Resolves the
    // owner off the refs (skips kotlin.*/local owners — those stay a plain newBoundDelegate ilemit binds via FindMethod).
    // The field set + order mirror kotc's former newBoundClrDelegate emission exactly (clrType from the owner identity,
    // method/argTypes/virtual/recv/funcType carried verbatim — including the method already Object-slot-renamed upstream).
    static void ReshapeBoundDelegate(JsonObject node)
    {
        var companionCall = node["companionCall"]?.GetValue<bool>() == true;
        // Only the .NET-bound producer (BirEmitter method-ref, clrOwner branch) carries `argTypes`; the Kotlin-owner
        // bound ref emits NONE. Gate on it so a cross-module Kotlin owner (a ProjectReference lib loaded via --ref,
        // which ResolveNetType WOULD resolve) is never mis-reshaped into a newBoundClrDelegate claiming `argTypes:[]`
        // — it stays the plain newBoundDelegate ilemit binds by FindMethod, exactly as before Wave 8. A validated
        // companion reference is the one additional admitted source: BindExternalUses marks it explicitly and its
        // ordinary Kotlin declaration vector lives in `sig`, which is enough for the exact member-level decision below.
        if (node["argTypes"] == null && !companionCall) return;
        var ownerJson = node["ownerType"];
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name);
        var declarationArgs = DeclarationArgs(node);
        var methodArity = (node["typeArgs"] as JsonArray)?.Count ?? 0;
        bool? companionStatic = null;
        if (companionCall)
        {
            if (_refs.TryCompanionIsStatic(bare, out var resolvedStatic))
                companionStatic = resolvedStatic;
        }
        var netType = companionStatic != null
            ? _refs.ResolveRefType(bare, ownerFqnNode.Args?.Length ?? 0)
            : _refs.ResolveNetType(bare, ownerFqnNode.Args?.Length ?? 0);
        if (netType == null && companionCall && _refs.HasDotKtOwner(bare))
        {
            netType = _refs.ResolveRefType(bare, ownerFqnNode.Args?.Length ?? 0);
            if (netType != null && _refs.IsKotlinRichEnumStaticApi(
                    bare, Str(node["method"]), declarationArgs.Count))
                companionStatic = true;
            else
                throw new InvalidOperationException(
                    $"DotKt companion callable-reference owner '{bare}' has no trusted companion carrier");
        }
        if (companionCall && companionStatic == null)
            throw new InvalidOperationException(
                $"DotKt companion callable-reference owner '{bare}' has no trusted companion carrier");
        if (netType == null)
        {
            if (companionCall)
                throw new InvalidOperationException(
                    $"companion callable-reference owner '{bare}' is absent from the selected references");
            return;   // a Kotlin/local owner -> leave the plain newBoundDelegate for ilemit's handler
        }
        // A nested companion can mix real Kotlin carrier bodies with members explicitly bound to the semantic
        // outer's CLR alias. Callable references do not pass through MemberCallSubstitution, so resolve that one
        // member here by the complete declaration identity. Never classify the carrier as a whole by its name.
        var companionCarrier = _refs.TryCompanionPhysicalOwner(bare, out var mappedCarrier)
            ? mappedCarrier
            : bare;
        var declarationSignature = declarationArgs?.Select(TypeJson.Read).ToList();
        string companionIntrinsic = null;
        string companionSemanticOwner = null;
        var exactBoundCompanion = companionCall && declarationSignature != null
            && declarationSignature.All(t => t != null)
            && _refs.TryExactMemberIntrinsic(
                ReferenceMetadataIndex.BareOwnerFqn(companionCarrier), Str(node["method"]), methodArity,
                declarationSignature, out companionIntrinsic)
            && _refs.TryCompanionSemanticOwner(companionCarrier, out companionSemanticOwner)
            && _refs.TryResolveClrOwner(companionSemanticOwner, out _, out _);
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        var isStatic = exactBoundCompanion || companionStatic == true;
        node["k"] = isStatic ? "newClrStaticDelegate" : "newBoundClrDelegate";
        node["clrType"] = exactBoundCompanion
            ? TypeJson.Write(new TypeNode.Fqn(companionSemanticOwner, ownerFqnNode.Args))
            : Take("ownerType");
        var sourceMethod = Take("method");
        node["method"] = exactBoundCompanion ? companionIntrinsic : sourceMethod;
        node["argTypes"] = Take("argTypes") ?? Take("shapeTypes") ?? Take("sig") ?? new JsonArray();
        var typeArgs = Take("typeArgs");
        if (typeArgs != null) node["typeArgs"] = typeArgs;
        node["virtual"] = Take("virtual");
        var recv = Take("recv");
        if (!isStatic) node["recv"] = recv;
        node["funcType"] = Take("funcType");
        // The reshape changes only CLR call shape. The frontend-selected Kotlin declaration remains authoritative and
        // is consumed by DeclarationIdentityBinding after this pass; dropping it here would make the CLR delegate
        // resolver search the erased overload set again.
        if (Take(DeclarationIdentityBinding.Key) is JsonNode declarationId)
            node[DeclarationIdentityBinding.Key] = declarationId;
        // Preserve evaluation when a real trusted nested companion member is explicitly authored as a CLR static
        // binding. The receiver is a real singleton value even though the selected physical member is static.
        if (isStatic && recv != null && exactBoundCompanion)
        {
            var lowered = node.DeepClone() as JsonObject;
            var preserved = CallEvalLowering.PreserveUnreadValueBefore(
                recv, lowered, $"static companion callable reference '{bare}.{Str(sourceMethod)}'");
            if (preserved is JsonObject wrapped)
            {
                node.Clear();
                foreach (var kv in wrapped) node[kv.Key] = kv.Value?.DeepClone();
            }
        }
    }

    static void NormalizeSuspendCompanionCapture(JsonObject node)
    {
        var physicalOwner = Str(node["externalCompanionOwner"]);
        if (physicalOwner == null) return;
        node.Remove("externalCompanionOwner");
        // A validated external companion is an ordinary nested Kotlin object with a real $INSTANCE value. Keep the
        // bound receiver capture even when the referenced member is authored as a CLR static binding: evaluating and
        // storing that value at reference construction preserves Kotlin's bound-reference evaluation order, and an
        // unused capture is a valid (if deliberately unoptimized) state-machine field. Inferring static/instance from
        // the adapted wrapper body is unsound once inline/default expansion or inherited dispatch changes that body.
        if (!_refs.TryCompanionIsStatic(physicalOwner, out var isStatic) || isStatic)
            throw new InvalidOperationException(
                $"external companion callable reference owner '{physicalOwner}' has no trusted instance carrier");
    }

    // The frontend's resolved declaration vector has three spellings depending on call family:
    // ordinary external calls carry argTypes, generic calls carry shapeTypes, and Kotlin ABI calls carry sig.
    // Keep that order so static/instance classification and the later CLR member resolver see the same descriptor.
    static JsonArray DeclarationArgs(JsonObject node) =>
        node["argTypes"] as JsonArray ?? node["shapeTypes"] as JsonArray ?? node["sig"] as JsonArray;

    // W1-S1 (#46): the clrGeneric* `resolvedMemberParams` = the callee's declared param types, matched structurally by
    // ClrMemberResolution against the reflected .NET method definition. Normalize each entry to reflection's OPEN form:
    // a nullability ANNOTATION over a type-var (`T?`, `T!`) reflects as the bare open param `T` (there is no `T?` Type),
    // so unwrap `nullable`/`oblivious` around a `tv` at any depth. Without this the later NullableGenericErasure
    // pass object-erases a `nullable(tv)` entry (the boxed value rep) to `object`, which then fails to match the open
    // `T` param. A `nullable(value)` (`Int?` = `Nullable<Int32>`) is a real reflected type and is KEPT.
    static JsonNode NormalizeMemberSig(JsonArray shapeTypes)
    {
        var result = new JsonArray();
        if (shapeTypes != null)
            foreach (var st in shapeTypes)
                result.Add(TypeJson.Read(st) is TypeNode t ? TypeJson.Write(NormSigTv(t)) : st?.DeepClone());
        return result;
    }

    static TypeNode NormSigTv(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => NormSigTv(o.Of),                          // annotation-only wrapper: always unwrap
        TypeNode.Nullable n => NormSigTv(n.Of) is TypeNode.Tv tv ? tv     // `T?` reflects as bare `T`
                               : new TypeNode.Nullable(NormSigTv(n.Of)),  // `Int?` stays Nullable<Int32>
        TypeNode.Fqn { Args: { } fa } f => new TypeNode.Fqn(f.Name, fa.Select(NormSigTv).ToArray()),
        TypeNode.Array a => new TypeNode.Array(NormSigTv(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(NormSigTv(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, NormSigTv(fn.Ret), fn.Params.Select(NormSigTv).ToArray(),
                                          fn.Recv == null ? null : NormSigTv(fn.Recv)),
        _ => t,
    };

    // Peel Nullable/Oblivious/ByRef wrappers off an owner type slot to reach the underlying .NET Fqn (name + type-args),
    // so a `List<Item>?`/`T!`/byref receiver resolves its open .NET definition. Also accepts a LEGACY STRING owner token
    // (kotc emits some owners — a referenced file class `LibKt`, the await marker `kotlin.clr.CoroutinesKt` — as a bare
    // string, not a structured `{t:fqn}` node); it carries no structured args (a method-generic's args live in
    // `typeArgs`). null when there is no Fqn underneath.
    static TypeNode.Fqn UnwrapFqn(JsonNode ownerJson)
    {
        if (ownerJson is JsonValue sv && sv.TryGetValue<string>(out var s) && s != null)
            return new TypeNode.Fqn(s);
        var t = TypeJson.Read(ownerJson);
        while (true)
            switch (t)
            {
                case TypeNode.Fqn f: return f;
                case TypeNode.Nullable nu: t = nu.Of; break;
                case TypeNode.Oblivious ob: t = ob.Of; break;
                case TypeNode.ByRef br: t = br.Of; break;
                default: return null;
            }
    }

    // The INVERSE of dll2klib's OPERATOR_NAMES (dll2klib Program.cs): a Kotlin `operator fun` name -> the .NET `op_X`
    // static-method slot. kotc emits the Kotlin identity; this pass reconstructs the .NET operator off the refs.
    static readonly Dictionary<string, string> OperatorToNet = new(StringComparer.Ordinal)
    {
        ["plus"] = "op_Addition",
        ["minus"] = "op_Subtraction",
        ["times"] = "op_Multiply",
        ["div"] = "op_Division",
        ["rem"] = "op_Modulus",
        ["unaryMinus"] = "op_UnaryNegation",
        ["unaryPlus"] = "op_UnaryPlus",
        ["inc"] = "op_Increment",
        ["dec"] = "op_Decrement",
    };

    // True iff the resolved owner is a .NET `enum` type (#107) — its members (name/ordinal) bind to the CLR enum
    // semantics rather than the plain property-accessor path. IsEnum is available on a MetadataLoadContext type.
    static bool IsNetEnum(Type type) { try { return type.IsEnum; } catch { return false; } }

    // True iff the .NET type declares `name` as a public static method (a `op_X` operator is a public static special
    // method on the declaring type). Guards against rewriting a Kotlin `plus` on a .NET type that has no such operator.
    static bool DeclaresPublicStaticMethod(Type type, string name)
    {
        try { return type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == name); }
        catch { return false; }
    }

    static bool IsPublicStaticSelfField(Type type, string name)
    {
        if (type == null || name == null) return false;
        try
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            return field != null && field.FieldType == type;
        }
        catch { return false; }
    }

    // The .NET DEFAULT INDEXED PROPERTY's `get_`/`set_` accessor slot name (A2 step 4). kotc's old hardcode was always
    // `get_Item`/`set_Item`; reflecting the type's `DefaultMemberAttribute` (which `[IndexerName("X")]` sets) honors a
    // custom-named indexer (e.g. `get_Chars`). Walks the type + bases + interfaces; prefers the indexed property whose
    // name matches the DefaultMember, else any indexed property. Returns the accessor MethodInfo.Name, or null if none.
    static string DefaultIndexerAccessor(Type type, bool isSet)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            string defaultMember = null;
            try
            {
                var dm = cur.GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.DefaultMemberAttribute");
                if (dm != null && dm.ConstructorArguments.Count > 0) defaultMember = dm.ConstructorArguments[0].Value as string;
            }
            catch { }
            try
            {
                PropertyInfo chosen = null;
                foreach (var p in cur.GetProperties(Flags))
                {
                    if (p.GetIndexParameters().Length == 0) continue;   // not an indexer
                    if (defaultMember != null && p.Name == defaultMember) { chosen = p; break; }
                    chosen ??= p;
                }
                if (chosen != null)
                {
                    var acc = isSet ? chosen.SetMethod : chosen.GetMethod;
                    if (acc != null) return acc.Name;
                }
            }
            catch { /* metadata-load edge on a malformed member table — keep walking */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return null;
    }

    // True iff the .NET type (or a base/interface) declares a NON-indexed property OR a field of this name — the two
    // members kotc's clrPropGet/clrPropSet covers (a property accessor, or a static/instance field read as ldsfld/ldfld).
    // An INDEXER (an indexed property, e.g. "Item") is excluded (it stays a plain get_Item/set_Item method call).
    internal static bool MemberIsPropertyOrField(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try
            {
                foreach (var p in cur.GetProperties(Flags))
                    if (p.Name == name && p.GetIndexParameters().Length == 0) return true;
                foreach (var fi in cur.GetFields(Flags))
                    if (fi.Name == name) return true;
            }
            catch { /* metadata-load edge on a malformed member table — treat as no match */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return false;
    }

    // The static/instance declaration bit for a property/field surfaced through Kotlin companion syntax. Refuse
    // ambiguous hierarchy collisions instead of choosing a first reflection result.
    static bool MemberIsStaticPropertyOrField(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var matches = new List<bool>();
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try
            {
                foreach (var p in cur.GetProperties(Flags))
                {
                    if (p.Name != name || p.GetIndexParameters().Length != 0) continue;
                    var accessor = p.GetMethod ?? p.SetMethod;
                    if (accessor != null) matches.Add(accessor.IsStatic);
                }
                foreach (var field in cur.GetFields(Flags))
                    if (field.Name == name) matches.Add(field.IsStatic);
            }
            catch { /* metadata-load edge — ambiguity/failure remains false */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return matches.Count == 1 && matches[0];
    }

    // True iff the .NET type (or a base/interface) declares a method of this name (any arity), public OR protected —
    // a Kotlin class can override a PROTECTED VIRTUAL .NET member (the WinUI OnLaunched pattern: `override fun Tag()`
    // over a protected `Base.Tag`). Used by DeclarationRename's dll2klib-override slot resolution (A2 step 5) to
    // confirm a Kotlin override binds a REAL .NET method before it keeps the identity slot — dll2klib injects the
    // Kotlin method identity EQUAL to the .NET name. NonPublic covers the protected/family case.
    internal static bool DeclaresPublicMethodNamed(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try { if (cur.GetMethods(Flags).Any(m => m.Name == name)) return true; }
            catch { /* metadata-load edge — keep walking */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return false;
    }

    // True iff this resolved call is the owner's `Comparable<Self>.compareTo(Self)` slot. Both halves are load-bearing:
    // the reflected owner must implement IComparable with ITSELF (not merely some unrelated T), and the frontend's
    // resolved one-parameter signature must name that same owner. A sibling `compareTo(Int)` therefore remains
    // lowercase even though the class also has the PascalCase CLR self slot (#182).
    static bool IsComparableSelfCall(Type type, TypeNode.Fqn owner, JsonObject call)
    {
        var sig = call["sig"] as JsonArray
            ?? call["shapeTypes"] as JsonArray
            ?? call["argTypes"] as JsonArray;
        if (sig is not { Count: 1 } || sig[0] is not JsonNode argNode
            || TypeJson.Read(argNode) is not TypeNode argType
            || NormSigTv(argType) is not TypeNode.Fqn arg
            || arg != owner)
            return false;
        try
        {
            return type.GetInterfaces().Any(i =>
            {
                if (!i.IsGenericType || i.GetGenericTypeDefinition().FullName != "System.IComparable`1") return false;
                var target = i.GetGenericArguments()[0];
                if (target == type) return true;
                if (!type.IsGenericTypeDefinition || !target.IsGenericType
                    || target.GetGenericTypeDefinition() != type) return false;
                return target.GetGenericArguments().SequenceEqual(type.GetGenericArguments());
            });
        }
        catch { return false; }
    }

    // ---- #178 Regex(options) ctor-arg conversion -----------------------------------------------

    const string RegexFqn = "kotlin.text.Regex";
    const string RegexOptionFqn = "kotlin.text.RegexOption";
    const string ClrRegexOptionsFqn = "kotlin.text.ClrRegexOptions";  // @ClrTypeAlias -> System...RegexOptions

    // The RegexOption ordinal -> System.Text.RegularExpressions.RegexOptions [Flags] bit, the exact INVERSE of
    // RegexClr.kt `nativeOptionsBits`. RegexOption's declaration order is IGNORE_CASE(0), MULTILINE(1), LITERAL(2),
    // UNIX_LINES(3), COMMENTS(4), DOT_MATCHES_ALL(5), CANON_EQ(6); only the four with a direct .NET bit appear — the
    // three .NET-unrepresentable options (LITERAL/UNIX_LINES/CANON_EQ) map to no bit (they encode to 0, the documented
    // CLR symmetry with the decode side, which never round-trips them either).
    static readonly (int Ordinal, int Bit)[] RegexOptionBits =
    {
        (0, 1),    // IGNORE_CASE      -> RegexOptions.IgnoreCase
        (1, 2),    // MULTILINE        -> RegexOptions.Multiline
        (5, 16),   // DOT_MATCHES_ALL  -> RegexOptions.Singleline
        (4, 32),   // COMMENTS         -> RegexOptions.IgnorePatternWhitespace
    };

    static int _rxCounter;

    // #178: `new kotlin.text.Regex(pattern, RegexOption)` / `new kotlin.text.Regex(pattern, Set<RegexOption>)` -> the
    // options arg is SYNTHESIZED into the OR'd RegexOptions bitmask (an Int) and its declared arg type is retyped to
    // `kotlin.text.ClrRegexOptions` (the @ClrTypeAlias twin of System...RegexOptions), so after BirTypeLowering the arg
    // is a `System...RegexOptions` Int that ClrMemberResolution matches EXACTLY against the BCL `Regex(String,
    // RegexOptions)` ctor. Runs before MemberCallSubstitution/BirTypeLowering (pure-Kotlin tokens) so the synthesized
    // `contains`/enum nodes lower through the ordinary passes. A no-op for any non-Regex `new` or non-option ctor.
    static void ReshapeRegexCtorOptions(JsonObject node)
    {
        if (TypeJson.Read(node["type"]) is not TypeNode.Fqn owner || owner.Name != RegexFqn) return;
        if (node["argTypes"] is not JsonArray argTypes || argTypes.Count != 2) return;
        if (node["args"] is not JsonArray args || args.Count != 2) return;
        var optType = TypeJson.Read(argTypes[1]);
        var single = optType is TypeNode.Fqn f1 && f1.Args == null && f1.Name == RegexOptionFqn;
        var set = optType is TypeNode.Fqn fs && fs.Name == "kotlin.collections.Set"
                  && fs.Args is { Length: 1 } sa && sa[0] is TypeNode.Fqn se && se.Name == RegexOptionFqn;
        if (!single && !set) return;

        var setType = argTypes[1];                 // the declared Set<RegexOption> type (needed before we retype it)
        var optArg = args[1];
        args.RemoveAt(1);                          // detach the option arg; the converted Int expr re-appends at index 1
        args.Add(single ? SingleOptionBits(optArg) : SetOptionBits(optArg, setType));
        argTypes[1] = TypeJson.Fqn(ClrRegexOptionsFqn);
    }

    // A single `RegexOption` value -> its RegexOptions bit: `d = ordinalOf(opt); d==0 ? 1 : d==1 ? 2 : d==5 ? 16 :
    // d==4 ? 32 : 0`. The ordinal is bound to a temp (valueBlock) so a side-effecting option expression runs exactly
    // once. `enumOrdinal` without a `type` lowers to Conv_I4 — RegexOption's underlying value == its ordinal (kotc emits
    // contiguous 0..n), so the bare underlying int IS the ordinal.
    static JsonNode SingleOptionBits(JsonNode optArg)
    {
        var name = "__rxopt$" + System.Threading.Interlocked.Increment(ref _rxCounter);
        var decl = new JsonObject
        {
            ["k"] = "var",
            ["name"] = name,
            ["type"] = TypeJson.Fqn("kotlin.Int"),
            ["init"] = new JsonObject { ["k"] = "enumOrdinal", ["e"] = optArg },
        };
        JsonNode chain = ConstInt(0);
        for (int i = RegexOptionBits.Length - 1; i >= 0; i--)
        {
            var (ord, bit) = RegexOptionBits[i];
            chain = new JsonObject
            {
                ["k"] = "cond",
                ["type"] = TypeJson.Fqn("kotlin.Int"),
                ["cond"] = new JsonObject
                {
                    ["k"] = "binOp",
                    ["op"] = "==",
                    ["lhs"] = new JsonObject { ["k"] = "local", ["name"] = name },
                    ["rhs"] = ConstInt(ord),
                },
                ["then"] = ConstInt(bit),
                ["else"] = chain,
            };
        }
        return new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { decl }, ["result"] = chain };
    }

    // A `Set<RegexOption>` -> the OR'd RegexOptions bitmask: `(IGNORE_CASE in s ? 1:0) | (MULTILINE in s ? 2:0) |
    // (DOT_MATCHES_ALL in s ? 16:0) | (COMMENTS in s ? 32:0)`, with the set bound to a temp so it evaluates once. Each
    // `X in s` is the plain Kotlin `Set.contains(RegexOption)` call (MemberCallSubstitution binds it off the ref.dll,
    // exactly as a user `in`) with the option as a compile-time `enumValue` constant.
    static JsonNode SetOptionBits(JsonNode setArg, JsonNode setType)
    {
        var name = "__rxopts$" + System.Threading.Interlocked.Increment(ref _rxCounter);
        var decl = new JsonObject
        {
            ["k"] = "var",
            ["name"] = name,
            ["type"] = setType.DeepClone(),
            ["init"] = setArg,
        };
        JsonNode result = null;
        foreach (var (ord, bit) in RegexOptionBits)
        {
            var test = new JsonObject
            {
                ["k"] = "cond",
                ["type"] = TypeJson.Fqn("kotlin.Int"),
                ["cond"] = ContainsOption(name, ord, setType),
                ["then"] = ConstInt(bit),
                ["else"] = ConstInt(0),
            };
            result = result == null
                ? (JsonNode)test
                : new JsonObject { ["k"] = "binOp", ["op"] = "|", ["lhs"] = result, ["rhs"] = test };
        }
        return new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { decl }, ["result"] = result };
    }

    // `RegexOption.<entry at ordinal> in s` = `s.contains(enumValue)` — the exact node kotc emits for a Set `in` check
    // (virtual contains(T), the type-var sig, the Collection.contains override marker), so it binds identically.
    static JsonObject ContainsOption(string setLocal, int ordinal, JsonNode setType) => new()
    {
        ["k"] = "callInstance",
        ["ownerType"] = setType.DeepClone(),
        ["virtual"] = true,
        ["recv"] = new JsonObject { ["k"] = "local", ["name"] = setLocal },
        ["method"] = "contains",
        ["sig"] = new JsonArray { new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = 0 } },
        ["ret"] = TypeJson.Fqn("kotlin.Boolean"),
        ["args"] = new JsonArray { new JsonObject { ["k"] = "enumValue", ["type"] = TypeJson.Fqn(RegexOptionFqn), ["ordinal"] = ordinal } },
        ["overrides"] = new JsonArray
        {
            new JsonObject { ["owner"] = TypeJson.Fqn("kotlin.collections.Collection"), ["member"] = "contains", ["kind"] = "method", ["arity"] = 1 },
        },
    };

    static JsonObject ConstInt(int v) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = v };
}
