using System.Text.Json;
using MasterDocumentation.Models;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Services;

public sealed record EmergencyDraft(long DocumentId, string Title, string Json, string Html, string PlainText, DateTime UpdatedAt);

public static class DraftRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static string DraftDirectory => Path.Combine(AppPaths.Temp, "Drafts");

    public static void Write(DocumentTab tab)
    {
        if (tab.DocumentId <= 0 || !tab.IsDirty) return;
        Directory.CreateDirectory(DraftDirectory);
        var path = PathFor(tab.DocumentId);
        var temporary = path + ".tmp";
        var draft = new EmergencyDraft(tab.DocumentId, tab.Title, tab.EditorJson, tab.Html, tab.PlainText, DateTime.UtcNow);
        File.WriteAllText(temporary, JsonSerializer.Serialize(draft, JsonOptions));
        File.Move(temporary, path, true);
    }

    public static IReadOnlyList<EmergencyDraft> ReadAll()
    {
        if (!Directory.Exists(DraftDirectory)) return [];
        var result = new List<EmergencyDraft>();
        foreach (var path in Directory.EnumerateFiles(DraftDirectory, "*.json"))
        {
            try
            {
                var value = JsonSerializer.Deserialize<EmergencyDraft>(File.ReadAllText(path), JsonOptions);
                if (value is not null) result.Add(value);
            }
            catch (Exception ex)
            {
                LogService.Error("Не удалось прочитать аварийный черновик " + path, ex);
            }
        }
        return result.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public static void Delete(long documentId)
    {
        try { File.Delete(PathFor(documentId)); }
        catch (Exception ex) { LogService.Error("Не удалось удалить сохранённый аварийный черновик", ex); }
    }

    private static string PathFor(long documentId) => Path.Combine(DraftDirectory, documentId + ".json");
}
