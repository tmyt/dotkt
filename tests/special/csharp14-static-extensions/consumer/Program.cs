using System;
using CSharp14StaticExtensions;

Alpha.Mutable = 7;
var result = Alpha.Answer() + Beta.Answer() + Alpha.Mutable + Alpha.Select(3) + Alpha.Select("x").Length + Alpha.Label.Length;
var generic = GenericTarget<string>.TypeName();
if (result != 142 || generic != "String")
    throw new InvalidOperationException($"unexpected static extension result: {result}, {generic}");
Console.WriteLine("csharp14-static-extensions");
