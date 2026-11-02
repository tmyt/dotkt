// Explicit physical identity and encoded version attributes selected by the build driver.
sealed record AssemblyVersions(Version Identity, IReadOnlyList<(string TypeName, byte[] Blob)> Attributes);
