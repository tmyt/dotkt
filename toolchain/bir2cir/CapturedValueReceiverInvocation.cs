using System.Text.Json.Nodes;
using DotKt.Bir;

// Capture closes a method's type frame, not the location of its value-type receiver. Reflection only creates
// a stateless adapter; the actual call passes the original managed address through an ordinary interface slot.
// No managed reference is boxed, stored in the adapter, or reconstructed through success/exception copyback.
static class CapturedValueReceiverInvocation
{
    static readonly TypeNode Object = new TypeNode.Fqn("object");
    static readonly TypeNode Values = new TypeNode.Array(Object);
    internal sealed record Plan(JsonObject Contract, JsonObject Adapter, JsonArray FactoryBody,
        TypeNode.Fqn CallerContract, TypeNode Receiver, TypeNode ContractReceiver, TypeNode Return);

    internal static Plan Create(string name, string lexicalOwner, JsonArray ownerFrame, JsonArray methodFrame,
        JsonArray fullFrame, TypeNode.Fqn receiver, TypeNode.Fqn liftedReceiver, JsonObject forward, bool returnsVoid)
    {
        var selectedMethodArity = fullFrame.Count - ownerFrame.Count - methodFrame.Count;
        TypeNode ClassFrame(TypeNode type) => CapturedGenericInvocation.Map(type,
            variable => new TypeNode.Tv("type", variable.I));
        TypeNode ContractFrame(TypeNode type) => CapturedGenericInvocation.Map(type,
            variable => new TypeNode.Tv("type", variable.Scope == "type" ? variable.I : ownerFrame.Count + variable.I));
        var callerArguments = Enumerable.Range(0, ownerFrame.Count).Select(i => (TypeNode)new TypeNode.Tv("type", i))
            .Concat(Enumerable.Range(0, methodFrame.Count).Select(i => (TypeNode)new TypeNode.Tv("method", i))).ToArray();
        var contractName = name + "$receiver";
        var adapterName = name + "$adapter";
        var contractReceiver = ContractFrame(receiver);
        var result = returnsVoid ? new TypeNode.Fqn("void") : Object;
        JsonArray Parameters(TypeNode receiverType) => new(
            new JsonObject { ["name"] = "receiver", ["type"] = TypeJson.Write(new TypeNode.ByRef(receiverType)) },
            new JsonObject { ["name"] = "values", ["type"] = TypeJson.Write(Values) });
        JsonObject Method(TypeNode receiverType, bool abstractMethod, JsonArray body) => new()
        {
            ["name"] = "Invoke", ["static"] = false, ["vis"] = "public",
            ["virtual"] = true, ["abstract"] = abstractMethod, ["override"] = false,
            ["typeParams"] = new JsonArray(), ["params"] = Parameters(receiverType),
            ["ret"] = TypeJson.Write(result), ["body"] = body, ["attrs"] = new JsonArray(),
        };
        JsonObject InvariantParameter(JsonNode parameter, Func<TypeNode, TypeNode> map)
        {
            var clone = CapturedGenericInvocation.CloneParameter(parameter, map);
            clone.Remove("variance");
            return clone;
        }
        var contract = Declaration(contractName, "interface", new JsonArray(ownerFrame.Concat(methodFrame)
            .Select(parameter => (JsonNode)InvariantParameter(parameter, ContractFrame)).ToArray()));
        contract["methods"] = new JsonArray(Method(contractReceiver, true, new JsonArray()));

        var invocation = (JsonObject)forward.DeepClone();
        // The MemberRef's parameter/return vector remains in the selected native MethodDef's frame. Only its
        // constructed owner and the call-site types move from the factory's method frame into the adapter's type frame.
        foreach (var key in new[] { "type", "ret", "resolvedMemberReturn" })
            if (invocation[key] != null) invocation[key] = TypeJson.Write(ClassFrame(TypeJson.Read(invocation[key])));
        invocation["memberRef"]["declaringType"] = TypeJson.Write(ClassFrame(liftedReceiver));
        invocation["typeArgs"] = new JsonArray(((JsonArray)invocation["typeArgs"])
            .Select(type => TypeJson.Write(ClassFrame(TypeJson.Read(type)))).ToArray());
        invocation["recv"] = new JsonObject { ["k"] = "byrefLoad", ["local"] = "receiver",
            ["elem"] = TypeJson.Write(ClassFrame(liftedReceiver)) };
        var arguments = (JsonArray)forward["args"];
        var declaredParameters = ((JsonArray)forward["memberRef"]["parameterTypes"]).Select(TypeJson.Read).ToArray();
        TypeNode CloseParameter(TypeNode type) => CapturedGenericInvocation.Map(type, variable => variable.Scope == "type"
            ? liftedReceiver.Args?[variable.I] ?? throw new InvalidOperationException("Captured receiver has no owner frame")
            : variable);
        invocation["args"] = new JsonArray(Enumerable.Range(0, arguments.Count).Select(index => (JsonNode)new JsonObject
        {
            ["k"] = "cast", ["type"] = TypeJson.Write(ClassFrame(CloseParameter(SignatureValueTypes.Of(declaredParameters[index])))),
            ["e"] = new JsonObject { ["k"] = "arrayGet", ["array"] = Local("values"),
                ["index"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("int"), ["value"] = index + 1 },
                ["elem"] = TypeJson.Write(Object) },
        }).ToArray());
        var adapterBody = new JsonArray(new JsonObject { ["k"] = returnsVoid ? "exprStmt" : "return",
            [returnsVoid ? "expr" : "value"] = returnsVoid ? invocation
                : new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(Object), ["e"] = invocation } });
        var adapter = Declaration(adapterName, "class", new JsonArray(fullFrame
            .Select(parameter => (JsonNode)CapturedGenericInvocation.CloneParameter(parameter, ClassFrame)).ToArray()));
        adapter["nestedIn"] = lexicalOwner;
        adapter["interfaces"] = new JsonArray(TypeJson.Write(new TypeNode.Fqn(contractName,
            callerArguments.Length == 0 ? null : Enumerable.Range(0, callerArguments.Length)
                .Select(i => (TypeNode)new TypeNode.Tv("type", selectedMethodArity + i)).ToArray())));
        adapter["ctors"] = new JsonArray(new JsonObject { ["vis"] = "public", ["params"] = new JsonArray(),
            ["baseArgs"] = null, ["body"] = new JsonArray() });
        adapter["methods"] = new JsonArray(Method(ClassFrame(liftedReceiver), false, adapterBody));
        var factoryBody = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = new JsonObject
        {
            ["k"] = "new", ["type"] = TypeJson.Write(new TypeNode.Fqn(adapterName,
                Enumerable.Range(0, fullFrame.Count).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray())),
            ["args"] = new JsonArray(), ["argTypes"] = new JsonArray(), ["localCtorIndex"] = 0,
        } });
        return new Plan(contract, adapter, factoryBody,
            new TypeNode.Fqn(contractName, callerArguments.Length == 0 ? null : callerArguments),
            receiver, contractReceiver, result);
    }

    internal static JsonObject Invoke(Plan plan, JsonNode receiver, JsonObject factory, string prefix)
    {
        var pointer = prefix + "$address";
        var values = prefix + "$values";
        var helper = prefix + "$helper";
        var factoryArguments = (JsonArray)factory["args"];
        var valueArray = factoryArguments[7].DeepClone();
        factoryArguments[7] = Local(values);
        var signature = new JsonArray(TypeJson.Write(new TypeNode.ByRef(plan.ContractReceiver)), TypeJson.Write(Values));
        return new JsonObject
        {
            ["k"] = "valueBlock", ["type"] = TypeJson.Write(plan.Return),
            ["stmts"] = new JsonArray(
                Variable(pointer, new TypeNode.ByRef(plan.Receiver), new JsonObject { ["k"] = "byrefOf", ["inner"] = receiver.DeepClone() }),
                Variable(values, Values, valueArray),
                Variable(helper, plan.CallerContract, new JsonObject { ["k"] = "cast",
                    ["type"] = TypeJson.Write(plan.CallerContract), ["e"] = factory })),
            ["result"] = new JsonObject
            {
                ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(plan.CallerContract),
                ["method"] = "Invoke", ["virtual"] = true, ["recv"] = Local(helper), ["sig"] = signature,
                ["ret"] = TypeJson.Write(plan.Return), ["calleeRet"] = TypeJson.Write(plan.Return),
                ["args"] = new JsonArray(Local(pointer), Local(values)),
            },
        };
    }

    static JsonObject Declaration(string name, string kind, JsonArray frame) => new()
    {
        ["name"] = name, ["kind"] = kind, ["generated"] = true, ["vis"] = "internal",
        ["typeParams"] = frame, ["interfaces"] = new JsonArray(), ["fields"] = new JsonArray(),
        ["ctors"] = new JsonArray(), ["methods"] = new JsonArray(), ["attrs"] = new JsonArray(),
    };
    static JsonObject Variable(string name, TypeNode type, JsonNode init) => new()
        { ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(type), ["init"] = init };
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
}
