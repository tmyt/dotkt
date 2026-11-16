using System;
using System.Collections.Generic;
using DotKt.Toolchain;

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex DefaultDemandTestIndex(ReferenceDotKtMetadata metadata) => new(
        new List<ReferenceAssembly> { new("default-demand.dll", "default-demand", "1", metadata) },
        ManagedReferenceCatalog.Create(Array.Empty<string>(), "bir2cir"));
}
