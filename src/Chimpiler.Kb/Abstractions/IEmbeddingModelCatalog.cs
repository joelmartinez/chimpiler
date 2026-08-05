namespace Chimpiler.Kb.Abstractions;

/// <summary>Describes a downloadable local embedding model.</summary>
public sealed record EmbeddingModelDescriptor(
    string Id,
    string Description,
    int Dimension,
    string ModelUrl,
    string TokenizerUrl);

/// <summary>Manages locally installed ONNX embedding models.</summary>
public interface IEmbeddingModelCatalog
{
    /// <summary>Models that can be installed.</summary>
    IReadOnlyList<EmbeddingModelDescriptor> Available { get; }

    /// <summary>The identifier used when the user asks for "default".</summary>
    string DefaultModelId { get; }

    /// <summary>Directory holding the files for a model, whether or not it is installed.</summary>
    string GetModelDirectory(string modelId);

    bool IsInstalled(string modelId);

    /// <summary>Downloads the model files if they are missing. Returns the model directory.</summary>
    Task<string> EnsureInstalledAsync(string modelId, CancellationToken cancellationToken = default);

    void Remove(string modelId);
}
