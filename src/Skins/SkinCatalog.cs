using System.Text.Json;

namespace FragLab.Skins;

public sealed class SkinItemDef
{
    public int Defindex { get; init; }
    public string Name { get; init; } = "";
    public string ClassName { get; init; } = "";

    /// <summary>pistol, rifle, smg, heavy, equipment, knife or gloves.</summary>
    public string Category { get; init; } = "";
}

public sealed class SkinPaintDef
{
    public int Defindex { get; init; }
    public int Paint { get; init; }
    public string Name { get; init; } = "";
    public string Rarity { get; init; } = "#FFFFFF";
    public bool Legacy { get; init; }
    public float MinFloat { get; init; }
    public float MaxFloat { get; init; } = 1f;
}

/// <summary>All weapons, knives, gloves and paint kits, loaded from data/skins.json (see scripts/generate-skins.ps1).</summary>
public sealed class SkinCatalog
{
    private Dictionary<int, SkinItemDef> _items = new();
    private Dictionary<int, List<SkinPaintDef>> _skins = new();

    public int SkinCount { get; private set; }

    public void Load(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), options)
                   ?? throw new InvalidDataException($"Could not read {path}");

        _items = file.Items.ToDictionary(i => i.Defindex);
        _skins = file.Skins.GroupBy(s => s.Defindex)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Name).ToList());
        SkinCount = file.Skins.Count;
    }

    public SkinItemDef? Item(int defindex) => _items.GetValueOrDefault(defindex);

    public SkinItemDef? ItemByClass(string className) => _items.Values.FirstOrDefault(i => i.ClassName == className);

    public IEnumerable<SkinItemDef> ItemsIn(string category) =>
        _items.Values.Where(i => i.Category == category).OrderBy(i => i.Name);

    public IReadOnlyList<SkinPaintDef> SkinsFor(int defindex) =>
        _skins.TryGetValue(defindex, out var list) ? list : [];

    public SkinPaintDef? Skin(int defindex, int paint) =>
        SkinsFor(defindex).FirstOrDefault(s => s.Paint == paint);

    public static bool IsKnifeClass(string designerName) =>
        designerName.Contains("knife") || designerName.Contains("bayonet");

    private sealed class CatalogFile
    {
        public List<SkinItemDef> Items { get; init; } = [];
        public List<SkinPaintDef> Skins { get; init; } = [];
    }
}
