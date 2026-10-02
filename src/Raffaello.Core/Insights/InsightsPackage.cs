using Raffaello.Core.Domain;
using Raffaello.Core.Packaging;

namespace Raffaello.Core.Insights;

/// <summary>The "insight checks" page added to every subcontractor invoice package (09_Insight_checks.pdf).</summary>
public static class InsightsPackage
{
    public const string FileName = "09_Insight_checks.pdf";

    /// <summary>Files for one package: the warnings that name the invoice's subcontractor and invoice no., with dismissal reasons.</summary>
    public static IEnumerable<(string Name, string Path, string Note)> Files(PackageRequest req, string workFolder, DateTime stamp, InsightsData data)
    {
        var h = req.Build.Header;
        if (!InvoiceKinds.IsSubcontractor(h)) yield break;
        var hashes = data.FileHashes;   // cached hashes only: building a package must not re-read every photo
        var all = AnomalyDetector.Detect(new AnomalyInputs
        {
            Project = req.Snapshot, Data = data, Documents = InsightsEngine.CollectDocuments(req.Snapshot), Hashes = hashes, Today = stamp,
        });
        var mine = InsightsEngine.ForInvoice(all, h.Subcontractor, h.InvoiceNo);
        var path = Path.Combine(workFolder, FileName);
        InsightsEngine.PackagePdf(path, h.Title, mine, stamp);
        yield return (FileName, path, $"insight checks: {mine.Count(a => !a.IsDismissed)} open warnings, {mine.Count(a => a.IsDismissed)} dismissed");
    }

    /// <summary>A section for <see cref="InvoicePackageBuilder.GlobalSections"/> that reads the insights data when the package is built.</summary>
    public static Func<PackageRequest, string, DateTime, IEnumerable<(string Name, string Path, string Note)>> Section(Func<InsightsData> data) =>
        (req, work, stamp) => Files(req, work, stamp, SafeLoad(data)).ToList();

    private static InsightsData SafeLoad(Func<InsightsData> data)
    {
        try { return data(); } catch (Exception) { return new InsightsData(); }
    }
}
