using MetadataExtractor;
using pViewer.ViewModels;

namespace pViewer.Services;

public static class MetadataService
{
    /// <summary>Legge EXIF, IPTC, XMP e i dati del formato, raggruppati per sezione.</summary>
    public static IReadOnlyList<MetadataGroup> Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        var directories = ImageMetadataReader.ReadMetadata(ms);
        var groups = new List<MetadataGroup>();
        foreach (var dir in directories)
        {
            var tags = dir.Tags
                .Where(t => !string.IsNullOrWhiteSpace(t.Description))
                .Select(t => new KeyValuePair<string, string>(t.Name, t.Description!.Trim()))
                .ToList();
            if (tags.Count > 0) groups.Add(new MetadataGroup(dir.Name, tags));
        }
        return groups;
    }
}
