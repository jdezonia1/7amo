namespace Raffaello.Core.Settings;

/// <summary>
/// [phase6] One data / documents folder for every module. <see cref="AppSettings.DocumentsRoot"/> is the root on the shared
/// partition (e.g. D:\RAFFLES ELEC\RAFFAELLO); each module folder defaults to a sub-folder unless set explicitly.
/// </summary>
public sealed record ProjectFolders(string Root, string Wir, string Mir, string Other, string Packages, string VariationDocs, string AconexScreenshots, string Statements, string Exports)
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Raffaello");

    public static ProjectFolders From(AppSettings s)
    {
        var root = string.IsNullOrWhiteSpace(s.DocumentsRoot) ? DefaultRoot : Environment.ExpandEnvironmentVariables(s.DocumentsRoot.Trim());
        string Or(string value, string sub) => string.IsNullOrWhiteSpace(value) ? Path.Combine(root, sub) : Environment.ExpandEnvironmentVariables(value.Trim());
        return new ProjectFolders(root,
            Wir: Or(s.WirFolder, "WIR"),
            Mir: Path.Combine(root, "MIR"),
            Other: Path.Combine(root, "Aconex", "Other"),
            Packages: Or(s.PackageOutputFolder, "Packages"),
            VariationDocs: Or(s.VariationDocsFolder, "Variations"),
            AconexScreenshots: Path.Combine(root, "Aconex", "Screenshots"),
            Statements: Path.Combine(root, "Site statements"),
            Exports: Path.Combine(root, "Exports"));
    }

    public IEnumerable<(string Name, string Path)> All => new[]
    {
        ("WIR", Wir), ("MIR", Mir), ("ACONEX OTHER", Other), ("PACKAGES", Packages), ("VARIATION DOCUMENTS", VariationDocs),
        ("ACONEX SCREENSHOTS", AconexScreenshots), ("SITE STATEMENTS", Statements), ("EXPORTS", Exports),
    };

    /// <summary>Creates the folders that do not exist yet (first-run wizard). Returns the problems (e.g. drive not available).</summary>
    public List<string> Ensure()
    {
        var problems = new List<string>();
        foreach (var (name, path) in All)
            try { Directory.CreateDirectory(path); }
            catch (Exception ex) { problems.Add($"{name}: {path} - {ex.Message}"); }
        return problems;
    }
}
