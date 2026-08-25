namespace DotKt.Toolchain;

/// <summary>
/// Expands UTF-8 <c>@argfile</c> arguments using the same token grammar as the
/// Kotlin compiler: whitespace separates arguments, either quote character may
/// delimit one argument, and a backslash inside quotes escapes the next
/// character. Expansion is deliberately one level, matching kotc.
/// </summary>
internal static class ResponseFileArguments
{
    public static string[] Expand(IEnumerable<string> arguments) =>
        arguments.SelectMany(argument =>
            argument.StartsWith('@')
                ? Parse(File.ReadAllText(argument[1..]))
                : new[] { argument })
            .ToArray();

    static IEnumerable<string> Parse(string content)
    {
        var index = 0;
        while (true)
        {
            while (index < content.Length && char.IsWhiteSpace(content[index])) index++;
            if (index == content.Length) yield break;

            var argument = new System.Text.StringBuilder();
            while (index < content.Length)
            {
                var current = content[index++];
                if (char.IsWhiteSpace(current)) break;
                if (current is '\'' or '"')
                {
                    var quote = current;
                    while (index < content.Length && content[index] != quote)
                    {
                        current = content[index++];
                        if (current == '\\' && index < content.Length)
                            current = content[index++];
                        argument.Append(current);
                    }
                    if (index < content.Length) index++;
                    yield return argument.ToString();
                    goto NextArgument;
                }
                argument.Append(current);
            }

            if (argument.Length != 0) yield return argument.ToString();
        NextArgument:;
        }
    }
}
