using pViewer.ViewModels;

namespace pViewer.Services;

public static class BatchRenamer
{
    /// <summary>Final name of the n-th file: «base-001.jpg».</summary>
    public static string TargetName(BatchRenameOptions o, int index, string extension) =>
        $"{o.BaseName}-{(o.Start + index).ToString().PadLeft(o.Digits, '0')}{extension}";

    /// <summary>
    /// Renames the files in the given order. Works in two passes (temporary names first, then the
    /// final ones) so swapping names between files in the list causes no conflicts.
    /// Never overwrites files outside the list.
    /// </summary>
    /// <returns>Map of old path → new path of the renamed files.</returns>
    public static Dictionary<string, string> Rename(IReadOnlyList<string> files, BatchRenameOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseName) || options.BaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid base name.");

        var plan = files.Select((f, i) => (Old: f, New: Path.Combine(Path.GetDirectoryName(f)!,
            TargetName(options, i, Path.GetExtension(f).ToLowerInvariant())))).ToList();

        var sources = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var clash = plan.FirstOrDefault(p => File.Exists(p.New) && !sources.Contains(p.New));
        if (clash.New is not null)
            throw new IOException($"«{Path.GetFileName(clash.New)}» already exists and is not part of the list.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var temps = new List<(string Old, string Temp, string New)>();
        try
        {
            foreach (var (oldPath, newPath) in plan)
            {
                if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) continue;
                string temp = Path.Combine(Path.GetDirectoryName(oldPath)!, $".pvren_{Guid.NewGuid():N}{Path.GetExtension(oldPath)}");
                File.Move(oldPath, temp);
                temps.Add((oldPath, temp, newPath));
            }
            foreach (var (oldPath, temp, newPath) in temps)
            {
                File.Move(temp, newPath);
                result[oldPath] = newPath;
            }
        }
        catch
        {
            // Restore the files left with a temporary name.
            foreach (var (oldPath, temp, _) in temps)
            {
                if (!File.Exists(temp)) continue;
                try { File.Move(temp, File.Exists(oldPath) ? temp : oldPath); } catch (IOException) { }
            }
            throw;
        }
        return result;
    }
}
