using System.IO;
using System.Text.Json;

namespace SharedQuickSearch;

public sealed record UpdateHealthReceipt(string Token, string Version, int ProcessId);

/// <summary>One-use startup acknowledgement, bound to an exact process, version and random token.</summary>
public static class UpdateHealth
{
    public static bool IsValidLocation(string receiptPath, string token, string updatesFolder)
    {
        try
        {
            return Guid.TryParseExact(token, "N", out _) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(receiptPath)), Path.GetFullPath(updatesFolder), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(receiptPath), $"health-{token}.json", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static void Acknowledge(string receiptPath, string token, string updatesFolder, string version, int processId)
    {
        if (!IsValidLocation(receiptPath, token, updatesFolder)) return;
        Directory.CreateDirectory(updatesFolder);
        var staging = receiptPath + ".new";
        File.WriteAllText(staging, JsonSerializer.Serialize(new UpdateHealthReceipt(token, version, processId)));
        File.Move(staging, receiptPath, overwrite: true);
    }

    public static bool Matches(string receiptPath, string token, string version, int processId)
    {
        try
        {
            if (new FileInfo(receiptPath).Length > 4096) return false;
            var receipt = JsonSerializer.Deserialize<UpdateHealthReceipt>(File.ReadAllText(receiptPath));
            return receipt is not null && receipt.Token == token && receipt.ProcessId == processId &&
                UpdateRules.Compare(receipt.Version, version) == 0;
        }
        catch { return false; }
    }
}
