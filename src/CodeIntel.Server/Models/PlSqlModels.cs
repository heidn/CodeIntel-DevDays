namespace CodeIntel.Server.Models;

public enum PlSqlObjectKind { Unknown, Table, Routine, Package }

public record PlSqlObjectReference(string Name, PlSqlObjectKind Kind);

public record ParsedObjectReferences(
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Routines,
    IReadOnlyList<string> Packages
)
{
    public IEnumerable<PlSqlObjectReference> All()
    {
        foreach (var t in Tables)   yield return new PlSqlObjectReference(t, PlSqlObjectKind.Table);
        foreach (var r in Routines) yield return new PlSqlObjectReference(r, PlSqlObjectKind.Routine);
        foreach (var p in Packages) yield return new PlSqlObjectReference(p, PlSqlObjectKind.Package);
    }

    public static ParsedObjectReferences Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

public record PlSqlResolution(
    string Name,
    PlSqlObjectKind Kind,
    string AbsolutePath,
    string RelativePath,
    string Content,
    string ResolvedVia
);
