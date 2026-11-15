using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendResidueLowering
{
    // This entry runs only after both owning suspend materializers. The caller
    // owns the !RefBuild gate; the retained method declaration remains live.
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, bool appBuild,
        Action<BirGraphValue> retireDiscardedSubtree)
    {
        ArgumentNullException.ThrowIfNull(retireDiscardedSubtree);
        foreach (var file in roots)
        {
            Stub(file.Methods.OrEmpty(), file.FileClass.OrDefault() ?? "?");
            foreach (var type in file.Types.OrEmpty()) Stub(type.Methods.OrEmpty(), type.Name.OrDefault() ?? "?");
        }

        void Stub(IEnumerable<BirFunctionDeclaration> methods, string owner)
        {
            foreach (var method in methods)
            {
                if (method.Mods.OrDefault()?.Suspend.OrDefault() != true) continue;
                var name = method.Name.OrDefault() ?? "?";
                // The app invariant applies to declarations, including abstract
                // or absent-body ones; it precedes the body-writing decision.
                if (appBuild)
                    throw new NotSupportedException(
                        $"bir2cir: suspend-lowering: '{owner}.{name}' reached CIR still carrying the `suspend` modifier. "
                        + "Every suspend declaration in an application build is transformed into its cold Continuation "
                        + "shape (state machine + `$dotkt_suspend` cold entry + public Task bridge), and a shape the "
                        + "classifier refuses gets a call-time-throw COLD ENTRY rather than a retained original — so this "
                        + "is a cold-lowering MISS, not a supported residue.");
                if (method.Abstract.OrDefault() || !method.Body.HasValue) continue;
                // Each statement owns its complete discarded executable forest.
                // Report all of them while the original body is still attached,
                // so captured declarations retain exact continuation evidence.
                foreach (var statement in method.Body.Value) retireDiscardedSubtree((BirGraphValue)statement);
                method.Body = SourceThrowStubBody(
                    $"{owner}.{name}: this suspend declaration has no state-machine body — bir2cir's cold-core coroutine "
                    + "lowering leaves it un-lowered in a standard-library build. It is compile-time surface only and cannot be invoked.");
                method.BodyTerminates = true;
            }
        }
    }

    static IBirStatement[] SourceThrowStubBody(string message) => new IBirStatement[] {
        new BirThrow { Value = new BirNewClr {
            Type = new TypeNode.Fqn("System.NotSupportedException"),
            ArgTypes = new TypeNode[] { new TypeNode.Fqn("kotlin.String") },
            Args = new IBirExpression[] { new BirConstant { Type = new TypeNode.Fqn("kotlin.String"),
                Value = new BirLiteral.TextLiteral(message) } },
        } },
    };
}
