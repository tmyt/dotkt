using DotKt.Cir;

namespace DotKt.Bir;

// Physical declarations and executable children are checked directly. Source-only
// suspension/stamp fields cannot inhabit this graph; the source checker owns them.
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
            CheckCirScope(owner + "." + method.Name, ParamNames(method.Params), [method.Body.Value], PosPrefix(method.Pos), which);
        }
        foreach (var ctor in container.DeclaredConstructors)
        {
            var roots = new List<ICirNode[]> { ctor.Body };
            if (ctor.PreStmts.HasValue) roots.Add(ctor.PreStmts.Value);
            if (ctor.ThisArgs.HasValue) roots.Add(ctor.ThisArgs.Value);
            if (ctor.BaseArgs.HasValue) roots.Add(ctor.BaseArgs.Value);
            CheckCirScope(owner + "..ctor", ParamNames(ctor.Params), roots, PosPrefix(ctor.Pos), which);
        }
        if (!isInterface)
        {
            var inits = container.DeclaredFields.Where(field => field.Static.OrDefault() && field.Init.HasValue)
                .Select(field => new ICirNode[] { field.Init.Value }).ToList();
            if (inits.Count > 0) CheckCirScope(owner + "..cctor", null, inits, PosPrefix(container.Pos), which);
        }
    }

    static void CheckCirScope(string declaration, HashSet<string> parameters, List<ICirNode[]> roots,
        string position, IrSanityChecks which)
    {
        if (which == IrSanityChecks.StyStampsOnly) return;
        var label = position + declaration;
        var declared = parameters != null ? new HashSet<string>(parameters) : new HashSet<string>();
        var labels = new HashSet<int>();
        foreach (var root in roots)
        {
            var seen = new HashSet<int>();
            foreach (var node in root.SelectMany(CirExecutableWalk.DescendantsAndSelf))
            {
                switch (node)
                {
                    case CirVariable variable: declared.Add(variable.Name); break;
                    case CirFor loop: declared.Add(loop.Var); break;
                    case CirForArray loop: declared.Add(loop.Var); break;
                    case CirForRange loop: declared.Add(loop.Var); break;
                    case CirForEach loop: declared.Add(loop.Var); break;
                    case CirRepeat loop: declared.Add(loop.Var); break;
                    case CirTry attempt:
                        foreach (var clause in attempt.Catches)
                            if (clause.Var.HasValue) declared.Add(clause.Var.Value);
                        break;
                    case CirLabel anchor:
                        labels.Add(anchor.Id);
                        CheckDuplicateLabel(label, anchor.Id, seen);
                        break;
                }
            }
        }
        foreach (var root in roots)
            foreach (var node in root.SelectMany(CirExecutableWalk.DescendantsAndSelf))
            {
                switch (node)
                {
                    case CirLocal local: CheckKnownLocal(label, local.Tag, local.Name, declared); break;
                    case CirSetLocal store: CheckKnownLocal(label, store.Tag, store.Name, declared); break;
                    case CirByRefLoad load when load.Local.HasValue:
                        CheckKnownLocal(label, load.Tag, load.Local.Value, declared); break;
                    case CirByRefStore store when store.Local.HasValue:
                        CheckKnownLocal(label, store.Tag, store.Local.Value, declared); break;
                    case CirGoto branch: CheckKnownLabel(label, branch.Tag, branch.Id, labels); break;
                    case CirBranchIf branch: CheckKnownLabel(label, branch.Tag, branch.Id, labels); break;
                    case CirFor loop: CheckLoopComparison(label, loop.Cmp); break;
                    case CirLateinitGet read when !read.Value.HasValue && !read.OwnerType.HasValue:
                        throw new IrSanityException(label, "'lateinitGet' is missing a non-null 'ownerType'");
                    case CirCallStatic call when call.Owner.IsNull && !call.CalleeOwner.HasValue:
                        throw new IrSanityException(label, "'callStatic' with owner:null is missing required 'calleeOwner'");
                }
            }
    }

    static void CheckCirPInvoke(CirMethod method, string label)
    {
        var descriptor = method.Pinvoke.Value;
        CheckPInvokeOwner(label, !method.Extern.OrDefault() || !method.Static ||
            method.TypeParams.IsPresent && (!method.TypeParams.HasValue || method.TypeParams.Value.Length != 0) ||
            !method.Body.HasValue || method.Body.Value.Length != 0 || method.Mods.OrDefault()?.External.OrDefault() == true);
        CheckPInvokeComplete(label, !string.IsNullOrEmpty(descriptor.Module) && descriptor.EntryPoint != null &&
            !string.IsNullOrEmpty(descriptor.CallingConvention) && !string.IsNullOrEmpty(descriptor.CharSet));
        CheckPInvokeFields(label, method.Name, descriptor.PseudoFields.ToHashSet(StringComparer.Ordinal),
            descriptor.EntryPoint, descriptor.CharSet, descriptor.ExactSpelling, descriptor.SetLastError,
            descriptor.PreserveSig, descriptor.BestFitMapping, descriptor.ThrowOnUnmappableChar);
    }
}
