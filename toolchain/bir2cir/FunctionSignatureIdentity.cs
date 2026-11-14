using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Function value storage is uniform; declaration identity still distinguishes
// the pre-carrier signature. This fact describes a CLR signature, never a value.
static class FunctionSignatureIdentity
{
    internal const string Key = "functionSignatureType";
    internal const string CallKey = "functionCallSignature";

    internal static void Capture(BirGraphValue root)
    {
        void Parameters(OptionalField<BirParameter[]> parameters)
        {
            foreach (var parameter in parameters.OrEmpty())
                if (!parameter.FunctionSignatureType.HasValue
                    && FunctionValueRepresentation.ContainsOrdinaryFunction(parameter.Type.OrDefault()))
                    parameter.FunctionSignatureType = BirGraphValue.CopyType(parameter.Type.Value);
        }
        void Declarations(OptionalField<BirFunctionDeclaration[]> methods, OptionalField<BirConstructor[]> constructors)
        {
            foreach (var method in methods.OrEmpty()) Parameters(method.Params);
            foreach (var constructor in constructors.OrEmpty()) Parameters(constructor.Params);
        }
        void Owner(IBirTypeDeclaration owner) => Declarations(owner.Methods, owner.Ctors);
        if (root is BirFileDeclaration file)
        {
            Declarations(file.Methods, default);
            foreach (var owner in file.Types.OrEmpty()) Owner(owner);
        }
        else if (root is IBirTypeDeclaration owner) Owner(owner);
    }

    internal static JsonNode SignatureType(JsonObject parameter) => TypeJson.Write(SignatureType(
        TypeJson.Read(parameter["type"]), TypeJson.Read(parameter[Key])));

    internal static TypeNode SignatureType(BirParameter parameter) => SignatureType(
        parameter.Type.OrDefault(), parameter.FunctionSignatureType.OrDefault());

    static TypeNode SignatureType(TypeNode type, TypeNode discriminator) => type == null
        ? throw new InvalidOperationException("A physical declaration parameter has no type")
        : discriminator != null ? new TypeNode.Mod(false, DeclarationType(discriminator), type) : BirGraphValue.CopyType(type);

    // A signature is allocated in its declaration's open frame, before owner/method substitution.
    // Closing a value-returning T at Unit must retain Func<T,T>, not choose Action<Unit> again.
    // This describes only the signature discriminator; ordinary function values keep their shared carrier.
    internal static TypeNode DeclarationType(TypeNode type) => type switch
    {
        TypeNode.Fqn { Name: FunctionPointerVocabulary.Pointer } => type,
        TypeNode.Fn { Suspend: false } fn => BirTypeLowering.PhysicalDelegate(
            DeclarationType(BirTypeLowering.DelegateReturnSlot(fn)),
            fn.Params.Select(DeclarationType).ToArray(),
            fn.Recv == null ? null : DeclarationType(fn.Recv), fn.Clr)
            with { Ctx = fn.Ctx?.Select(DeclarationType).ToArray() },
        TypeNode.Fn fn => fn with {
            Ret = DeclarationType(fn.Ret), Params = fn.Params.Select(DeclarationType).ToArray(),
            Recv = fn.Recv == null ? null : DeclarationType(fn.Recv),
            Ctx = fn.Ctx?.Select(DeclarationType).ToArray(),
        },
        TypeNode.Fqn { Args: { } args } named => named with { Args = args.Select(DeclarationType).ToArray() },
        TypeNode.Array array => array with { Elem = DeclarationType(array.Elem) },
        TypeNode.Nullable nullable => nullable with { Of = DeclarationType(nullable.Of) },
        TypeNode.Oblivious oblivious => oblivious with { Of = DeclarationType(oblivious.Of) },
        TypeNode.ByRef byRef => byRef with { Of = DeclarationType(byRef.Of) },
        TypeNode.Ptr pointer => pointer with { Of = DeclarationType(pointer.Of) },
        TypeNode.Mod modifier => modifier with { M = DeclarationType(modifier.M), Of = DeclarationType(modifier.Of) },
        _ => type,
    };

