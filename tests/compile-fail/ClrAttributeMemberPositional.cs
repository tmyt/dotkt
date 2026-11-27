namespace AttributeArguments;

public class ChoiceAttribute : System.Attribute
{
    public ChoiceAttribute() { }
    public ChoiceAttribute(string text) { }
    public string Name { get; set; }
}

public class FlagAttribute : System.Attribute
{
    public FlagAttribute(int code) { }
    public bool Enabled;
}

public class IntsAttribute : System.Attribute
{
    public int[] Values { get; set; }
}
