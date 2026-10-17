namespace MRB_Boostrap.Utilities;

/// <summary>
/// Validation for paths that come from patch archives, hash files, backups and manifests.
/// None of those strings may be used as local paths until they pass through here.
/// </summary>
public static class PathSafety
{
    private static readonly char[] InvalidSegmentChars =
        Path.GetInvalidFileNameChars().Concat([':', '/', '\\', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Converts both separator styles to the platform separator and trims leading/trailing separators.
    /// </summary>
    public static string NormalizeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        return path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// True when <paramref name="name"/> is a single, plain path segment (no separators, no traversal,
    /// no drive or stream syntax, no reserved device name, no trailing dot/space aliasing).
    /// </summary>
    public static bool IsSafeSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name is "." or "..")
            return false;

        if (name.IndexOfAny(InvalidSegmentChars) >= 0)
            return false;

        // Windows silently strips trailing dots and spaces, which would alias another name
        if (name.EndsWith('.') || name.EndsWith(' '))
            return false;

        string stem = name.Split('.')[0];
        return !ReservedDeviceNames.Contains(stem);
    }

    /// <summary>
    /// Builds a validated relative file path from an archive/hash style (directory, file name) pair.
    /// </summary>
    public static bool TryGetRelativeFilePath(string? directory, string? fileName, out string relativePath)
    {
        relativePath = string.Empty;

        if (!IsSafeSegment(fileName))
            return false;

        string dir = NormalizeRelative(directory);
        if (dir.Length > 0 && !dir.Split(Path.DirectorySeparatorChar).All(IsSafeSegment))
            return false;

        relativePath = dir.Length == 0 ? fileName! : Path.Combine(dir, fileName!);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="root"/> and confirms the result
    /// stays strictly inside the root.
    /// </summary>
    public static bool TryResolveUnderRoot(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));

        if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || candidate.Length <= rootFull.Length)
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Combined check: validates the (directory, file name) pair and resolves it under the root.
    /// </summary>
    public static bool TryResolveEntry(string root, string? directory, string? fileName, out string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        return TryGetRelativeFilePath(directory, fileName, out relativePath)
               && TryResolveUnderRoot(root, relativePath, out fullPath);
    }
}
