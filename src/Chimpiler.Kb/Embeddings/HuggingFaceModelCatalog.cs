using Chimpiler.Kb.Abstractions;
using System.Security.Cryptography;

namespace Chimpiler.Kb.Embeddings;

/// <summary>Downloads and manages ONNX embedding models from Hugging Face into a local cache directory.</summary>
public sealed class HuggingFaceModelCatalog : IEmbeddingModelCatalog
{
    public const string BgeSmallEn = "bge-small-en-v1.5";
    private const string BgeSmallEnRevision = "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a";

    private static readonly IReadOnlyList<EmbeddingModelDescriptor> Catalog = new[]
    {
        new EmbeddingModelDescriptor(
            BgeSmallEn,
            "BAAI bge-small-en-v1.5 — 384-dim English retrieval model, ~130 MB. Recommended default.",
            384,
            512,
            EmbeddingPooling.Cls,
            null,
            null,
            $"https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/{BgeSmallEnRevision}/onnx/model.onnx",
            "828E1496D7FABB79CFA4DCD84FA38625C0D3D21DA474A00F08DB0F559940CF35",
            $"https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/{BgeSmallEnRevision}/vocab.txt",
            "07ECED375CEC144D27C900241F3E339478DEC958F92FDDBC551F295C992038A3")
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
        HasExpectedHash(GetModelPath(modelId), Resolve(modelId).ModelSha256) &&
        HasExpectedHash(GetVocabPath(modelId), Resolve(modelId).TokenizerSha256);

    public async Task<string> EnsureInstalledAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = Resolve(modelId);
        var directory = GetModelDirectory(descriptor.Id);
        Directory.CreateDirectory(directory);

        await DownloadIfMissingAsync(
            descriptor.ModelUrl,
            descriptor.ModelSha256,
            GetModelPath(descriptor.Id),
            cancellationToken).ConfigureAwait(false);
        await DownloadIfMissingAsync(
            descriptor.TokenizerUrl,
            descriptor.TokenizerSha256,
            GetVocabPath(descriptor.Id),
            cancellationToken).ConfigureAwait(false);

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

    private async Task DownloadIfMissingAsync(
        string url,
        string expectedSha256,
        string destination,
        CancellationToken cancellationToken)
    {
        if (HasExpectedHash(destination, expectedSha256))
        {
            return;
        }

        File.Delete(destination);
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

        if (!HasExpectedHash(temporary, expectedSha256))
        {
            File.Delete(temporary);
            throw new InvalidDataException($"Downloaded file '{Path.GetFileName(destination)}' did not match its expected SHA-256 checksum.");
        }

        File.Move(temporary, destination, overwrite: true);
    }

    private static bool HasExpectedHash(string path, string expectedSha256)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        return string.Equals(
            Convert.ToHexString(SHA256.HashData(stream)),
            expectedSha256,
            StringComparison.OrdinalIgnoreCase);
    }
}
