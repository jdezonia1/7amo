using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Raffaello.Core;
using Raffaello.Core.Imaging;
using Raffaello.Core.Insights;
using Raffaello.Core.Materials;

namespace Raffaello.App.ViewModels.Insights;

/// <summary>
/// [insights] Shared plumbing of the INSIGHTS pages: the insights store (SQLite file or server), the materials snapshot, the Windows
/// image decoder for photo hashes, and the full anomaly run used by the pages, Needs-today and the invoice package.
/// </summary>
public sealed class InsightsHub
{
    private readonly ProjectService _project;
    private readonly IMaterialsStore _materials;

    public InsightsHub(ProjectService project, IInsightsStore store, IMaterialsStore materials)
    {
        _project = project; Store = store; _materials = materials;
    }

    public IInsightsStore Store { get; }

    public InsightsData LoadData()
    {
        try { Store.User = _project.Settings.EffectiveUserName; return Store.Load(); }
        catch (Exception) { return new InsightsData(); }
    }

    public MaterialsSnapshot? LoadMaterials()
    {
        try { return _materials.Load(); } catch (Exception) { return null; }
    }

    /// <summary>Claim anomalies + material + schedule warnings. <paramref name="hashNewFiles"/> = read documents not hashed yet (slow on a big share).</summary>
    public List<Anomaly> Compute(ProjectService p, string? building, bool hashNewFiles, out ReconResult recon, out EvResult ev, InsightsData? data = null, MaterialsSnapshot? mats = null)
    {
        data ??= LoadData();
        mats ??= LoadMaterials();
        var docs = InsightsEngine.CollectDocuments(p.Snapshot);
        var hashes = hashNewFiles ? InsightsEngine.HashDocuments(Store, data, docs, DecodeImage, DateTime.Now) : data.FileHashes;
        recon = MaterialReconciliation.Build(p.Snapshot, mats, data.Norms, building);
        ev = EarnedValue.Build(new EvInputs { Project = p.Snapshot, Data = data, Today = p.Options.Today, ProjectStart = p.ProjectStart, PlannedFinish = p.PlannedFinish, Building = building });
        return InsightsEngine.All(new AnomalyInputs { Project = p.Snapshot, Data = data, Materials = mats, Documents = docs, Hashes = hashes, Building = building, Today = p.Options.Today }, recon, ev);
    }

    /// <summary>"Needs you today" items (cached hashes only, so a reload never reads the document share).</summary>
    public IEnumerable<Raffaello.Core.Queue.QueueItem> QueueItems(ProjectService p) => InsightsEngine.QueueItems(Compute(p, null, false, out _, out _));

    /// <summary>Windows imaging decoder (JPEG, PNG, BMP, TIFF, GIF, HEIC with the codec), downscaled for the perceptual hash.</summary>
    public static RgbaImage? DecodeImage(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource f = dec.Frames[0];
            var scale = Math.Min(1.0, 256.0 / Math.Max(f.PixelWidth, f.PixelHeight));
            if (scale < 1) f = new TransformedBitmap(f, new ScaleTransform(scale, scale));
            var conv = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return new RgbaImage(w, h, px);
        }
        catch (Exception) { return DocumentHashes.DecodePng(path); }
    }
}
