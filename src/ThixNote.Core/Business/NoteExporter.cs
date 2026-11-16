using System.Text;
using Markdig;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

public static class NoteExporter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string FileExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => ".md",
        ExportFormat.Html => ".html",
        _ => ".txt",
    };

    /// <summary>Exports the content of a note. The source path is checked so locked notes cannot be exported.</summary>
    public static void ExportToFile(string sourcePath, string title, string content, ExportFormat format, string targetPath)
    {
        AccessGuard.EnsureCanExport(sourcePath);
        File.WriteAllText(targetPath, Render(title, content, format), new UTF8Encoding(false));
    }

    public static string Render(string title, string content, ExportFormat format) => format switch
    {
        ExportFormat.Markdown => content,
        ExportFormat.Html => BuildHtml(title, content),
        _ => Markdown.ToPlainText(content, Pipeline),
    };

    private static string BuildHtml(string title, string markdown)
    {
        var body = Markdown.ToHtml(markdown, Pipeline);
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        const string style =
            "body{font-family:Georgia,serif;max-width:46rem;margin:3rem auto;padding:0 1rem;line-height:1.65;color:#2b2623}" +
            "blockquote{border-left:3px solid #db4c3f;margin-left:0;padding-left:1rem;font-style:italic}" +
            "code{background:#f5eeea;padding:.1em .3em;border-radius:4px}";
        return $"<!DOCTYPE html>\n<html lang=\"de\"><head><meta charset=\"utf-8\"><title>{safeTitle}</title>\n<style>{style}</style>\n</head><body>\n{body}\n</body></html>\n";
    }
}
