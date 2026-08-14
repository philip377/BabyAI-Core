using System.IO;
using IOPath = System.IO.Path;

namespace BabyAI.Setup;

internal static class InstallSlotAllocator
{
    internal static string Allocate(string versionsRoot, string version)
    {
        Directory.CreateDirectory(versionsRoot);
        var safeVersion = ValidateVersionSegment(version);
        var preferred = IOPath.Combine(versionsRoot, safeVersion);
        if (!Directory.Exists(preferred) && !File.Exists(preferred))
            return preferred;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var suffix = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var candidate = IOPath.Combine(versionsRoot, $"{safeVersion}+{suffix}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        throw new IOException("Не удалось выделить уникальную папку для новой версии BabyAI.");
    }

    private static string ValidateVersionSegment(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("Версия BabyAI не задана.");

        var trimmed = version.Trim();
        if (trimmed is "." or ".." ||
            trimmed.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains(IOPath.DirectorySeparatorChar) ||
            trimmed.Contains(IOPath.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException("Версия BabyAI содержит недопустимые символы пути.");
        }

        return trimmed;
    }
}
