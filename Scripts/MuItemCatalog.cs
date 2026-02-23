using Client.Data.BMD;
using System.Text;

namespace MuGodot;

public sealed class MuItemCatalog
{
	private static readonly HashSet<short> WingIds = new(
	[
		0, 1, 2, 3, 4, 5, 6, 27, 28, 36, 37, 38, 39, 40, 41, 42, 43, 49, 50,
		130, 131, 132, 133, 134, 135, 152, 154, 155, 156, 157, 158, 159, 160,
		172, 173, 174, 175, 176, 177, 178, 180, 181, 182, 183, 184, 185, 186,
		187, 188, 189, 190, 191, 192, 193, 262, 263, 264, 265, 266, 267, 268,
		269, 270, 278, 279, 280, 281, 282, 283, 284, 285, 286, 287, 414, 415,
		416, 417, 418, 419, 420, 421, 422, 423, 424, 425, 426, 427, 428, 429,
		430, 431, 432, 433, 434, 435, 436, 437, 467, 468, 469, 472, 473, 474,
		480, 489, 490, 496
	]);

	public sealed record ItemDef(byte Group, short Id, string Name, string TexturePath);

	private readonly Dictionary<(byte Group, short Id), ItemDef> _byKey = new();

	public IReadOnlyList<ItemDef> Weapons { get; private set; } = Array.Empty<ItemDef>();
	public IReadOnlyList<ItemDef> Armors { get; private set; } = Array.Empty<ItemDef>();
	public IReadOnlyList<ItemDef> Wings { get; private set; } = Array.Empty<ItemDef>();

	public ItemDef? Get(byte group, short id)
	{
		return _byKey.TryGetValue((group, id), out var item) ? item : null;
	}

	public static async Task<MuItemCatalog> LoadAsync(string dataPath)
	{
		var catalog = new MuItemCatalog();
		string itemsPath = System.IO.Path.Combine(dataPath, "Local", "item.bmd");
		if (!System.IO.File.Exists(itemsPath))
			return catalog;

		var reader = new ItemBMDReader();
		List<ItemBMD> items;
		try
		{
			items = await reader.Load(itemsPath);
		}
		catch
		{
			return catalog;
		}

		var weapons = new List<ItemDef>();
		var armors = new List<ItemDef>();
		var wings = new List<ItemDef>();

		for (int i = 0; i < items.Count; i++)
		{
			var item = items[i];
			byte group = (byte)item.ItemSubGroup;
			short id = (short)item.ItemSubIndex;
			string modelPath = BuildModelPath(item.szModelFolder, item.szModelName);
			if (string.IsNullOrEmpty(modelPath))
				continue;

			string name = DecodeName(item.szItemName);
			if (string.IsNullOrWhiteSpace(name))
				name = $"{group}:{id}";

			var def = new ItemDef(group, id, name, modelPath);
			if (catalog._byKey.ContainsKey((group, id)))
				continue;

			catalog._byKey.Add((group, id), def);
			if (group <= 6)
				weapons.Add(def);
			if (group == 8)
				armors.Add(def);
			if (group == 12 && WingIds.Contains(id))
				wings.Add(def);
		}

		catalog.Weapons = weapons.OrderBy(p => p.Group).ThenBy(p => p.Id).ToList();
		catalog.Armors = armors.OrderBy(p => p.Id).ToList();
		catalog.Wings = wings.OrderBy(p => p.Id).ToList();
		return catalog;
	}

	private static string BuildModelPath(string folder, string modelName)
	{
		if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(modelName))
			return string.Empty;

		string cleanFolder = folder
			.Replace("Data\\", string.Empty, StringComparison.OrdinalIgnoreCase)
			.Replace("Data/", string.Empty, StringComparison.OrdinalIgnoreCase)
			.Trim();
		string cleanModelName = modelName.Trim();
		string combined = System.IO.Path.Combine(cleanFolder, cleanModelName);
		return combined.Replace("\\", "/");
	}

	private static string DecodeName(byte[] raw)
	{
		if (raw == null || raw.Length == 0)
			return string.Empty;

		int len = Array.IndexOf(raw, (byte)0);
		if (len < 0)
			len = raw.Length;

		if (len == 0)
			return string.Empty;

		// Item names in common MU clients are usually ANSI-compatible in item.bmd.
		return Encoding.Default.GetString(raw, 0, len).Trim();
	}
}
