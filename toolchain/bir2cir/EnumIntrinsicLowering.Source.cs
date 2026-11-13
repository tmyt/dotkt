using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class EnumIntrinsicLowering
{
    internal static void Apply(BirFileDeclaration file, ISet<string> richEnums, ISet<string> localTopLevelFns,
        bool appBuild, ReferenceMetadataIndex refs, IReadOnlyDictionary<string, NullableRepresentationFrame> methodFrames)
    {
        Walk(file);
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            node = Rewrite(node);
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
        BirGraphValue Rewrite(BirGraphValue node)
        {
            var semantic = node switch {
                BirEnumValues values => values.Type.OrDefault() as TypeNode.Fqn,
                BirEnumParse parse => parse.Type.OrDefault() as TypeNode.Fqn,
                _ => null,
            };
            if (semantic != null)
            {
                var values = "values";
                var valueOf = "valueOf";
                if (richEnums.Contains(semantic.Name) || refs.TryKotlinRichEnumStaticApis(semantic.Name, out values, out valueOf))
                    return RichEnumCall(semantic, node is BirEnumParse ? valueOf : values,
                        node is BirEnumParse parse ? parse.Arg.OrDefault() : null);
            }
            if (node is not BirCallStatic call || call.Owner.HasValue || !call.Method.HasValue
                || !Names.Contains(call.Method.Value) || !call.TypeArgs.HasValue) return node;
            var identity = call.DeclarationId.OrDefault();
            var frame = identity == null ? null : methodFrames.GetValueOrDefault(identity) ?? refs.NullableMethodFrame(identity);
            var argument = SourceArgument(call.TypeArgs.Value, frame);
            if (argument == null) return node;
            var valueOfCall = call.Method.Value == "enumValueOf";
            var arguments = call.Args.OrEmpty();
            if (arguments.Length != (valueOfCall ? 1 : 0) || appBuild && localTopLevelFns.Contains(call.Method.Value)) return node;
            var richName = (argument as TypeNode.Fqn)?.Name;
            var localRich = richName != null && richEnums.Contains(richName);
            string mappedValues = null, mappedValueOf = null;
            var referencedRich = richName != null && refs.TryKotlinRichEnumStaticApis(richName, out mappedValues, out mappedValueOf);
            if (localRich || referencedRich)
                return RichEnumCall(argument, valueOfCall ? referencedRich ? mappedValueOf : "valueOf"
                    : referencedRich ? mappedValues : "values", valueOfCall ? arguments[0] : null);
            return valueOfCall
                ? new BirEnumParse { Type = BirGraphValue.CopyType(argument),
                    Arg = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)arguments[0]).DeepClone()) }
                : new BirEnumValues { Type = BirGraphValue.CopyType(argument) };
        }
    }

    // The selected declaration frame, not a use-site physical index, identifies the single source argument.
    static TypeNode SourceArgument(TypeNode[] arguments, NullableRepresentationFrame frame)
        => frame == null ? arguments.Length == 1 ? arguments[0] : null
            : frame.SourceArity == 1 && arguments.Length == frame.PhysicalArity
                ? arguments[frame.Variable(new TypeNode.Tv("method", 0), NullableRepresentationFrame.Role.Ordinary).I]
                : null;

    static BirCallStatic RichEnumCall(TypeNode owner, string method, IBirExpression valueOfArgument)
    {
        var valueOf = valueOfArgument != null;
        return new BirCallStatic {
            Owner = BirGraphValue.CopyType(owner), Method = method,
            Ret = valueOf ? BirGraphValue.CopyType(owner) : new TypeNode.Array(BirGraphValue.CopyType(owner)),
            Args = valueOf ? new[] { (IBirExpression)((BirGraphValue)valueOfArgument).DeepClone() } : Array.Empty<IBirExpression>(),
            Sig = valueOf ? new TypeNode[] { new TypeNode.Fqn("System.String") } : Array.Empty<TypeNode>(),
        };
    }
}
