using MetadataExtractor;
using pViewer.ViewModels;

namespace pViewer.Services;

public static class MetadataService
{
    /// <summary>Reads EXIF, IPTC, XMP and format data, grouped by section.</summary>
    public static IReadOnlyList<MetadataGroup> Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(ms);
        }
        catch (ImageProcessingException)
        {
            return []; // format MetadataExtractor does not know (QOI, TGA…): simply no metadata
        }
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
