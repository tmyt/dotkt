using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClosureSynthesis
{
    internal readonly record struct CaptureContext(string Owner, string Path, int? Line);
    internal sealed record MaterializedOccurrence(BirGraphSlot Slot, CaptureContext Context);
    internal sealed record ClosureProvenance(string Name, BirGraphValue Construction, BirGraphValue Ingredients);
    internal sealed record MaterializedClosures(IReadOnlyList<BirClassDefinition> AppendedTypes,
        IReadOnlyList<ClosureProvenance> Provenance);

    internal static bool HasPreboundFrame(BirGraphValue source) => source switch {
        BirClosureDefinition value => value._syntheticFrameBound.HasValue,
        BirClassDefinition value => value._syntheticFrameBound.HasValue,
        _ => false,
    };

    internal static CaptureContext ContextFor(BirGraphValue node, CaptureContext fallback)
    {
        var (name, position, declaration) = node switch {
            BirFunctionDeclaration value => (value.Name.OrDefault(), value.Pos.OrDefault(), value.Body.HasValue),
            BirClassDefinition value => (value.Name.OrDefault(), value.Pos.OrDefault(), value.Methods.HasValue || value.Ctors.HasValue),
            BirInterfaceDefinition value => (value.Name.OrDefault(), value.Pos.OrDefault(), value.Methods.HasValue || value.Ctors.HasValue),
            BirEnumDefinition value => (value.Name.OrDefault(), value.Pos.OrDefault(), value.Methods.HasValue || value.Ctors.HasValue),
            _ => (null, (BirDeclarationPosition)null, false),
        };
        return name != null && declaration
            ? new CaptureContext(name, position?.F.OrDefault(), position?.L.HasValue == true ? position.L.Value : null) : fallback;
    }

    // This is the exact late-materialized capability, not the whole-file closure
    // pass. Slots explicitly publish cross-kind replacements before ingredients
    // are snapshotted; the saved provenance therefore has the normalized shape.
    internal static MaterializedClosures ApplyMaterialized(BirFileDeclaration file,
        IEnumerable<MaterializedOccurrence> inputs, ReferenceMetadataIndex refs,
        Action<BirFunctionDeclaration, NullableRepresentationFrame> projectSamMethod = null)
    {
        var provenance = new List<ClosureProvenance>();
        var appended = SynthesizeTyped(file, inputs, refs, projectSamMethod, provenance, skipAttributes: true);
        return new MaterializedClosures(appended, provenance);
    }

    // The main file pass shares the actual normalization and construction authority without paying for the
    // independent ingredients snapshots used only by prepared-default restoration.
    static IReadOnlyList<BirClassDefinition> SynthesizeTyped(BirFileDeclaration file,
        IEnumerable<MaterializedOccurrence> inputs, ReferenceMetadataIndex refs,
        Action<BirFunctionDeclaration, NullableRepresentationFrame> projectSamMethod,
        ICollection<ClosureProvenance> provenance, bool skipAttributes)
    {
        var pending = new List<BirClassDefinition>();
        foreach (var input in inputs) Walk(input.Slot, input.Context);
        var have = file.Types.OrEmpty().Select(type => type switch {
            BirClassDefinition value => value.Name.OrDefault(),
            BirInterfaceDefinition value => value.Name.OrDefault(),
            BirEnumDefinition value => value.Name.OrDefault(), _ => null,
        }).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var appended = pending.Where(type => !type.Name.HasValue || have.Add(type.Name.Value)).ToArray();
        if (appended.Length != 0) file.Types = file.Types.OrEmpty().Concat(appended).ToArray();
        return appended;

        void Children(BirGraphValue node, CaptureContext context)
            => node.VisitChildSlots(slot => { if (!skipAttributes || slot.Value is not BirAttribute) Walk(slot, context); });

        void Record(string name, IEnumerable<(string Name, TypeNode Type)> fields, CaptureContext context)
            => RecordCaptureLegality(name, fields, FieldLegality.PosPrefix(context.Path, context.Line), context.Owner, refs);

        void Walk(BirGraphSlot slot, CaptureContext context)
        {
            var node = slot.Value;
            if (node == null || node is IBirExecutablePayload) return;
            if (node is BirSamConversion conversion)
            {
                if (conversion.Type.OrDefault() is not TypeNode.Fqn target || refs?.IsClrDelegate(target) != true
                    || !conversion.E.HasValue)
                    throw new InvalidOperationException("SAM conversion has no projected CLR delegate target or operand");
                if (ClrMemberResolution.MarkDelegateSlot((BirGraphValue)conversion.E.Value, target, refs, new HashSet<string>()))
                {
                    node = (BirGraphValue)conversion.E.Value;
                    slot.Replace(node);
                }
            }
            if (node is BirNewSam { SynthClass.HasValue: true } delegateSam
                && LowerClrDelegateSam(delegateSam, refs) is { } lowered)
            {
                node = lowered;
                slot.Replace(lowered);
            }
            if (node is BirNewClosure { SynthClass.HasValue: true } closure)
            {
                var source = closure.SynthClass.Value;
                provenance?.Add(new ClosureProvenance(source.Name.OrDefault(), closure, source.DeepClone()));
                if (closure.Captures.HasValue)
                    for (var index = 0; index < closure.Captures.Value.Length; index++)
                    {
                        var at = index;
                        Walk(new BirGraphSlot(() => (BirGraphValue)closure.Captures.Value[at],
                            value => closure.Captures.Value[at] = (IBirExpression)value), context);
                    }
                WalkBody(source.Body, context);
                Record(source.Name.OrDefault(), source.Fields.OrEmpty().Where(field => field.Type.HasValue)
                    .Select(field => (field.Name.OrDefault(), field.Type.Value)), context);
                pending.Add(BuildClosureClass((BirClosureDefinition)TypedExecutableFrames.PrepareSynthetic(source, closure.TypeArgs.OrDefault())));
                closure.SynthClass = default;
                return;
            }
            if (node is BirNewSam { SynthClass.HasValue: true } sam)
            {
                var source = sam.SynthClass.Value;
                provenance?.Add(new ClosureProvenance(source.Name.OrDefault(), sam, source.DeepClone()));
                // Preserve the existing ordinary-SAM branch's body-only traversal. It does not use the
                // generic child walk (or the closure branch's separate capture-expression traversal).
                foreach (var method in source.Methods.OrEmpty()) WalkBody(method.Body, context);
                Record(source.Name.OrDefault(), source.Fields.OrEmpty().Where(field => field.Type.HasValue)
                    .Select(field => (field.Name.OrDefault(), field.Type.Value)), context);
                var declaration = (BirClassDefinition)TypedExecutableFrames.PrepareSynthetic(source, sam.TypeArgs.OrDefault());
                var ownerFrame = declaration.KotlinSupertypesPre.OrDefault()?.NullableFrame;
                foreach (var method in declaration.Methods.OrEmpty()) projectSamMethod?.Invoke(method, ownerFrame);
                pending.Add(declaration);
                sam.SynthClass = default;
                return;
            }
            Children(node, ContextFor(node, context));
        }

        void WalkBody(OptionalField<IBirStatement[]> body, CaptureContext context)
        {
            if (!body.HasValue) return;
            for (var index = 0; index < body.Value.Length; index++)
            {
                var at = index;
                Walk(new BirGraphSlot(() => (BirGraphValue)body.Value[at], value => body.Value[at] = (IBirStatement)value), context);
            }
        }
    }

    static BirNewClosure LowerClrDelegateSam(BirNewSam node, ReferenceMetadataIndex refs)
    {
        var source = node.SynthClass.Value;
        if (ProjectedDelegateSamTarget(source.Interfaces.OrEmpty(), source.Methods.OrEmpty().Length, refs) is not { } target
            || source.Methods.OrEmpty() is not [var invoke] || !node.SamType.HasValue
            || invoke.Params.OrEmpty().Any(parameter => !parameter.Type.HasValue) || !invoke.Ret.HasValue) return null;
        var closure = new BirNewClosure {
            ClosureType = node.SamType, Captures = node.Captures, Method = "invoke", TypeArgs = node.TypeArgs,
            FuncType = new TypeNode.Fn(false, invoke.Ret.Value, invoke.Params.OrEmpty().Select(parameter => parameter.Type.Value).ToArray()),
            SynthClass = new BirClosureDefinition {
                Name = source.Name, Fields = source.Fields.OrEmpty().Select(ToCapture).ToArray(),
                Params = invoke.Params.OrEmpty().Select(ToLambdaParameter).ToArray(), Ret = invoke.Ret,
                Body = invoke.Body.HasValue ? invoke.Body : Array.Empty<IBirStatement>(),
                TypeParams = source.TypeParams, SemanticOwner = source.SemanticOwner,
                OuterTypeParamCount = source.OuterTypeParamCount, OuterTypeParamOffset = source.OuterTypeParamOffset,
                _syntheticFrameBound = source._syntheticFrameBound,
            },
        };
        ClrMemberResolution.MarkDelegateSlot(closure, target, refs, new HashSet<string>());
        return closure;
    }

    internal static BirCaptureDescriptor ToCapture(BirFieldDeclaration value) => new() {
        Name = value.Name, Type = value.Type, Outer = value.Outer, SuspendFnTypePre = value.SuspendFnTypePre,
        KotlinType = value.KotlinType, NullableGeneric = value.NullableGeneric, NullableFlags = value.NullableFlags,
    };

    internal static BirCaptureDescriptor ToCapture(BirParameter value) => new() {
        Name = value.Name, Type = value.Type, Outer = value.Outer, SuspendFnTypePre = value.SuspendFnTypePre,
        KotlinType = value.KotlinType, NullableGeneric = value.NullableGeneric, NullableFlags = value.NullableFlags,
    };

    internal static BirLambdaParameter ToLambdaParameter(BirParameter value) => new() {
        Name = value.Name, Type = value.Type, SuspendFnTypePre = value.SuspendFnTypePre,
        Outer = value.Outer, Mods = value.Mods, Default = value.Default, DefaultBir = value.DefaultBir,
        CtxFnType = value.CtxFnType, SharedCellType = value.SharedCellType, Attrs = value.Attrs,
        FunctionSignatureType = value.FunctionSignatureType, KotlinType = value.KotlinType,
        NullableGeneric = value.NullableGeneric, NullableFlags = value.NullableFlags,
        NominalParameterSignatureIdentity = value.NominalParameterSignatureIdentity,
        GenericParameterSignatureBounds = value.GenericParameterSignatureBounds,
    };

    internal static BirParameter ToParameter(BirCaptureDescriptor value) => new() {
        Name = value.Name, Type = value.Type, Outer = value.Outer, SuspendFnTypePre = value.SuspendFnTypePre,
        KotlinType = value.KotlinType, NullableGeneric = value.NullableGeneric, NullableFlags = value.NullableFlags,
    };

    internal static BirParameter ToParameter(BirLambdaParameter value) => new() {
        Name = value.Name, Type = value.Type, SuspendFnTypePre = value.SuspendFnTypePre,
        Outer = value.Outer, Mods = value.Mods, Default = value.Default, DefaultBir = value.DefaultBir,
        CtxFnType = value.CtxFnType, SharedCellType = value.SharedCellType, Attrs = value.Attrs,
        FunctionSignatureType = value.FunctionSignatureType, KotlinType = value.KotlinType,
        NullableGeneric = value.NullableGeneric, NullableFlags = value.NullableFlags,
        NominalParameterSignatureIdentity = value.NominalParameterSignatureIdentity,
        GenericParameterSignatureBounds = value.GenericParameterSignatureBounds,
    };

    static BirClassDefinition BuildClosureClass(BirClosureDefinition source)
    {
        var self = new TypeNode.Fqn(source.Name.OrDefault());
        var fields = source.Fields.OrEmpty();
        var body = source.Body.OrEmpty().Select(statement => (IBirStatement)((BirGraphValue)statement).DeepClone()).ToArray();
        var fieldTypes = new Dictionary<string, BirCaptureDescriptor>(StringComparer.Ordinal);
        foreach (var field in fields)
            if (field.Name.HasValue && field.Type.HasValue) fieldTypes[field.Name.Value] = field;
        void Stamp(BirGraphValue node)
        {
            if (node is BirFieldGet { Name.HasValue: true } read && !read.Sty.HasValue
                && read.Recv.OrDefault() is BirThis && fieldTypes.TryGetValue(read.Name.Value, out var field))
            {
                read.Sty = BirGraphValue.CopyType(field.Type.Value);
                if (field.Outer.OrDefault()) read.Outer = true;
            }
            if (node is BirNewClosure nestedClosure)
            {
                foreach (var capture in nestedClosure.Captures.OrEmpty()) Stamp((BirGraphValue)capture);
                return;
            }
            if (node is BirNewSam nestedSam)
            {
                foreach (var capture in nestedSam.Captures.OrEmpty()) Stamp((BirGraphValue)capture);
                return;
            }
            node.VisitChildren(Stamp);
        }
        foreach (var statement in body) Stamp((BirGraphValue)statement);
        var declaration = new BirClassDefinition {
            Name = source.Name, Kind = "class", Generated = true, SemanticOwner = source.SemanticOwner,
            OuterTypeParamCount = source.OuterTypeParamCount, OuterTypeParamOffset = source.OuterTypeParamOffset,
            KotlinSupertypesPre = source.KotlinSupertypesPre,
            Base = OptionalField<TypeNode>.Null, Interfaces = Array.Empty<TypeNode>(),
            Fields = fields.Select(field => new BirFieldDeclaration {
                Name = field.Name, Type = field.Type, Outer = field.Outer, SuspendFnTypePre = field.SuspendFnTypePre,
                KotlinType = field.KotlinType, NullableGeneric = field.NullableGeneric, NullableFlags = field.NullableFlags,
            }).ToArray(),
            Ctors = new[] { new BirConstructor {
                Params = fields.Select(field => ToParameter((BirCaptureDescriptor)field.DeepClone())).ToArray(),
                BaseArgs = OptionalField<IBirExpression[]>.Null,
                Body = fields.Where(field => field.Name.HasValue).Select(field => (IBirStatement)new BirSetField {
                    OwnerType = self, Recv = new BirThis(), Name = field.Name,
                    Value = new BirLocal { Name = field.Name,
                        Outer = field.Outer.OrDefault() ? OptionalField<bool>.Of(true) : default },
                    Outer = field.Outer.OrDefault() ? OptionalField<bool>.Of(true) : default,
                }).ToArray(),
            } },
            Methods = new[] { new BirFunctionDeclaration {
                Name = "invoke", Static = false, Override = false, Virtual = false,
                Params = source.Params.OrEmpty().Select(parameter => ToParameter((BirLambdaParameter)parameter.DeepClone())).ToArray(),
                Ret = source.Ret, Body = body,
            } },
        };
        if (source.TypeParams.OrEmpty().Length != 0) declaration.TypeParams = source.TypeParams;
        return declaration;
    }
}
