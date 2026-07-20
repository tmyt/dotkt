// Producer source for the migrated il-clriface case. A property typed as a GENERIC INTERFACE (like
// Application.Resources.MergedDictionaries : IList<ResourceDictionary>); `.Add` lives on the inherited
// ICollection<T>. Own namespace so its `Item` does not collide with the other cases' `Item`.
using System.Collections.Generic;
namespace ClrIface {
    public class Item { public Item(string n) { Name = n; } public string Name { get; } }
    public class Doc { public IList<Item> Items { get; } = new List<Item>(); }
}
