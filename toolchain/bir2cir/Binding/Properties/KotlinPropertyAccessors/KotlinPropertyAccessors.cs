using System;
using System.Text.Json.Nodes;

// Kotlin property accessor identity is an explicit BIR fact.  This is the sole forward projection from that
// identity to the dedicated CLR accessor spelling. No caller may parse
// the resulting method name to recover the property or accessor role.
static partial class KotlinPropertyAccessors
{

    internal const string SourceNameKey = "propertyName";
    internal const string KindKey = "propertyAccessor";
    internal const string PropertyRolesKey = "kotlinAccessors";
    internal const string AssociationKey = "propertyAssociation";
    // Frontend-selected Kotlin override facts. bir2cir consumes these while allocating CLR slots and never attempts
    // to recover the selected implementation from hierarchy bodies or physical names.
    internal const string InheritedImplementationKey = "inheritedImplementation";
    internal const string InheritedDefaultAccessorsKey = "inheritedDefaultAccessors";
    internal const string InheritedDefaultMethodsKey = "inheritedDefaultMethods";
    // Suspend lowering projects one Kotlin declaration into a public Task method and a continuation cold entry.
    // These bir2cir-only carriers preserve the frontend declaration signature on each physical projection so later
    // slot allocation can consume the selected declaration identity without reconstructing it from either ABI shape.
    internal const string SuspendSourceParamsKey = "suspendSourceParams";
    internal const string SuspendSourceRetKey = "suspendSourceRet";
    // A bare result erasure on a logical suspend override becomes a nested Task<R> erasure after suspend lowering.
    // The early slot pass states that required physical Task result explicitly so the generated TCS/body and the
    // eventual MethodDef signature are authored together; the late pass must never rewrite only the signature.
    internal const string SuspendTaskResultKey = "suspendTaskResult";
    // This adapter fills only the hot Task slot. The original declaration's cold override remains owned by the
    // ordinary final slot pass; mapping both onto a base-class cold slot would redispatch the adapter to itself.
    internal const string SuspendTaskOnlyBridgeKey = "suspendTaskOnlyBridge";
    // A declaration synthesized solely to carry a CLR MethodImpl is not a Kotlin declaration candidate. MethodImpl
    // descriptors may also live directly on a source accessor when only its physical name differs from an external
    // property slot, so descriptor presence alone cannot distinguish the two roles.
    internal const string PhysicalSlotBridgeKey = "physicalSlotBridge";
    // CIR instruction: a private forwarding MethodDef declared on an interface is the final body of an explicit
    // MethodImpl. The CLR requires that interface MethodImpl body to be final; ilemit maps this fact one-to-one.
    internal const string ClrInterfaceSlotBridgeKey = "clrInterfaceSlotBridge";
    // A CLR Property signature cannot own method generic parameters. Generic top-level extension properties therefore
    // carry their exact accessor association on the MethodDef instead of emitting an invalid Property row.
    internal const string MetadataCarrierKey = "kotlinPropertyAccessorCarrier";
    internal static string PhysicalName(string sourceName, string kind) => kind switch
    {
        "get" => "prop_get<" + sourceName + ">",
        "set" => "prop_set<" + sourceName + ">",
        _ => throw new InvalidOperationException($"invalid Kotlin property accessor role '{kind}'"),
    };

    internal static bool TryIdentity(JsonObject declaration, out string sourceName, out string kind)
    {
        sourceName = Str(declaration[SourceNameKey]);
        kind = Str(declaration[KindKey]);
        return HasIdentity(sourceName, kind);
    }

    internal static bool HasIdentity(string sourceName, string kind)
    {
        if (sourceName == null && kind == null) return false;
        if (sourceName == null || kind is not ("get" or "set"))
            throw new InvalidOperationException("incomplete Kotlin property accessor identity");
        return true;
    }

    // A MethodImpl bridge is a resolved CLR implementation detail, not another Kotlin declaration that a frontend-
    // selected property call may bind to.  It deliberately carries the source identity for MethodSemantics and
    // round-trip metadata, so every source-declaration index must exclude it by this explicit physical-role fact.
    internal static bool IsPhysicalSlotBridge(JsonObject declaration) =>
        IsPhysicalSlotBridge(declaration[PhysicalSlotBridgeKey] is JsonValue value
        && value.TryGetValue<bool>(out var physicalSlotBridge)
        && physicalSlotBridge);

    internal static bool IsPhysicalSlotBridge(bool physicalSlotBridge) => physicalSlotBridge;

    internal static void PreserveCallIdentity(JsonObject call, string sourceName, string kind)
    {
        ValidateCallIdentity(sourceName, kind);
        call[SourceNameKey] = sourceName;
        call[KindKey] = kind;
    }

    internal static bool IsCall(JsonObject call, string sourceName, string kind) =>
        IsCall(Str(call[SourceNameKey]), Str(call[KindKey]), Str(call["method"]), Str(call["prop"]), sourceName, kind);

    internal static bool IsCall(string preservedName, string preservedKind, string method, string prop,
        string sourceName, string kind) => preservedName == sourceName && preservedKind == kind
        || method == sourceName && prop == kind;

    internal static bool TryCallIdentity(string preservedName, string preservedKind, string method, string prop,
        out string sourceName, out string kind)
    {
        sourceName = preservedName;
        kind = preservedKind;
        if (sourceName != null || kind != null)
        {
            if (sourceName == null || kind is not ("get" or "set"))
                throw new InvalidOperationException("incomplete preserved Kotlin property call identity");
            return true;
        }
        sourceName = method;
        kind = prop;
        if (sourceName != null && kind is "get" or "set") return true;
        sourceName = null;
        kind = null;
        return false;
    }

    internal static void SetPhysicalName(JsonObject declaration, string physicalName)
    {
        if (!TryIdentity(declaration, out _, out _))
            throw new InvalidOperationException("cannot allocate a physical accessor name without Kotlin identity");
        declaration["name"] = physicalName;
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
