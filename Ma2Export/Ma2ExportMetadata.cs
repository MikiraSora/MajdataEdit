using System.Globalization;

namespace MajdataEdit.Ma2Export;

public static class Ma2ExportMetadata
{
    public static string GetMusicId6(string? otherCommands)
    {
        var id = GetCommandValue(otherCommands, "id");
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !char.IsDigit(c)))
        {
            return "000000";
        }

        id = id.Trim();
        return id.Length <= 6 ? id.PadLeft(6, '0') : id;
    }

    public static float? GetWholeBpm(string? otherCommands)
    {
        var value = GetCommandValue(otherCommands, "wholebpm");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantBpm) &&
            invariantBpm > 0)
        {
            return invariantBpm;
        }

        if (float.TryParse(value, out var currentBpm) && currentBpm > 0)
        {
            return currentBpm;
        }

        return null;
    }

    private static string? GetCommandValue(string? otherCommands, string name)
    {
        if (string.IsNullOrWhiteSpace(otherCommands))
        {
            return null;
        }

        foreach (var rawLine in otherCommands.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var prefix = "&" + name + "=";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }
}
