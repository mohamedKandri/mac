using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MacShell.Core;

// The only place MacShell writes to config.json after creating it: changes made from the UI (the dock's
// "Pin to dock" / "Unpin from dock"). The file is edited as a JSON tree, so every other key stays as the owner
// wrote it; only `//` comments are lost (System.Text.Json can skip comments but can't keep them).
// The normal hot reload then picks up the change, as if the owner had edited the file.
public static class ConfigEditor
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Applies `change` to dock.pinned. `current` is the list in effect (from the defaults when the file has no
    // dock.pinned). Returns an error message, or null when it worked.
    public static string? UpdatePinned(string path, IReadOnlyList<string> current, Func<List<string>, List<string>> change)
    {
        try
        {
            string text = File.Exists(path) ? File.ReadAllText(path) : "{}";
            if (JsonNode.Parse(text, documentOptions: ReadOptions) is not JsonObject root)
                return "config.json isn't a JSON object.";

            var dock = Child(root, "dock") as JsonObject;
            if (dock is null)
            {
                dock = [];
                root["dock"] = dock;
            }
            var updated = change([.. current]);
            Remove(dock, "pinned");
            dock["pinned"] = new JsonArray([.. updated.Select(p => (JsonNode?)JsonValue.Create(p))]);

            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }

    // Keys are matched without case, like the loader does ("Dock" and "dock" are the same key).
    private static JsonNode? Child(JsonObject parent, string name) =>
        parent.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Remove(JsonObject parent, string name)
    {
        foreach (var key in parent.Select(p => p.Key).Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            parent.Remove(key);
    }
}
