namespace pViewer.Core.Sources;

/// <summary>Una pagina navigabile: un file su disco o una voce di un archivio.</summary>
/// <param name="Name">Nome mostrato (nome file o percorso interno all'archivio).</param>
/// <param name="FilePath">Percorso su disco, null per le pagine dentro un archivio.</param>
public sealed record PageInfo(string Name, string? FilePath);

/// <summary>Elenco ordinato di immagini da sfogliare.</summary>
public interface IImageSource : IDisposable
{
    /// <summary>Cartella o file archivio da cui provengono le pagine.</summary>
    string Location { get; }

    bool IsArchive { get; }

    IReadOnlyList<PageInfo> Pages { get; }

    /// <summary>Legge in memoria i byte della pagina (nessun file resta bloccato).</summary>
    Task<byte[]> ReadAsync(int index, CancellationToken ct = default);
}
