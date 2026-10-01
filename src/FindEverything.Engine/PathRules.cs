namespace FindEverything.Engine;

public static class PathRules
{
    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectDeviceNamespace(path);
        var fullPath = Path.GetFullPath(path);
        RejectDeviceNamespace(fullPath);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void RejectDeviceNamespace(string path)
    {
        if (OperatingSystem.IsWindows() &&
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
             path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
             path.StartsWith(@"\??\", StringComparison.Ordinal)))
            throw new ArgumentException("Windows device namespace paths are not supported. Use an ordinary drive or UNC source path.", nameof(path));
    }

    public static string Key(string path)
    {
        var fullPath = Normalize(path);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    public static bool IsWithin(string candidate, string root)
    {
        candidate = Normalize(candidate);
        root = Normalize(root);
        if (candidate.Equals(root, Comparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, Comparison);
    }

    // Reject existing symbolic links/junctions in configured paths. This prevents
    // ordinary accidental aliases, not malicious concurrent link replacement.
    public static void EnsureNoReparseAncestors(string path)
    {
        var current = Normalize(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Symbolic links and reparse points are not allowed in configured paths: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}
