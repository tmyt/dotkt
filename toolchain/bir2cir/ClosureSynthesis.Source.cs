using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClosureSynthesis
{
    internal static void Apply(BirFileDeclaration file, ReferenceMetadataIndex refs,
        Action<BirFunctionDeclaration, NullableRepresentationFrame> projectSamMethod)
    {
        var inputs = new List<MaterializedOccurrence>();
        if (file.Methods.HasValue)
            for (var index = 0; index < file.Methods.Value.Length; index++)
            {
                var at = index;
                inputs.Add(new(new BirGraphSlot(() => file.Methods.Value[at], value => file.Methods.Value[at] = (BirFunctionDeclaration)value),
                    SourceInputContext(file.Methods.Value[at])));
            }
        if (file.Fields.HasValue)
            for (var index = 0; index < file.Fields.Value.Length; index++)
            {
                var at = index;
                inputs.Add(new(new BirGraphSlot(() => file.Fields.Value[at], value => file.Fields.Value[at] = (BirFieldDeclaration)value),
                    SourceInputContext(file.Fields.Value[at])));
            }
        if (file.Types.HasValue)
            for (var index = 0; index < file.Types.Value.Length; index++)
            {
                var at = index;
                inputs.Add(new(new BirGraphSlot(() => (BirGraphValue)file.Types.Value[at], value => file.Types.Value[at] = (IBirTypeDeclaration)value),
                    SourceInputContext((BirGraphValue)file.Types.Value[at])));
            }
        SynthesizeTyped(file, inputs, refs, projectSamMethod, null, skipAttributes: false);
        foreach (var type in file.Types.OrEmpty())
            RejectDirectClrDelegateImplementation(type.Name.OrDefault(), type.Interfaces.OrEmpty(), refs);
    }

    // Main inputs are themselves declarations even when they have no body. In particular, a field initializer
    // starts in that field's source context; nested declarations use the shared ContextFor scope policy.
    static CaptureContext SourceInputContext(BirGraphValue node)
    {
        var (name, position) = node switch {
            BirFunctionDeclaration value => (value.Name.OrDefault(), value.Pos.OrDefault()),
            BirFieldDeclaration value => (value.Name.OrDefault(), value.Pos.OrDefault()),
            BirClassDefinition value => (value.Name.OrDefault(), value.Pos.OrDefault()),
            BirInterfaceDefinition value => (value.Name.OrDefault(), value.Pos.OrDefault()),
            BirEnumDefinition value => (value.Name.OrDefault(), value.Pos.OrDefault()),
            _ => (null, (BirDeclarationPosition)null),
        };
        return new CaptureContext(name ?? "<file>", position?.F.OrDefault(), position?.L.HasValue == true ? position.L.Value : null);
    }

    static void RejectDirectClrDelegateImplementation(string typeName, IEnumerable<TypeNode> interfaces, ReferenceMetadataIndex refs)
    {
        foreach (var implemented in interfaces)
        {
            var candidate = implemented;
            while (candidate is TypeNode.Nullable nullable) candidate = nullable.Of;
            while (candidate is TypeNode.Oblivious oblivious) candidate = oblivious.Of;
            if (candidate is not TypeNode.Fqn named || !refs.IsClrDelegate(named)) continue;
            throw new InvalidOperationException(
                $"bir2cir: type '{typeName}' directly implements CLR delegate '{named.Name}'. " +
                "Projected CLR delegates may be constructed through their SAM constructor but cannot be " +
                "inherited or implemented as CLR interfaces");
        }
    }

    // Shape admission is the same for the owning typed prefix and real late DOM adapter consumers.
    static TypeNode.Fqn ProjectedDelegateSamTarget(IReadOnlyList<TypeNode> interfaces, int methodCount, ReferenceMetadataIndex refs)
        => refs != null && interfaces.Count == 1 && interfaces[0] is TypeNode.Fqn target
            && refs.IsClrDelegate(target) && methodCount == 1 ? target : null;
}
