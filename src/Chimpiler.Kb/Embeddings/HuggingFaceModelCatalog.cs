using Chimpiler.Kb.Abstractions;

namespace Chimpiler.Kb.Embeddings;

/// <summary>Downloads and manages ONNX embedding models from Hugging Face into a local cache directory.</summary>
public sealed class HuggingFaceModelCatalog : IEmbeddingModelCatalog
{
    public const string BgeSmallEn = "bge-small-en-v1.5";
    public const string NomicEmbedText = "nomic-embed-text-v1.5";

    private static readonly IReadOnlyList<EmbeddingModelDescriptor> Catalog = new[]
    {
        new EmbeddingModelDescriptor(
            BgeSmallEn,
            "BAAI bge-small-en-v1.5 — 384-dim English retrieval model, ~130 MB. Recommended default.",
            384,
            "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main/onnx/model.onnx",
            "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main/vocab.txt"),
        new EmbeddingModelDescriptor(
            NomicEmbedText,
            "Nomic Embed Text v1.5 — 768-dim long-context model, ~550 MB.",
            768,
            "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/onnx/model.onnx",
            "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/vocab.txt")
    };

    private readonly string _rootDirectory;
    private readonly Func<HttpClient> _httpClientFactory;

    public HuggingFaceModelCatalog(string? rootDirectory = null, Func<HttpClient>? httpClientFactory = null)
    {
        _rootDirectory = rootDirectory ?? DefaultRootDirectory();
        _httpClientFactory = httpClientFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
    }

    public IReadOnlyList<EmbeddingModelDescriptor> Available => Catalog;

    public string DefaultModelId => BgeSmallEn;

    /// <summary>Default per-user model cache: ~/.chimpiler/kb/models.</summary>
    public static string DefaultRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".chimpiler",
        "kb",
        "models");

    public EmbeddingModelDescriptor Resolve(string modelId)
    {
        var id = string.Equals(modelId, "default", StringComparison.OrdinalIgnoreCase) ? DefaultModelId : modelId;
        return Catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown embedding model '{modelId}'.", nameof(modelId));
    }

    public string GetModelDirectory(string modelId) => Path.Combine(_rootDirectory, Resolve(modelId).Id);

    public string GetModelPath(string modelId) => Path.Combine(GetModelDirectory(modelId), "model.onnx");

    public string GetVocabPath(string modelId) => Path.Combine(GetModelDirectory(modelId), "vocab.txt");

    public bool IsInstalled(string modelId) =>
        File.Exists(GetModelPath(modelId)) && File.Exists(GetVocabPath(modelId));

    public async Task<string> EnsureInstalledAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = Resolve(modelId);
        var directory = GetModelDirectory(descriptor.Id);
        Directory.CreateDirectory(directory);

        await DownloadIfMissingAsync(descriptor.ModelUrl, GetModelPath(descriptor.Id), cancellationToken).ConfigureAwait(false);
        await DownloadIfMissingAsync(descriptor.TokenizerUrl, GetVocabPath(descriptor.Id), cancellationToken).ConfigureAwait(false);

        return directory;
    }

    public void Remove(string modelId)
    {
        var directory = GetModelDirectory(modelId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task DownloadIfMissingAsync(string url, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            return;
        }

        using var client = _httpClientFactory();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Download to a temp file first so an interrupted download never looks like a valid install.
        var temporary = destination + ".download";
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(temporary))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, destination, overwrite: true);
    }
}