    internal static JsonArray Signature(JsonArray parameters) =>
        new(parameters.OfType<JsonObject>().Select(SignatureType).ToArray());

    internal static void SelfTest()
    {
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var variable = new TypeNode.Tv("type", 0);
        var open = new TypeNode.Fn(false, variable, new TypeNode[] { variable });
        var closed = SupertypeGraph.SubstOwnerTvs(DeclarationType(open), new TypeNode[] { unit });
        if (closed is not TypeNode.Fn { Clr: "System.Func" } function || function.Ret != unit
            || function.Params.Single() != unit
            || DeclarationType(open with { Ret = unit }) is not TypeNode.Fn {
                Clr: "System.Action", Ret: TypeNode.Fqn { Name: "void" } }
            || DeclarationType(closed) != closed)
            throw new InvalidOperationException("Function signature allocation was repeated after owner substitution");
        var nested = new TypeNode.Mod(false, new TypeNode.Fqn("Probe.Marker"),
            new TypeNode.Array(new TypeNode.Fqn("Probe.Box", new TypeNode[] { open })));
        if (DeclarationType(nested) is not TypeNode.Mod {
                Of: TypeNode.Array { Elem: TypeNode.Fqn { Args: [TypeNode.Fn { Clr: "System.Func" }] } } })
            throw new InvalidOperationException("Nested function signature allocation lost its declaration frame");
        var parameter = new BirParameter { Name = "callback", Type = new TypeNode.Fqn("System.Object"),
            FunctionSignatureType = open };
        var call = new BirCallStatic { Method = "invoke", FunctionCallSignature = new TypeNode[] { open } };
        var typed = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "probe", Params = new[] { parameter }, Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } },
        } } };
        var dom = typed.ToDom();
        Complete(dom);
        Complete(typed);
        if (!JsonNode.DeepEquals(dom, typed.ToDom()) || parameter.FunctionSignatureType.IsPresent
            || call.FunctionCallSignature.IsPresent || parameter.Type.Value is not TypeNode.Mod)
            throw new InvalidOperationException("Owning function signature completion lost its declaration discriminator");
        Console.WriteLine("[function signature identity] self-test OK (open Func, concrete Action, nested and idempotent)");
    }

    public static void Complete(JsonNode root)
    {
        void RemoveCallFacts(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                obj.Remove(CallKey);
                foreach (var child in obj.Select(pair => pair.Value).ToList()) RemoveCallFacts(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) RemoveCallFacts(child);
        }
        RemoveCallFacts(root);
        foreach (var parameters in DeclarationParameters(root))
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (parameter[Key] != null)
                {
                    parameter["type"] = SignatureType(parameter);
                    parameter.Remove(Key);
                }
    }

    internal static void Complete(BirGraphValue root)
    {
        root.Visit(value => {
            switch (value)
            {
                case BirCallStatic call: call.FunctionCallSignature = default; break;
                case BirCallInstance call: call.FunctionCallSignature = default; break;
                case BirConstrainedCall call: call.FunctionCallSignature = default; break;
                case BirParameter { FunctionSignatureType.HasValue: true } parameter:
                    parameter.Type = new TypeNode.Mod(false,
                        DeclarationType(parameter.FunctionSignatureType.Value), parameter.Type.Value);
                    parameter.FunctionSignatureType = default;
                    break;
            }
        });
    }

    static IEnumerable<JsonArray> DeclarationParameters(JsonNode node)
    {
        if (node is not JsonObject owner) yield break;
        foreach (var key in new[] { "methods", "ctors" })
            if (owner[key] is JsonArray declarations)
                foreach (var declaration in declarations.OfType<JsonObject>())
                    if (declaration["params"] is JsonArray parameters) yield return parameters;
        if (owner["types"] is JsonArray types)
            foreach (var type in types)
                foreach (var parameters in DeclarationParameters(type)) yield return parameters;
    }
}
