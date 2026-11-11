#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

namespace DotKt.Cir;

public static class DeclarationSelfTest
{
    public static void Run()
    {
        // Serialization samples deliberately cover every record field, not a semantically executable program.
        // Real signature/body/MethodImpl emission is exercised by target-universe and the compiler corpus.
        var integer = new TypeNode.Fqn("System.Int32");
        var owner = new TypeNode.Fqn("Physical.Owner", [new TypeNode.Tv("type", 0)]);
        var reference = new MemberRefNode("method", "Physical", owner, "M", 1, integer,
            MemberRefNode.Instance, [integer]);
        var constructor = new MemberRefNode("ctor", "Physical", new TypeNode.Fqn("Physical.Attribute"),
            ".ctor", 0, MemberRefNode.Void, MemberRefNode.Instance, []);
        var attribute = new AttributeApplication(new TypeNode.Fqn("Physical.Attribute"), [], [], true, "Physical", constructor);
        var attributes = new[] { attribute };
        var position = new CirSourcePosition { F = "λ.kt", L = 0, C = 7 };
        var modifiers = new CirModifiers {
            Inline = true, Infix = true, Operator = true, Tailrec = true, External = true, Ext = true,
            ExtensionReceiver = true, Override = true, Abstract = true, Open = true, Suspend = true,
            Data = true, Sealed = true, Inner = true, Enum = true, Fun = true, Annotation = true, Value = true,
            Object = true, Const = true, Lateinit = true, Vararg = true, Noinline = true, Crossinline = true,
            InlineOnly = true, Context = true,
        };
        var generic = new CirTypeParameter { Name = "T", Variance = "out",
            Constraints = new TypeNode[] { new TypeNode.Fqn("System.IDisposable"), new TypeNode.Tv("type", 1) },
            SpecialConstraints = new[] { "class", "new" } };
        var parameter = new CirParameter { Name = "value", Type = integer, Mods = modifiers,
            Default = new AttributeArgument.Int32Value(-19), Attrs = attributes,
            NullableFlags = new[] { 1, 2 }, SuspendFnType = owner, CtxFnType = 2 };
        var descriptor = new CirMethodImpl { Owner = owner, Member = "M", Arity = 1, Params = [integer],
            Ret = integer, TypeParams = new[] { generic }, MemberRef = reference };
        var import = new CirPInvoke { Module = "physical", EntryPoint = "entry", CallingConvention = "cdecl",
            CallingConventionType = integer, CharSet = "unicode", CharSetType = integer, ExactSpelling = false,
            SetLastError = true, PreserveSig = false, BestFitMapping = false, ThrowOnUnmappableChar = true,
            PseudoFields = ["EntryPoint"], AttributeCtorRef = constructor };
        var method = new CirMethod { Name = "M", Static = false, Override = true, Virtual = true,
            Abstract = false, Vis = "protected", Params = [parameter], Ret = integer,
            Body = new ICirStatement[] { new CirReturn { Value = new CirLocal { Name = "value" } } },
            TypeParams = new[] { generic }, Attrs = attributes, RetAttrs = attributes, Mods = modifiers,
            Pos = position, Generated = true, ObjectOverride = true, ClrFinal = false, SpecialName = false,
            ClrInterfaceSlotBridge = true, RequiresClrOverride = true, ClrOverrideRef = reference, SuspendBridge = false,
            ClrInterfaceImpls = new[] { descriptor }, ClrBaseImpls = new[] { descriptor }, Extern = false,
            Pinvoke = import, BodyTerminates = true, IndependentAliasSlot = false, PhysicalSlotBridge = true,
            InlineBir = new byte[] { 0, 1, 128, 255 }, RetNothing = false, RetNullableFlags = new[] { 1, 2 },
            RetSuspendFnType = owner, SuspendRet = integer, RetCtxFnType = 1,
        };
        var ctor = new CirConstructor { Vis = "public", Params = [parameter],
            BaseArgs = Array.Empty<ICirExpression>(), ThisArgs = OptionalField<ICirExpression[]>.Null,
            DelegationSig = new TypeNode[] { integer }, Body = [],
            BaseCtorRef = constructor, LocalCtorIndex = 0, Attrs = attributes,
            PreStmts = Array.Empty<ICirStatement>(), Pos = position };
        var field = new CirField { Name = "F", Type = integer, Static = false, ReadOnly = false, InitOnly = false,
            Vis = "private", Init = new CirConstant { Type = integer, Value = new AttributeArgument.Int32Value(42) },
            Constant = new AttributeArgument.Int32Value(42), Volatile = false, Const = false, Lateinit = false,
            Attrs = attributes, NullableFlags = new[] { 2 }, SuspendFnType = owner, Pos = position };
        var property = new CirProperty { Name = "P", Type = integer, Get = "get_P", GetSig = Array.Empty<TypeNode>(),
            GetRet = integer, GetMethodArity = 0, Set = "set_P", SetSig = new TypeNode[] { integer },
            SetRet = MemberRefNode.Void, SetMethodArity = 0, Attrs = attributes, NullableFlags = new[] { 1 },
            SuspendFnType = owner, Mods = modifiers, Vis = "public", Recv = owner, Pos = position };
        var entry = new CirEnumEntry { Name = "Entry", Ordinal = 0, Underlying = "System.Int32",
            PhysicalValue = "-19", Attrs = attributes };
        var slots = new CirInterfaceSlotSet { Owner = owner, Assembly = "Physical", Slots = [reference] };
        var @event = new CirEvent { K = "clrEventDecl", Name = "Changed", DelegateType = new TypeNode.Fqn("Physical.Handler") };
        var type = new CirTypeDeclaration { Name = "Nested", Kind = CirTypeKind.Class, Vis = "internal",
            Generated = false, Abstract = false, Final = false, SpecialName = false, BeforeFieldInit = false,
            NestedIn = "Parent", Base = owner, Interfaces = new TypeNode[] { owner }, TypeParams = new[] { generic },
            CapturedTypeParams = new[] { generic }, Fields = new[] { field }, Ctors = new[] { ctor },
            Methods = new[] { method }, Properties = new[] { property }, Attrs = attributes, Mods = modifiers,
            Pos = position, InterfaceSlotRefs = new[] { slots }, ClrEvents = new[] { @event }, Underlying = integer,
            Entries = new[] { entry }, Params = new[] { parameter }, Ret = integer, RetAttrs = attributes };
        var file = new CirFileDeclaration { FileClass = "Facade", HasMain = false, Methods = [method],
            Fields = [field], Types = [type], Properties = new[] { property }, AssemblyAttrs = attributes,
            Attrs = attributes, WellKnownRefs = new Dictionary<string, MemberRefNode> { ["physical"] = reference }, Pos = position };
        var json = DeclarationJson.Write(file);
        var read = DeclarationJson.Read(Encoding.UTF8.GetBytes(json));
        if (DeclarationJson.Write(read) != json || !read.Methods[0].InlineBir.Value.SequenceEqual(method.InlineBir.Value)
            || read.Methods[0].Body.Value[0] is not CirReturn
            || read.Types[0].Ctors.Value[0].ThisArgs.IsNull != true)
            throw new Exception("CIR declaration roundtrip changed a physical fact or executable subtree");
        foreach (var kind in Enum.GetValues<CirTypeKind>())
        {
            var variant = DeclarationJson.Read(Encoding.UTF8.GetBytes(DeclarationJson.Write(file with { Types = [type with { Kind = kind }] })));
            if (variant.Types[0].Kind != kind) throw new Exception("CIR declaration kind changed");
        }
        // Record schema coverage uses actual serialized values, not reflection over the declarations alone.
        object[] records = [import, modifiers, file, type, generic, parameter, method, ctor, field, property, entry, descriptor, slots, @event, position];
        foreach (var (name, fields) in DeclarationWireContract.Properties)
        {
            var record = records.Single(value => value.GetType().Name == name);
            using var encoded = JsonDocument.Parse(JsonSerializer.Serialize(record, record.GetType(), AttributeJson.Options));
            if (!encoded.RootElement.EnumerateObject().Select(pair => pair.Name).ToHashSet().SetEquals(fields))
                throw new Exception("CIR declaration schema field coverage mismatch: " + name);
            CheckNullFields(record);
        }
        Presence(file, field);
        // Descriptor consumption keys must survive an independent parse, including arrays and constraints.
        var independent = read.Methods[0].ClrInterfaceImpls.Value[0];
        if (!descriptor.Equals(independent) || descriptor.GetHashCode() != independent.GetHashCode()
            || descriptor.Equals(independent with { Arity = 2 }))
            throw new Exception("CIR MethodImpl structural bookkeeping key changed");
        Console.WriteLine($"[CIR declarations] self-test OK ({records.Length} record shapes, presence, bodies, opaque bytes, structural descriptor keys)");
    }

    static void Presence(CirFileDeclaration file, CirField field)
    {
        var sample = file with { Fields = [field with { Name = "absent", Init = default },
            field with { Name = "null", Init = OptionalField<ICirExpression>.Null },
            field with { Name = "value", Init = field.Init }] };
        var read = DeclarationJson.Read(Encoding.UTF8.GetBytes(DeclarationJson.Write(sample)));
        if (read.Fields[0].Init.IsPresent || !read.Fields[1].Init.IsNull || !read.Fields[2].Init.HasValue)
            throw new Exception("CIR absent/null/value distinction changed");
        CheckNullFields(sample.Fields[1]);
        CheckNullFields(file.Types[0] with { Base = OptionalField<TypeNode>.Null });
    }

    static void CheckNullFields(object record)
    {
        var allowed = DeclarationWireContract.NullableProperties.Single(item => item.Name == record.GetType().Name).Fields;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record, record.GetType(), AttributeJson.Options));
        foreach (var field in document.RootElement.EnumerateObject())
            if (field.Value.ValueKind == JsonValueKind.Null && !allowed.Contains(field.Name))
                throw new Exception($"CIR serialized null contradicts its schema: {record.GetType().Name}.{field.Name}");
    }
}
