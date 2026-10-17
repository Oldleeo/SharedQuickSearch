using System.Diagnostics;

namespace SharedQuickSearch;

internal static class ExternalLinks
{
    public const string OldleeProfile = "https://x.com/oldleeoo";

    public static void OpenOldleeProfile() => Process.Start(new ProcessStartInfo(OldleeProfile) { UseShellExecute = true });
}
