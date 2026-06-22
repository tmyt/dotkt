using System.Collections.Generic;
namespace P {
    public class Item { public Item(string n) { Name = n; } public string Name { get; } }
    // The doc's symptom: a property typed as a GENERIC INTERFACE (like Application.Resources.MergedDictionaries
    // : IList<ResourceDictionary>). `.Add` lives on the inherited ICollection<T>.
    public class Doc { public IList<Item> Items { get; } = new List<Item>(); }
}
