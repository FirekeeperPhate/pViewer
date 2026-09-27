using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;

namespace pViewer.Services;

public enum BatchOperation
{
    Resize, RotateRight, RotateLeft, FlipHorizontal, FlipVertical, Convert, Grayscale, Invert, BlackBorder, WhiteBorder,
}

/// <param name="ResizePercent">Percentage; 0 = use MaxWidth×MaxHeight (shrinks without distortion).</param>
/// <param name="ConvertExtension">Target extension for <see cref="BatchOperation.Convert"/>, e.g. ".png".</param>
public sealed record BatchJob(
    BatchOperation Operation, string OutputFolder, int ResizePercent, int MaxWidth, int MaxHeight,
    string? ConvertExtension, int JpegQuality, int BorderThickness, bool AutoOrient);

public sealed record BatchProgress(int Done, int Total, string Current);

public sealed record BatchResult(int Written, int Skipped, IReadOnlyList<string> Errors, bool Cancelled);

/// <summary>
/// Applies an operation to every page of a source (folder or archive) and writes the results
/// to a separate folder: originals are never touched or overwritten.
/// </summary>
public static class BatchProcessor
{
    public static string DefaultFolderName(BatchOperation op) => op switch
    {
        BatchOperation.Resize => "Resized",
        BatchOperation.RotateRight or BatchOperation.RotateLeft => "Rotated",
        BatchOperation.FlipHorizontal or BatchOperation.FlipVertical => "Flipped",
        BatchOperation.Convert => "Converted",
        BatchOperation.Grayscale => "Grayscale",
        BatchOperation.Invert => "Inverted",
        _ => "Bordered",
    };

    public static Task<BatchResult> RunAsync(IImageSource source, BatchJob job, IProgress<BatchProgress>? progress, CancellationToken ct) =>
        // A single STA thread for the whole job: the WIC and WPF encoders prefer it.
        StaTask.Run(() => Run(source, job, progress, ct));

    private static BatchResult Run(IImageSource source, BatchJob job, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(job.OutputFolder);
        int written = 0, skipped = 0, total = source.Pages.Count;
        var errors = new List<string>();
        // Output names already used by this run: pages with the same name in different archive
        // folders, or a.jpg + a.png converted to the same format, get " (2)", " (3)"…
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) return new BatchResult(written, skipped, errors, true);
            string name = Path.GetFileName(source.Pages[i].Name.Replace('/', '\\'));
            progress?.Report(new BatchProgress(i, total, name));

            string ext = job.Operation == BatchOperation.Convert && job.ConvertExtension is not null
                ? job.ConvertExtension
                : Path.GetExtension(name).ToLowerInvariant();
            if (!ImageFormats.CanSave("x" + ext)) ext = ".png";
            string baseName = Path.GetFileNameWithoutExtension(name);
            string target = Path.Combine(job.OutputFolder, baseName + ext);
            for (int n = 2; used.Contains(target); n++)
                target = Path.Combine(job.OutputFolder, $"{baseName} ({n}){ext}");
            used.Add(target);
            if (File.Exists(target))
            {
                skipped++; // left by an earlier run: never overwritten
                continue;
            }

            try
            {
                byte[] bytes = source.ReadAsync(i, ct).GetAwaiter().GetResult();
                var loaded = ImageDecoder.Decode(bytes, name, job.AutoOrient);
                var result = Apply(job, loaded.Bitmap);
                var meta = ImageFormats.IsJpeg(target) ? loaded.JpegMetadata : null;
                ImageSaver.Save(result, target, job.JpegQuality, meta, resetOrientation: job.AutoOrient);
                written++;
            }
            catch (OperationCanceledException)
            {
                return new BatchResult(written, skipped, errors, true);
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        progress?.Report(new BatchProgress(total, total, ""));
        return new BatchResult(written, skipped, errors, false);
    }

    public static BitmapSource Apply(BatchJob job, BitmapSource src) => job.Operation switch
    {
        BatchOperation.Resize when job.ResizePercent > 0 => ImageOps.Resize(src,
            Math.Max(1, src.PixelWidth * job.ResizePercent / 100), Math.Max(1, src.PixelHeight * job.ResizePercent / 100)),
        BatchOperation.Resize => ImageOps.FitWithin(src, job.MaxWidth, job.MaxHeight),
        BatchOperation.RotateRight => ImageOps.Rotate(src, 90),
        BatchOperation.RotateLeft => ImageOps.Rotate(src, 270),
        BatchOperation.FlipHorizontal => ImageOps.Flip(src, true),
        BatchOperation.FlipVertical => ImageOps.Flip(src, false),
        BatchOperation.Grayscale => ImageOps.Grayscale(src),
        BatchOperation.Invert => ImageOps.Invert(src),
        BatchOperation.BlackBorder => ImageOps.AddBorder(src, job.BorderThickness, Colors.Black),
        BatchOperation.WhiteBorder => ImageOps.AddBorder(src, job.BorderThickness, Colors.White),
        _ => src, // Convert: only the format changes
    };
}
