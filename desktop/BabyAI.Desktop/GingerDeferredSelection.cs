using System.Text.Json;

namespace BabyAI.Desktop;

/// <summary>Verifies fresh observation data before navigation; a reminder never authorizes a match.</summary>
internal static class GingerDeferredSelection
{
    internal static void ValidateBeforeSelection(GingerDeferredRow target, GingerChatItem current,
        JsonElement before, long processId, long tableHandle, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);
        if (processId <= 0 || tableHandle <= 0 || rowCount is < 1 or > 500 ||
            target.Position < 1 || target.Position > rowCount || current.Position < 1 || current.Position > rowCount ||
            string.IsNullOrWhiteSpace(target.Supplier) || string.IsNullOrWhiteSpace(target.Document) ||
            string.IsNullOrWhiteSpace(target.Title) || current.Supplier != target.Supplier || current.Document != target.Document)
            throw Changed();
        var process = Required(before, "process_id");
        if (process.ValueKind != JsonValueKind.Number || !process.TryGetInt64(out var observedProcess) || observedProcess != processId)
            throw Changed();
        var rows = Required(before, "rows");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != rowCount) throw Changed();
        var selectedCount = 0;
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var observation = Required(row, "Observation");
            var position = Required(observation, "ObservedRowPosition");
            var table = Required(observation, "TableHandle");
            var title = Required(observation, "Title");
            if (position.ValueKind != JsonValueKind.Number || !position.TryGetInt32(out var observedPosition) || observedPosition != ++index ||
                table.ValueKind != JsonValueKind.Number || !table.TryGetInt64(out var observedTable) || observedTable != tableHandle ||
                Text(observation, "Supplier") != target.Supplier || Text(observation, "DocumentNumber") != target.Document ||
                title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString()))
                throw Changed();
            if (observedPosition == target.Position && title.GetString() != target.Title) throw Changed();
            var selected = Required(row, "Selected");
            if (selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Changed();
            if (selected.GetBoolean())
            {
                selectedCount++;
                if (observedPosition != current.Position || title.GetString() != current.Title) throw Changed();
            }
        }
        if (selectedCount != 1) throw Changed();
    }

    internal static void ValidateSelected(GingerDeferredRow target, GingerChatItem selected)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Supplier != target.Supplier || selected.Document != target.Document ||
            selected.Position != target.Position || selected.Title != target.Title)
            throw Changed();
    }

    private static string? Text(JsonElement value, string name)
    {
        var property = Required(value, name);
        if (property.ValueKind != JsonValueKind.String) throw Changed();
        return property.GetString();
    }

    private static JsonElement Required(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Changed();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? found = null;
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Changed();
            if (property.Name == name) found = property.Value;
        }
        return found ?? throw Changed();
    }

    private static InvalidDataException Changed() => new(
        "Отложенная позиция не совпадает с текущим прайсом. Проверь поставщика, документ, номер строки и полное название товара.");
}
