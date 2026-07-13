// SHARED across bir2cir / ilemit via a <Compile Link/> (like TypeNode.cs — NOT its own project).
// The single source of the JSON depth bound for reading/writing BIR and CIR documents.
//
// System.Text.Json defaults MaxDepth to 64. A single function with deeply-nested inlined lambdas/blocks
// produces a BIR (and the derived CIR) whose method-body JSON nests deeper than 64 — legal Kotlin that
// then hard-crashes the pipeline with "maximum configured depth of 64 has been exceeded" (#147). Every
// BIR/CIR document reader (JsonDocument.Parse / JsonNode.Parse) AND every full-document writer
// (JsonNode.ToJsonString) must use these options so the whole pipeline tolerates the same generous bound.

#nullable enable
using System.Text.Json;

namespace DotKt.Bir;

public static class BirJson
{
    /// <summary>Generous JSON nesting bound for BIR/CIR documents (default STJ MaxDepth is 64 — too small
    /// for deeply-nested inlined lambdas in one function, #147).</summary>
    public const int MaxDepth = 1024;

    /// <summary>Options for JsonDocument.Parse / JsonNode.Parse of a BIR or CIR document.</summary>
    public static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = MaxDepth };

    /// <summary>Options for JsonNode.ToJsonString of a full BIR/CIR document (compact).</summary>
    public static readonly JsonSerializerOptions Writer = new() { MaxDepth = MaxDepth };

    /// <summary>Options for JsonNode.ToJsonString of a full BIR/CIR document (indented).</summary>
    public static readonly JsonSerializerOptions WriterIndented = new() { MaxDepth = MaxDepth, WriteIndented = true };
}
