using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

internal static class BirGraphMutationTests
{
    internal static void SelfTest()
    {
        TypeNode source = new TypeNode.Tv("method", 0);
        TypeNode target = new TypeNode.Fqn("kotlin.String");
        foreach (var origin in new BirGraphValue[] {
            new BirCallStatic(), new BirCallInstance(), new BirFieldGet(), new BirLocal(),
            new BirCast(), new BirValueBlock(), new BirConditional(),
        })
        {
            origin.ErasedValueVariableFact = source;
            var originDecoded = (BirGraphValue)BirGraphDom.ReadBirExpression(origin.ToDom());
            var originCopy = origin.DeepClone();
            originCopy.ErasedValueVariableFact = target;
            if (originDecoded.ErasedValueVariableFact.OrDefault() != source
                || origin.ErasedValueVariableFact.OrDefault() != source)
                throw new InvalidOperationException("Erased-value origin lost its explicit frame or independent occurrence");
        }
        var fieldArgument = new BirLocal { Name = "argument" };
        var fieldRegionOwner = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirExpressionStatement { Expr = fieldArgument } },
            Result = fieldArgument,
        };
        var regionCount = 0;
        fieldRegionOwner.VisitSourceChildren((_, _, _) => throw new InvalidOperationException("A source region escaped grouping"),
            region => {
                if (region.Statements.Length != 1 || !ReferenceEquals(region.Result?.Value, fieldArgument))
                    throw new InvalidOperationException("Field-aware source visitation changed region ownership");
                regionCount++;
            });
        if (regionCount != 1) throw new InvalidOperationException("Field-aware source region was not visited exactly once");
        var explicitType = new BirCallInstance { Sty = source, Ret = target, DynRet = new TypeNode.Fqn("Ignored") };
        Require(explicitType.ExplicitSourceType == source, "explicit source type prefers sty");
        explicitType.Sty = OptionalField<TypeNode>.Null;
        Require(explicitType.ExplicitSourceType == target, "explicit null stamp falls through to represented return");
        explicitType.Ret = default;
        Require(explicitType.ExplicitSourceType == null && new BirNew { Type = target }.ExplicitSourceType == target,
            "explicit source type reads construction type but never infers or uses dynRet");
        var local = new BirLocal { Name = "value", Sty = source };
        var arguments = new IBirExpression[] { local };
        var call = new BirCallStatic {
            Owner = source, Method = "read", Args = arguments, Ret = source,
            Sig = new[] { source }, TypeArgs = new[] { source },
            ResolvedMemberReturn = source, ResolvedMemberParams = new[] { source },
            Sty = OptionalField<TypeNode>.Null,
        };
        var statement = new BirExpressionStatement { Expr = call };
        var body = new IBirStatement[] { statement };
        var parameter = new BirParameter { Name = "value", Type = source };
        var parameters = new[] { parameter };
        var attribute = new BirAttribute { Attr = source };
        var attributes = new[] { attribute };
        var method = new BirFunctionDeclaration {
            Name = "test", Params = parameters, Body = body, Attrs = attributes,
            Ret = source, RetKotlinType = OptionalField<TypeNode>.Null,
        };
        var methods = new[] { method };
        var root = new BirFileDeclaration { Methods = methods };
        var before = root.DeepClone();
        void Rewrite(BirGraphValue node)
        {
            node.RewriteTypeFieldsInPlace(_ => target);
            node.VisitChildren(Rewrite);
        }
        Rewrite(root);
        Require(ReferenceEquals(root.Methods.Value, methods)
            && ReferenceEquals(root.Methods.Value[0], method)
            && ReferenceEquals(method.Params.Value, parameters)
            && ReferenceEquals(method.Params.Value[0], parameter)
            && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(method.Body.Value[0], statement)
            && ReferenceEquals(statement.Expr.Value, call)
            && ReferenceEquals(call.Args.Value, arguments)
            && ReferenceEquals(call.Args.Value[0], local)
            && ReferenceEquals(method.Attrs.Value, attributes)
            && ReferenceEquals(method.Attrs.Value[0], attribute), "in-place graph identities");
        Require(parameter.Type.Value == target && local.Sty.Value == target
            && call.Owner.Value == target && attribute.Attr.Value == target,
            "in-place descendant type fields");
        Require(!root.Types.IsPresent && call.Sty.IsNull && method.RetKotlinType.IsNull,
            "in-place absent and explicit-null facts");
        var fieldCall = new BirCallStatic {
            Sig = new[] { source }, ResolvedMemberParams = new[] { source },
            Sty = OptionalField<TypeNode>.Null,
            Args = new IBirExpression[] { new BirLocal { Name = "argument", Sty = source } },
        };
        var fields = new List<(string Name, BirGenericFrameRole Frame)>();
        fieldCall.RewriteOwnTypeFieldsInPlace((name, frame, type) => {
            fields.Add((name, frame));
            return name == "sig" ? target : type;
        });
        var frozenMethod = new BirFunctionDeclaration { RetKotlinType = source };
        frozenMethod.RewriteOwnTypeFieldsInPlace((name, frame, type) => {
            fields.Add((name, frame));
            return target;
        });
        Require(fields.Contains(("sig", BirGenericFrameRole.ReferencedDeclaration))
            && fields.Contains(("resolvedMemberParams", BirGenericFrameRole.ReferencedDeclaration))
            && fields.All(field => field.Name != "retKotlinType")
            && fieldCall.Sig.Value.Single() == target && fieldCall.ResolvedMemberParams.Value.Single() == source
            && frozenMethod.RetKotlinType.Value == source && fieldCall.Sty.IsNull,
            "field-context rewrite retains declared frame roles, frozen facts and explicit null");
        var argumentSlots = 0;
        var fieldArguments = fieldCall.Args.Value;
        fieldCall.VisitOwnChildSlots((name, frame, slot) => {
            if (name != "args") return;
            Require(frame == BirGenericFrameRole.Lexical, "argument slots belong to the caller frame");
            slot.Replace(new BirLocal { Name = "replacement" });
            argumentSlots++;
        });
        fieldCall.ClearResultTypeStamp();
        root.ClearResultTypeStamp();
        Require(argumentSlots == 1 && ReferenceEquals(fieldCall.Args.Value, fieldArguments)
            && fieldCall.Args.Value[0] is BirLocal { Name.HasValue: true } replacement
            && replacement.Name.Value == "replacement" && !fieldCall.Sty.IsPresent
            && fieldCall.Sig.Value.Single() == target,
            "field-context child mutation preserves owning vectors and clears only the result stamp");
        var expected = before.Rewrite(node => node.RewriteTypeFields(_ => target));
        Require(root.StructurallyEquals(expected), "in-place type rewrite changes no other data");

        var roleRoot = (BirFileDeclaration)before.DeepClone();
        void RewriteRoles(BirGraphValue node) => node.RewriteRepresentationInPlace(
            (frame, role, type) => frame == BirGenericFrameRole.Lexical
                && role != BirRepresentationRole.Declaration && role != BirRepresentationRole.Foreign
                ? target : type,
            (frame, role, child) => {
                if (frame == BirGenericFrameRole.Lexical && role != BirRepresentationRole.Attributes)
                    RewriteRoles(child);
            });
        RewriteRoles(roleRoot);
        var roleMethod = roleRoot.Methods.Value[0];
        var roleCall = (BirCallStatic)((BirExpressionStatement)roleMethod.Body.Value[0]).Expr.Value;
        Require(roleCall.TypeArgs.Value[0] == target && roleCall.Owner.Value == target
            && ((BirLocal)roleCall.Args.Value[0]).Sty.Value == target
            && roleCall.Sig.Value[0] == source && roleCall.ResolvedMemberParams.Value[0] == source
            && roleCall.ResolvedMemberReturn.Value == source
            && roleMethod.Attrs.Value[0].Attr.Value == source,
            "representation lexical, foreign, declaration and attribute roles");
        var genericCall = new BirCallStatic { TypeArgs = new[] { source }, Sig = new[] { source } };
        genericCall.RewriteGenericFrameInPlace(
            (role, type) => role == BirGenericFrameRole.Lexical ? target : type, (_, _) => { });
        Require(genericCall.TypeArgs.Value[0] == target && genericCall.Sig.Value[0] == source,
            "generic frame lexical versus referenced declaration");
        var observedArguments = genericCall.TypeArgs.Value;
        var observedSignature = genericCall.Sig.Value;
        var observedTypes = new List<(BirGenericFrameRole Role, TypeNode Type)>();
        genericCall.VisitGenericFrame((role, type) => observedTypes.Add((role, type)), (_, _) => { });
        Require(observedTypes.Contains((BirGenericFrameRole.Lexical, target))
            && observedTypes.Contains((BirGenericFrameRole.ReferencedDeclaration, source))
            && ReferenceEquals(observedArguments, genericCall.TypeArgs.Value)
            && ReferenceEquals(observedSignature, genericCall.Sig.Value),
            "generic-frame observation retains vector identity and owning roles");

        var statementOwner = new BirFunctionDeclaration { Body = new IBirStatement[] { statement } };
        var statementFile = new BirFileDeclaration { Methods = new[] { statementOwner } };
        statementOwner.VisitSourceChildren(_ => { }, region =>
            region.ReplaceStatements!(new IBirStatement[] { statement, new BirExpressionStatement { Expr = local } }));
        Require(ReferenceEquals(statementFile.Methods.Value[0], statementOwner)
            && statementOwner.Body.Value.Length == 2 && ReferenceEquals(statementOwner.Body.Value[0], statement),
            "statement-vector replacement keeps the owning declaration and continuing statements");

        var ownershipCall = new BirCallStatic {
            TypeArgs = new[] { source }, Sig = new[] { source },
            MemberSignature = new[] { source }, MemberReturnType = source,
            ResolvedMemberReturn = source, ResolvedMemberParams = new[] { source },
        };
        ownershipCall.RewriteOwnershipInPlace(
            (role, type) => role == BirOwnershipRole.ForeignVector ? type : target, (_, _) => { });
        Require(ownershipCall.TypeArgs.Value[0] == target && ownershipCall.Sig.Value[0] == source
            && ownershipCall.ResolvedMemberParams.Value[0] == source
            && ownershipCall.MemberSignature.Value[0] == target && ownershipCall.MemberReturnType.Value == target
            && ownershipCall.ResolvedMemberReturn.Value == target,
            "owner permutation distinguishes foreign vectors from member descriptors and resolved returns");
        var ownershipNew = new BirNew { ArgTypes = new[] { source }, MemberSignature = new[] { source } };
        ownershipNew.RewriteOwnershipInPlace(
            (role, type) => role == BirOwnershipRole.Lexical ? target : type, (_, _) => { });
        Require(ownershipNew.ArgTypes.Value[0] == target && ownershipNew.MemberSignature.Value[0] == source,
            "construction argument types belong to the application rather than the member declaration");
        var frozen = new BirFieldDeclaration { Type = source, KotlinType = source };
        frozen.RewriteOwnershipInPlace(
            (role, type) => role == BirOwnershipRole.FrozenFact ? type : target, (_, _) => { });
        Require(frozen.Type.Value == target && frozen.KotlinType.Value == source,
            "ownership traversal leaves encoded source type facts opaque");

        var declarations = new[] { new BirTypeParameter { Name = "T" } };
        var slots = new BirCallStatic { MemberOwnerTypeParams = declarations, MemberMethodTypeParams = declarations };
        var capture = new BirInlineCapture { SharedCellTypeParams = declarations };
        var ordinary = new BirFunctionDeclaration { TypeParams = declarations };
        var seen = new List<BirRepresentationParameterRole>();
        BirTypeParameter[] Expand(BirRepresentationParameterRole role, BirTypeParameter[] values)
        {
            seen.Add(role);
            return values.Append(new BirTypeParameter { Name = role.ToString() }).ToArray();
        }
        slots.RewriteRepresentationParametersInPlace(Expand);
        capture.RewriteRepresentationParametersInPlace(Expand);
        ordinary.RewriteRepresentationParametersInPlace(Expand);
        Require(seen.SequenceEqual(new[] { BirRepresentationParameterRole.Owner,
                BirRepresentationParameterRole.Method, BirRepresentationParameterRole.SharedCell })
            && slots.MemberOwnerTypeParams.Value.Length == 2 && slots.MemberMethodTypeParams.Value.Length == 2
            && capture.SharedCellTypeParams.Value.Length == 2
            && ReferenceEquals(ordinary.TypeParams.Value, declarations), "selected parameter vectors only");

        var expression = new BirValueBlock {
            Type = target, Stmts = Array.Empty<IBirStatement>(),
            Result = new BirLocal { Name = "value", Sty = target },
        };
        Require(expression.StructurallyEquals(expression.DeepClone()), "deep-clone logical equality");
        var decoded = (BirGraphValue)ExecutableBirCodec.ReadExpression(ExecutableBirCodec.Codec,
            ExecutableBirCodec.EncodeExpression(ExecutableBirCodec.Codec, expression));
        Require(expression.StructurallyEquals(decoded), "codec logical equality");
        var absent = new BirValueBlock();
        var explicitNull = new BirValueBlock { Stmts = OptionalField<IBirStatement[]>.Null };
        var empty = new BirValueBlock { Stmts = Array.Empty<IBirStatement>() };
        Different(absent, explicitNull, "absent versus null");
        Different(explicitNull, empty, "null versus empty");
        Different(absent, empty, "absent versus empty");
        Different(new BirOpaqueBytes { Bytes = new byte[] { 1 } },
            new BirOpaqueBytes { Bytes = new byte[] { 2 } }, "opaque byte content");
        Different(Typed(new TypeNode.Fqn("A") { Classifier = new("p", new[] { "Outer" }, "A") }),
            Typed(new TypeNode.Fqn("A") { Classifier = new("p", new[] { "Other" }, "A") }),
            "source classifier owner path");
        Different(Typed(new TypeNode.Fn(false, target, Array.Empty<TypeNode>())),
            Typed(new TypeNode.Fn(false, target, Array.Empty<TypeNode>(), Ctx: Array.Empty<TypeNode>())),
            "function absent versus empty context");
        Different(Typed(new TypeNode.Array(target, 1, true)),
            Typed(new TypeNode.Array(target, 1, false)), "SZ versus rank-one MD array");
        SourceTypeAlias Alias(string value)
        {
            var classifier = new SourceClassifier("sample", new[] { "Outer" }, "A");
            var type = new SourceType.ClassType(classifier, false, false, false,
                Array.Empty<SourceTypeArgument>(), Array.Empty<SourceAnnotation>());
            return new SourceTypeAlias("sample", new[] { "Outer" }, "Alias", "public",
                Array.Empty<SourceTypeParameter>(), type, type,
                new[] { new SourceAnnotation(classifier, new[] {
                    new SourceAnnotationArgument("values", new SourceAnnotationValue.ArrayValue(false,
                        new SourceAnnotationValue[] { new SourceAnnotationValue.StringValue(false, value) })) }) });
        }
        var metadata = new BirFileDeclaration { TypeAliases = new[] { Alias("one") } };
        Require(metadata.StructurallyEquals(new BirFileDeclaration { TypeAliases = new[] { Alias("one") } }),
            "independently allocated nested metadata equality");
        Different(metadata, new BirFileDeclaration { TypeAliases = new[] { Alias("two") } },
            "nested annotation metadata content");
    }

    static BirLocal Typed(TypeNode type) => new() { Name = "value", Sty = type };
    static void Different(BirGraphValue left, BirGraphValue right, string label)
        => Require(!left.StructurallyEquals(right) && !right.StructurallyEquals(left), label);
    static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("BIR graph mutation: " + label);
    }
}
