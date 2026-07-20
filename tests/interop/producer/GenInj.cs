// Producer source for the migrated il-geninj case. A property typed as a CONSTRUCTED GENERIC (List<Item>) must
// resolve through the injected open List<T> applied to the injected Item. Own namespace.
using System.Collections.Generic;
namespace GenInj {
    public class Item { public Item(string n) { Name = n; } public string Name { get; } }
    public class Bag { public List<Item> Items { get; } = new List<Item>(); }
}
