using System.Text.Json;
using System.Text.Json.Nodes;
using BabyAI.Desktop;

internal static class DeferredSelectionTests
{
    private static readonly GingerDeferredRow Target = new(new string('A', 64), "chat-original", "Supplier", "Doc", 2,
        "Expected full product title", GingerDeferredRows.NoCandidatesReason, "C:\\old-observation-never-used", DateTimeOffset.UtcNow);
    private static readonly GingerChatItem Current = new("Supplier", "Doc", 1, "Current row", false, []);

    internal static void Run()
    {
        var fresh = Before();
        GingerDeferredSelection.ValidateBeforeSelection(Target, Current, fresh, 123, 456, 2);
        var selected = Current with { Position = 2, Title = Target.Title };
        GingerDeferredSelection.ValidateSelected(Target, selected);
        GingerDeferredSelection.ValidateSelected(Target, selected with { Linked = true, Candidates = ["Fresh candidate"] });

        foreach (var target in new[] { Target with { Supplier = "Other" }, Target with { Document = "Other" },
            Target with { Position = 0 }, Target with { Position = 3 }, Target with { Title = "Expected full product" },
            Target with { Title = Target.Title.ToLowerInvariant() } })
            Refused(() => GingerDeferredSelection.ValidateBeforeSelection(target, Current, fresh, 123, 456, 2));
        foreach (var current in new[] { Current with { Supplier = "Other" }, Current with { Document = "Other" },
            Current with { Position = 0 }, Current with { Position = 2 }, Current with { Title = "Changed current row" } })
            Refused(() => GingerDeferredSelection.ValidateBeforeSelection(Target, current, fresh, 123, 456, 2));
        foreach (var (process, table, count) in new[] { (0L, 456L, 2), (124L, 456L, 2),
            (123L, 0L, 2), (123L, 457L, 2), (123L, 456L, 1), (123L, 456L, 501) })
            Refused(() => GingerDeferredSelection.ValidateBeforeSelection(Target, Current, fresh, process, table, count));

        foreach (var change in new Action<JsonObject>[] {
            value => value.Remove("process_id"), value => value["process_id"] = 124,
            value => value["process_id"] = "123", value => value["rows"] = new JsonObject(),
            value => value["rows"]!.AsArray().RemoveAt(1),
            value => Observation(value, 1)["Title"] = "Different target",
            value => Observation(value, 1)["Title"] = null,
            value => Observation(value, 1)["Supplier"] = "Other supplier",
            value => Observation(value, 1)["DocumentNumber"] = "Other document",
            value => Observation(value, 1)["ObservedRowPosition"] = 1,
            value => Observation(value, 1)["ObservedRowPosition"] = "2",
            value => Observation(value, 1)["TableHandle"] = 789,
            value => Observation(value, 0)["TableHandle"] = "456",
            value => Observation(value, 0)["Title"] = "Changed anchor",
            value => Observation(value, 0)["DocumentNumber"] = "Changed anchor document",
            value => value["rows"]![1]! ["Selected"] = true,
            value => value["rows"]![0]! ["Selected"] = false,
            value => value["rows"]![0]! ["Selected"] = "true",
            value => value["rows"]![1]! ["Observation"] = null,
        })
        {
            var changed = JsonNode.Parse(fresh.GetRawText())!.AsObject();
            change(changed);
            Refused(() => GingerDeferredSelection.ValidateBeforeSelection(Target, Current,
                JsonSerializer.SerializeToElement(changed), 123, 456, 2));
        }
        foreach (var duplicate in new[] {
            fresh.GetRawText().Replace("\"process_id\":123", "\"process_id\":123,\"process_id\":123", StringComparison.Ordinal),
            fresh.GetRawText().Replace("\"Title\":\"Expected full product title\"",
                "\"Title\":\"Expected full product title\",\"Title\":\"Expected full product title\"", StringComparison.Ordinal),
        })
        {
            using var corrupted = JsonDocument.Parse(duplicate);
            Refused(() => GingerDeferredSelection.ValidateBeforeSelection(Target, Current, corrupted.RootElement, 123, 456, 2));
        }
        foreach (var wrongSelection in new[] { selected with { Supplier = "Other" }, selected with { Document = "Other" },
            selected with { Position = 1 }, selected with { Title = "Expected full product" } })
            Refused(() => GingerDeferredSelection.ValidateSelected(Target, wrongSelection));

        Console.WriteLine("Ginger deferred selection: fresh process/table/document/full-title validation before selection and exact selected-row checks passed; Ginger was not started.");
    }

    private static JsonObject Observation(JsonObject value, int index) => value["rows"]![index]!["Observation"]!.AsObject();

    private static void Refused(Action validate)
    {
        var selections = 0;
        try { validate(); selections++; }
        catch (InvalidDataException) { }
        if (selections != 0) throw new Exception("Changed or malformed observation was allowed to reach selection");
    }

    private static JsonElement Before() => JsonSerializer.SerializeToElement(new {
        process_id = 123L,
        rows = new[] {
            new { Selected = true, Observation = new { TableHandle = 456L, ObservedRowPosition = 1,
                Supplier = Current.Supplier, DocumentNumber = Current.Document, Title = Current.Title,
                PriceText = "100", LinkedProductText = "<не указано>" } },
            new { Selected = false, Observation = new { TableHandle = 456L, ObservedRowPosition = 2,
                Supplier = Target.Supplier, DocumentNumber = Target.Document, Title = Target.Title,
                PriceText = "200", LinkedProductText = "<не указано>" } },
        },
    });
}
