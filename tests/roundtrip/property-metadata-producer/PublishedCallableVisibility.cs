using roundtrip.publishedcallables;

namespace InheritedCallableAdapter;

public interface ISecret { string secret { get; } }
public interface ISecretField { string secretField { get; } }
public interface IPublicValue { string publicValue { get; } }
public interface IPublicField { string publicField { get; } }

public class Derived : InheritedVisibilityBase, ISecret, ISecretField, IPublicValue, IPublicField
{
    string ISecret.secret => "property-slot";
    string ISecretField.secretField => "field-slot";
    string IPublicValue.publicValue => "public-property-slot";
    string IPublicField.publicField => "public-field-slot";
}
