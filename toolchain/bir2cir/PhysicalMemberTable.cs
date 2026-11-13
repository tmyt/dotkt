using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Cir;
using MessagePack;

// A physical role table is declaration metadata, not an executable IR bag.
// Entries retain the selected CLR member's exact identity and open signature.
sealed class PhysicalMemberTable
{
    internal IReadOnlyDictionary<string, MemberRefNode> Members { get; }
    PhysicalMemberTable(Dictionary<string, MemberRefNode> members) => Members = members;

    internal bool StructurallyEquals(PhysicalMemberTable other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other == null || Members.Count != other.Members.Count) return false;
        foreach (var (role, member) in Members)
            if (!other.Members.TryGetValue(role, out var candidate) || !EqualMember(member, candidate)) return false;
        return true;

        static bool EqualMember(MemberRefNode left, MemberRefNode right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Kind != right.Kind || left.Assembly != right.Assembly
                || left.Name != right.Name || left.GenericArity != right.GenericArity
                || left.CallingConvention != right.CallingConvention
                || !TypeNodeDataEquality.Equal(left.DeclaringType, right.DeclaringType)
                || !TypeNodeDataEquality.Equal(left.ReturnType, right.ReturnType)) return false;
            if (left.ParameterTypes == null || right.ParameterTypes == null)
                return left.ParameterTypes == null && right.ParameterTypes == null;
            if (left.ParameterTypes.Length != right.ParameterTypes.Length) return false;
            for (var index = 0; index < left.ParameterTypes.Length; index++)
                if (!TypeNodeDataEquality.Equal(left.ParameterTypes[index], right.ParameterTypes[index])) return false;
            return true;
        }
    }

    internal static PhysicalMemberTable Read(JsonNode source) => new(source.AsObject().ToDictionary(
        pair => pair.Key, pair => MemberRefNode.Read(pair.Value), StringComparer.Ordinal));

    internal static PhysicalMemberTable Read(ref Utf8JsonReader reader) => new(
        JsonSerializer.Deserialize<Dictionary<string, MemberRefNode>>(ref reader, AttributeJson.Options)
            ?? throw new FormatException());

    internal static void Write(Utf8JsonWriter writer, PhysicalMemberTable value)
        => JsonSerializer.Serialize(writer, value.Members, AttributeJson.Options);

    internal static JsonNode ToDom(PhysicalMemberTable value)
    {
        var result = new JsonObject();
        foreach (var member in value.Members) result.Add(member.Key, member.Value.Write());
        return result;
    }

    internal static PhysicalMemberTable ReadMessagePack(ref MessagePackReader reader)
    {
        if (reader.Depth++ >= BirJson.MaxDepth) throw new FormatException();
        var count = reader.ReadMapHeader();
        if (count > (reader.Sequence.Length - reader.Consumed) / 2) throw new FormatException();
        var members = new Dictionary<string, MemberRefNode>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var role = IrMessagePack.ReadString(ref reader);
            if (!members.TryAdd(role, CirMessagePack.ReadMemberReference(ref reader))) throw new FormatException();
        }
        reader.Depth--;
        return new(members);
    }

    internal static void WriteMessagePack(ref MessagePackWriter writer, PhysicalMemberTable value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(value.Members.Count);
        foreach (var member in value.Members)
        {
            IrMessagePack.WriteString(ref writer, member.Key);
            CirMessagePack.WriteMemberReference(ref writer, member.Value, depth + 1);
        }
    }
}
