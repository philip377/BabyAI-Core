using System.Text.Json;
namespace BabyAI.Desktop;

internal static class GingerClaims
{
    // Read-only preflight. The writer's exclusive reservation remains authoritative.
    internal static bool HasReservation(string directory, long process, long table, string document,
        string supplier, int position, string title)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            using var claim = JsonDocument.Parse(File.ReadAllText(file));
            using var identity = JsonDocument.Parse(claim.RootElement.GetProperty("identity").GetString()!);
            var parts = identity.RootElement;
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 6)
                throw new InvalidDataException("Повреждена защитная запись сопоставления; продолжение запрещено.");
            if (parts[0].GetInt64() == process && parts[1].GetInt64() == table &&
                parts[2].GetString() == document && parts[3].GetString() == supplier &&
                parts[4].GetInt32() == position && parts[5].GetString() == title) return true;
        }
        return false;
    }
}
