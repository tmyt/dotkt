using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using AsmResolver;
using AsmResolver.PE.Win32Resources;

// The selected SDK generates and reads RES. Its RES parser is internal; bind only to the record-reading API.
// Adapt its records to the PE library without calling Roslyn Emit or inspecting resource file layout.
static class SdkResReader
{
    public static ResourceDirectory Read(string sdkAssembly, string resourcePath, Stream input)
    {
        try
        {
            var api = new SdkResourceRecords(sdkAssembly);
            var root = new ResourceDirectory(0u);
            using var completeReads = new CompleteReadStream(input);
            var records = (IEnumerable)api.Read.Invoke(null, [completeReads]);
            // Roslyn rounds the final record position up to a DWORD boundary. A larger
            // overrun means it accepted a skip past the end of the input, not padding.
            if (input.Position > input.Length && input.Position - input.Length > 3)
                throw new EndOfStreamException();
            foreach (var record in records)
            {
                var type = Directory(root, api.Key(api.Type.GetValue(record)));
                var name = Directory(type, api.Key(api.Name.GetValue(record)));
                var language = (ushort)api.Language.GetValue(record);
                if (name.Entries.Any(entry => entry.Id == language))
                    throw new InvalidDataException("Duplicate Win32 resource type/name/language.");
                name.Entries.Add(new ResourceData(language, new DataSegment((byte[])api.Data.GetValue(record))));
            }
            return root;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } cause)
        {
            throw new InvalidDataException($"Cannot read Win32 RES '{resourcePath}': {cause.Message}", cause);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Truncated Win32 RES '{resourcePath}'.", ex);
        }
    }

    static ResourceDirectory Directory(ResourceDirectory parent, (string Text, ushort Id) key)
    {
        // member-lookup-residual: native resource directory keys, not CLR members or Kotlin semantics
        var child = parent.Entries.OfType<ResourceDirectory>().SingleOrDefault(entry =>
            entry.Name == key.Text && (key.Text != null || entry.Id == key.Id));
        if (child != null) return child;
        child = key.Text == null ? new ResourceDirectory(key.Id) : new ResourceDirectory(key.Text);
        parent.Entries.Add(child);
        return child;
    }

    sealed class SdkResourceRecords
    {
        const BindingFlags InternalField = BindingFlags.Instance | BindingFlags.NonPublic;
        public MethodInfo Read { get; }
        public FieldInfo Type { get; }
        public FieldInfo Name { get; }
        public FieldInfo Language { get; }
        public FieldInfo Data { get; }
        readonly FieldInfo _ordinal;
        readonly FieldInfo _text;

        public SdkResourceRecords(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
                throw new InvalidDataException("A Win32 RES input requires the selected SDK's Microsoft.CodeAnalysis assembly.");
            var provider = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            var reader = provider.GetType("Microsoft.CodeAnalysis.CvtResFile");
            var resource = provider.GetType("Microsoft.CodeAnalysis.RESOURCE");
            var identifier = provider.GetType("Microsoft.CodeAnalysis.RESOURCE_STRING");
            // member-lookup-residual: selected SDK's RES reader API, not a CLR member emitted into the program
            Read = reader?.GetMethod("ReadResFile", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Stream)]);
            if (resource == null || identifier == null || Read?.ReturnType != typeof(List<>).MakeGenericType(resource))
                throw MissingContract(assemblyPath);
            Type = RequireField(resource, "pstringType", identifier);
            Name = RequireField(resource, "pstringName", identifier);
            Language = RequireField(resource, "LanguageId", typeof(ushort));
            Data = RequireField(resource, "data", typeof(byte[]));
            _ordinal = RequireField(identifier, "Ordinal", typeof(ushort));
            _text = RequireField(identifier, "theString", typeof(string));

            FieldInfo RequireField(Type owner, string name, Type type)
            {
                // member-lookup-residual: SDK native resource record fields, not Kotlin or generated CLR members
                var field = owner.GetField(name, InternalField);
                return field?.FieldType == type ? field : throw MissingContract(assemblyPath);
            }
        }

        public (string Text, ushort Id) Key(object record) =>
            ((string)_text.GetValue(record), (ushort)_ordinal.GetValue(record));

        static InvalidOperationException MissingContract(string assemblyPath) =>
            new($"The selected .NET SDK no longer exposes the expected RES reader: '{assemblyPath}'.");
    }

    // Roslyn's reader calls BinaryReader.Read for payloads. Fail on a short read rather than embedding
    // the zero-initialized remainder of a truncated resource. The underlying stream remains caller-owned.
    sealed class CompleteReadStream(Stream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            input.ReadExactly(buffer);
            return buffer.Length;
        }
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
