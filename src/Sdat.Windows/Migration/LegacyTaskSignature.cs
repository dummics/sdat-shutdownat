namespace Sdat.Windows.Migration;

internal static class LegacyTaskSignature
{
    public static bool IsVerified(string? applicationPath, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(applicationPath) || string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        if (!Path.GetFileName(applicationPath).Equals("wscript.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            arguments,
            "^//B\\s+//NoLogo\\s+\"(?<launcher>[^\"]+\\\\lib\\\\RunHidden\\.vbs)\"\\s+\"(?<script>[^\"]+\\\\shutdownat\\.ps1)\"\\s+-(?:RunVolatile|RunPermanent)(?:\\s+-Profile\\s+[^\\s]+)?(?:\\s+-Suspend)?(?:\\s+-Restart)?(?:\\s+-DryRun)?\\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        try
        {
            var launcherRoot = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(match.Groups["launcher"].Value))!)?.FullName;
            var scriptRoot = Path.GetDirectoryName(Path.GetFullPath(match.Groups["script"].Value));
            return launcherRoot is not null &&
                   string.Equals(launcherRoot, scriptRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
