namespace CodeIntel.Server.Services;

/// <summary>
/// Path-containment check that is not fooled by sibling-prefix names.
/// `C:\repofoo\evil.md` does NOT start with `C:\repo` once a separator is appended,
/// but a naive `StartsWith("C:\\repo")` would accept it.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// Returns true iff <paramref name="candidate"/> is the same path as
    /// <paramref name="root"/> or lies strictly inside it. Both paths are
    /// normalized via <c>Path.GetFullPath</c> before comparison.
    /// </summary>
    public static bool IsInside(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;

        var fullCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var rootWithSep = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
