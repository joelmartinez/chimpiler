namespace Chimpiler.Kb.Chunking;

/// <summary>Content types understood by the built-in chunkers.</summary>
public static class ContentTypes
{
    public const string Markdown = "markdown";
    public const string Text = "text";
    public const string Code = "code";

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".js", ".ts", ".tsx", ".jsx", ".py", ".go", ".rs", ".java",
        ".kt", ".rb", ".php", ".c", ".h", ".cpp", ".hpp", ".sql", ".sh", ".ps1"
    };

    /// <summary>Infers the content type from a file path's extension.</summary>
    public static string FromPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (MarkdownExtensions.Contains(extension))
        {
            return Markdown;
        }

        return CodeExtensions.Contains(extension) ? Code : Text;
    }
}
