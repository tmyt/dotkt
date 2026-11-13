using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class InlineBirStashTests
{
    internal static void SelfTest()
    {
        const string facade = "kotlin.StashFixture";
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        BirSourcePoint Point() => new() { Span = new BirSourceSpan { F = "producer.kt", L = 7 } };
        BirExpressionStatement Delegate(string method, string owner = facade) => new() {
            Expr = new BirNewDelegate { Method = method, CalleeOwner = new TypeNode.Fqn(owner),
                FuncType = new TypeNode.Fn(false, integer, Array.Empty<TypeNode>()) },
        };
        BirFunctionDeclaration Generated(string name, params IBirStatement[] body) => new() {
            Name = name, Static = true, Generated = true, Params = Array.Empty<BirParameter>(), Ret = integer,
            Body = body,
        };
        var first = Generated("first", Point(), Delegate("second"));
        first.SignatureOverrideDeclarations = new[] { "captured-slot" };
        var second = Generated("second", Delegate("first"));
        var user = Generated("user", Point()); user.Generated = false;
        var defaultBody = new BirValueBlock { Stmts = new IBirStatement[] { Point() },
            Result = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(1) } };
        var parameter = new BirParameter { Name = "value", Type = integer,
            NominalParameterSignatureIdentity = integer, GenericParameterSignatureBounds = new TypeNode[] { integer },
            Attrs = new[] { DefaultBirCarrier.Create(0, defaultBody) } };
        var method = new BirFunctionDeclaration {
            Name = "pick", DeclarationId = "pick-int", Static = true, Mods = new BirModifiers { Inline = true },
            TypeParams = Array.Empty<BirTypeParameter>(), Params = new[] { parameter }, Ret = integer,
            Body = new IBirStatement[] { Point(), Delegate("first"), Delegate("first"), Delegate("user"), Delegate("second", "foreign.Owner") },
        };
        var overload = new BirFunctionDeclaration {
            Name = "pick", DeclarationId = "pick-string", Static = true, Mods = new BirModifiers { Inline = true },
            Params = new[] { new BirParameter { Name = "value", Type = text } }, Ret = text,
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "value", Sty = text } } },
        };
        var extension = new BirFunctionDeclaration {
            Name = "extend", DeclarationId = "extension", Static = false, Mods = new BirModifiers { Inline = true }, Ret = integer,
            Params = new[] { new BirParameter { Name = "receiver", Type = integer, Mods = new BirModifiers { ExtensionReceiver = true } } },
            Body = Array.Empty<IBirStatement>(),
        };
        var dispatch = new BirFunctionDeclaration { Name = "dispatch", Mods = new BirModifiers { Inline = true },
            Params = Array.Empty<BirParameter>(), Ret = integer, Body = Array.Empty<IBirStatement>() };
        var carrier = new BirClassDefinition { Name = "Box$Static", Kind = "class", StaticCarrier = new BirStaticCarrier { Owner = "Box" },
            Methods = new[] { extension } };
        var root = new BirFileDeclaration { FileClass = facade, Methods = new[] { method, overload, first, second, user },
            Types = new IBirTypeDeclaration[] { carrier, new BirClassDefinition { Name = "kotlin.Member", Kind = "class", Methods = new[] { dispatch } } } };
        var sourceMethods = root.Methods.Value;
        var sourceBody = method.Body.Value;
        var sourceParameter = method.Params.Value[0];
        var index = new InlineBirIndex();
        index.Stash(root);
        var local = index.Declaration("pick-int");
        var outgoing = ExecutableBirCodec.ReadInline(ExecutableBirCodec.Codec, method.InlineBir.Value);
        if (!ReferenceEquals(root.Methods.Value, sourceMethods) || !ReferenceEquals(method.Body.Value, sourceBody)
            || !ReferenceEquals(method.Params.Value[0], sourceParameter)
            || !parameter.GenericParameterSignatureBounds.HasValue || !first.SignatureOverrideDeclarations.HasValue
            || local.Lifted.Value.Length != 2 || local.Lifted.Value[0].Name.Value != "first" || local.Lifted.Value[1].Name.Value != "second"
            || local.Lifted.Value[0].SignatureOverrideDeclarations.IsPresent
            || local.Params.Value[0].NominalParameterSignatureIdentity.IsPresent
            || local.Params.Value[0].GenericParameterSignatureBounds.IsPresent
            || local.Body.Value[0] is not BirSourcePoint { Span.HasValue: true }
            || outgoing.Body.Value[0] is not BirSourcePoint { Span.IsPresent: false }
            || outgoing.Lifted.Value[0].Body.Value[0] is not BirSourcePoint { Span.IsPresent: false }
            || DefaultBirCarrier.OfParameter(outgoing.Params.Value[0]) is not BirValueBlock { Stmts.Value: [BirSourcePoint { Span.IsPresent: false }] }
            || DefaultBirCarrier.OfParameter(local.Params.Value[0]) is not BirValueBlock { Stmts.Value: [BirSourcePoint { Span.HasValue: true }] })
            throw new InvalidOperationException("Executable stash changed live declarations, transitive target closure, signature facts or source-span ownership");

        var member = index.Declaration("extension");
        if (member.Owner.Value != "Box$Static" || member.Recv.Value != "extensionParam" || member.Static.Value
            || !ReferenceEquals(index.Candidates("Box", "extend", 1, 0)?.Single(), member)
            || index.Candidates("Box$Static", "extend", 1, 0) != null
            || index.OwnerlessCandidates("dispatch", 0, 0) != null)
            throw new InvalidOperationException("Executable stash lost physical/semantic owner association or receiver eligibility");

        JsonObject Call(TypeNode signature, string identity = null) => new() {
            ["k"] = "callInline", ["owner"] = TypeJson.Fqn(facade), ["callee"] = TypeJson.Fqn(facade + ".pick"),
            ["pc"] = 1, ["ga"] = 0, ["paramSig"] = new JsonArray(TypeJson.Write(signature)),
            [DeclarationIdentityBinding.Key] = identity,
        };
        var exact = InlineSplice.ResolvePayload(Call(text, "pick-int"), index, null);
        var selected = InlineSplice.ResolvePayload(Call(text), index, null);
        var ownerlessCall = Call(integer); ownerlessCall.Remove("owner");
        var selectedOwnerless = InlineSplice.ResolvePayload(ownerlessCall, index, null);
        if (!exact.sameModule || exact.payload?[DeclarationIdentityBinding.Key]?.GetValue<string>() != "pick-int"
            || !selected.sameModule || selected.payload?[DeclarationIdentityBinding.Key]?.GetValue<string>() != "pick-string"
            || !selectedOwnerless.sameModule || selectedOwnerless.payload?[DeclarationIdentityBinding.Key]?.GetValue<string>() != "pick-int")
            throw new InvalidOperationException("Executable stash selection diverged from exact identity, overload or ownerless policy");
        exact.payload["body"][0]["span"] = null;
        if (local.Body.Value[0] is not BirSourcePoint { Span.HasValue: true }
            || InlineSplice.ResolvePayload(Call(integer, "pick-int"), index, null).payload["body"][0]["span"] == null)
            throw new InvalidOperationException("Executable splice materializations share mutable stash nodes");
        Console.WriteLine("[typed executable inline stash] self-test OK (transitive lifted, receivers, selection, source spans, independent materialization)");
    }
}
