using System.Text.Json;
using DotKt.Cir;

namespace DotKt.Bir;

// Typed declaration/scope input to the SAME invariants. Executable checks still use the explicitly retained
// reader-side body adapter; no typed declaration is serialized back to a JSON document for validation.
public static partial class IrSanity
{
    public static void Check(IEnumerable<CirFileDeclaration> files, IrSanityChecks which = IrSanityChecks.All)
    {
        foreach (var file in files)
        {
            CheckCirContainer(file.FileClass, file, false, which);
            foreach (var type in file.Types)
            {
                if (which == IrSanityChecks.All) CheckCollectionViewFaces(type.Name, type.Interfaces.OrEmpty());
                CheckCirContainer(type.Name, type, type.Kind == CirTypeKind.Interface, which);
            }
        }
    }

    static string PosPrefix(OptionalField<CirSourcePosition> position)
    {
        if (!position.HasValue) return "";
        var pos = position.Value;
        return Path.GetFileName(pos.F) + (pos.L.HasValue && pos.L.Value >= 0 ? ":" + pos.L.Value : "") + ": ";
    }

    static HashSet<string> ParamNames(CirParameter[] parameters) => parameters.Select(p => p.Name.OrDefault())
        .Where(name => !string.IsNullOrEmpty(name)).ToHashSet(StringComparer.Ordinal);

    static void CheckCirContainer(string owner, ICirContainer container, bool isInterface, IrSanityChecks which)
    {
        foreach (var method in container.DeclaredMethods)
        {
            var label = PosPrefix(method.Pos) + owner + "." + method.Name;
            if (which != IrSanityChecks.StyStampsOnly)
            {
                CheckSuspendDecl(label, method.Mods.OrDefault()?.Suspend.OrDefault() == true);
                if (method.Pinvoke.HasValue) CheckCirPInvoke(method, label);
            }
            if (method.Abstract.OrDefault() || !method.Body.HasValue) continue;
            var body = method.Body.Value.Element;
            if (body.ValueKind == JsonValueKind.Array)
                CheckScope(owner + "." + method.Name, ParamNames(method.Params), [body], PosPrefix(method.Pos), which);
        }
        foreach (var ctor in container.DeclaredConstructors)
        {
            var body = ctor.Body.Element;
            if (body.ValueKind != JsonValueKind.Array) continue;
            var roots = new List<JsonElement> { body };
            Add(ctor.PreStmts.OrDefault()); Add(ctor.ThisArgs.OrDefault()); Add(ctor.BaseArgs.OrDefault());
            CheckScope(owner + "..ctor", ParamNames(ctor.Params), roots, PosPrefix(ctor.Pos), which);
            void Add(TemporaryCirExecutable tree)
            {
                if (tree != null && tree.Element.ValueKind == JsonValueKind.Array) roots.Add(tree.Element);
            }
        }
        if (!isInterface)
        {
            var inits = container.DeclaredFields.Where(field => field.Static.OrDefault() && field.Init.HasValue)
                .Select(field => field.Init.Value.Element).Where(init => init.ValueKind != JsonValueKind.Null).ToList();
            if (inits.Count > 0) CheckScope(owner + "..cctor", null, inits, PosPrefix(container.Pos), which);
        }
    }

    static void CheckCirPInvoke(CirMethod method, string label)
    {
        var descriptor = method.Pinvoke.Value;
        CheckPInvokeOwner(label, !method.Extern.OrDefault() || !method.Static ||
            method.TypeParams.IsPresent && (!method.TypeParams.HasValue || method.TypeParams.Value.Length != 0) ||
            !method.Body.HasValue || method.Body.Value.Element.ValueKind != JsonValueKind.Array ||
            method.Body.Value.Element.GetArrayLength() != 0 || method.Mods.OrDefault()?.External.OrDefault() == true);
        CheckPInvokeComplete(label, !string.IsNullOrEmpty(descriptor.Module) && descriptor.EntryPoint != null &&
            !string.IsNullOrEmpty(descriptor.CallingConvention) && !string.IsNullOrEmpty(descriptor.CharSet));
        CheckPInvokeFields(label, method.Name, descriptor.PseudoFields.ToHashSet(StringComparer.Ordinal),
            descriptor.EntryPoint, descriptor.CharSet, descriptor.ExactSpelling, descriptor.SetLastError,
            descriptor.PreserveSig, descriptor.BestFitMapping, descriptor.ThrowOnUnmappableChar);
    }
}
