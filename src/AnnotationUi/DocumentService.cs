using System.Collections.Concurrent;
using GroupDocs.Annotation;
using GroupDocs.Annotation.Models;
using GroupDocs.Annotation.Models.AnnotationModels;
using GroupDocs.Annotation.Options;

namespace AnnotationUi;

/// <summary>
/// Everything the browser UI needs from GroupDocs.Annotation, in one place.
///
/// Three rules drive the whole design:
///
///  1. <c>Annotator</c> holds the source file open until it is disposed, and it cannot save over
///     the file it is reading. Every change is written to a temporary file and moved into place
///     after the annotator is gone.
///  2. Page geometry and annotation geometry do not share an origin. <c>PageInfo</c> and
///     <c>AreaAnnotation.Box</c> measure from the TOP-left; text markup points measure from the
///     BOTTOM-left. The service converts both ways so the front end only ever sees top-left boxes.
///  3. Annotation ids are assigned when the document is read, not stored in it. They are stable
///     for as long as nothing else changes the file, which is enough for a UI that reloads the
///     list after every write - but they are not database keys.
/// </summary>
public sealed class DocumentService
{
    private readonly string _storage;
    private readonly string _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<DocumentService> logger)
    {
        _logger = logger;
        _storage = Path.GetFullPath(configuration["Storage:Documents"] ?? Path.Combine(environment.ContentRootPath, "storage"));
        _cache = Path.GetFullPath(configuration["Storage:Cache"] ?? Path.Combine(environment.ContentRootPath, "storage", ".preview-cache"));

        Directory.CreateDirectory(_storage);
        Directory.CreateDirectory(_cache);
        SeedFromSamples(environment.ContentRootPath);
        ApplyLicense();
    }

    // ----------------------------------------------------------------- documents

    public IReadOnlyList<string> List()
    {
        return Directory
            .EnumerateFiles(_storage)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DocumentInfoDto Info(string name)
    {
        string path = Resolve(name);

        using Annotator annotator = new(path);
        IDocumentInfo info = annotator.Document.GetDocumentInfo();

        List<PageDto> pages = info.PagesInfo
            .Select(page => new PageDto(page.PageNumber, page.Width, page.Height))
            .ToList();

        return new DocumentInfoDto(name, info.FileType.ToString(), info.PageCount, pages);
    }

    /// <summary>
    /// Returns a PNG of one page, rendered without annotations: the canvas draws those itself,
    /// so a new comment does not cost a re-render of the page image.
    /// <paramref name="pageNumber"/> is 1-based, like <c>PreviewOptions.PageNumbers</c>.
    /// </summary>
    public async Task<byte[]> PageImageAsync(string name, int pageNumber)
    {
        string path = Resolve(name);
        string cached = Path.Combine(_cache, Slug(name), $"page_{pageNumber}.png");

        if (File.Exists(cached) && File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(path))
        {
            return await File.ReadAllBytesAsync(cached).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);

        SemaphoreSlim gate = Lock(name);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using Annotator annotator = new(path);

            PreviewOptions previewOptions = new(page => File.Create(Path.Combine(_cache, Slug(name), $"page_{page}.png")))
            {
                PreviewFormat = PreviewFormats.PNG,
                PageNumbers = new[] { pageNumber },
                RenderAnnotations = false,
                Resolution = 144
            };

            annotator.Document.GeneratePreview(previewOptions);
        }
        finally
        {
            gate.Release();
        }

        return await File.ReadAllBytesAsync(cached).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- annotations

    public IReadOnlyList<AnnotationDto> Annotations(string name)
    {
        string path = Resolve(name);

        using Annotator annotator = new(path);
        IDocumentInfo info = annotator.Document.GetDocumentInfo();

        return annotator
            .Get()
            .Select(annotation => ToDto(annotation, info))
            .ToList();
    }

    public async Task<int> AddAsync(string name, CreateAnnotationRequest request)
    {
        return await MutateAsync(name, (annotator, info) =>
        {
            PageInfo page = info.PagesInfo[Math.Clamp(request.Page - 1, 0, info.PageCount - 1)];

            // preview pixels -> page units. The preview is rendered at a fixed resolution, so
            // the browser sends the image size it drew on rather than assuming a scale.
            double scaleX = page.Width / Math.Max(request.ImageWidth, 1);
            double scaleY = page.Height / Math.Max(request.ImageHeight, 1);

            AreaAnnotation area = new()
            {
                PageNumber = page.PageNumber - 1,   // 1-based in, 0-based out
                Box = new Rectangle(
                    (float)(request.X * scaleX),
                    (float)(request.Y * scaleY),
                    (float)(request.Width * scaleX),
                    (float)(request.Height * scaleY)),
                BackgroundColor = 0xFFE699,
                PenColor = 0xE08C00,
                PenWidth = 1,
                Opacity = 0.35,
                CreatedOn = DateTime.Now,
                Message = request.Message ?? string.Empty,
                User = new User { Id = 1, Name = request.User ?? "Reviewer", Role = Role.Editor }
            };

            annotator.Add(area);
            return 1;
        }).ConfigureAwait(false);
    }

    public async Task<int> UpdateAsync(string name, int id, UpdateAnnotationRequest request)
    {
        return await MutateAsync(name, (annotator, _) =>
        {
            AnnotationBase? annotation = annotator.Get().FirstOrDefault(a => a.Id == id);
            if (annotation is null)
            {
                return 0;
            }

            if (request.Message is not null)
            {
                annotation.Message = request.Message;
            }

            if (!string.IsNullOrWhiteSpace(request.Reply))
            {
                List<Reply> replies = annotation.Replies?.ToList() ?? new List<Reply>();
                replies.Add(new Reply
                {
                    Comment = request.Reply,
                    RepliedOn = DateTime.Now,
                    User = new User { Id = 2, Name = request.User ?? "Reviewer", Role = Role.Editor }
                });
                annotation.Replies = replies;
            }

            annotator.Update(annotation);
            return 1;
        }).ConfigureAwait(false);
    }

    public async Task<int> DeleteAsync(string name, int id)
    {
        return await MutateAsync(name, (annotator, _) =>
        {
            AnnotationBase? annotation = annotator.Get().FirstOrDefault(a => a.Id == id);
            if (annotation is null)
            {
                return 0;
            }

            annotator.Remove(annotation);
            return 1;
        }).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- internals

    /// <summary>Opens the document, applies a change, writes it next to the original and swaps the files.</summary>
    private async Task<int> MutateAsync(string name, Func<Annotator, IDocumentInfo, int> change)
    {
        string path = Resolve(name);
        string temporary = path + ".tmp";

        SemaphoreSlim gate = Lock(name);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            int affected;
            using (Annotator annotator = new(path))
            {
                affected = change(annotator, annotator.Document.GetDocumentInfo());
                if (affected == 0)
                {
                    return 0;
                }

                annotator.Save(temporary);
            }

            File.Move(temporary, path, overwrite: true);
            InvalidateCache(name);
            return affected;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                TryDelete(temporary);
            }

            gate.Release();
        }
    }

    private static AnnotationDto ToDto(AnnotationBase annotation, IDocumentInfo info)
    {
        int pageIndex = Math.Clamp(annotation.PageNumber ?? 0, 0, Math.Max(info.PageCount - 1, 0));
        PageInfo page = info.PagesInfo[pageIndex];

        double x = 0;
        double y = 0;
        double width = 0;
        double height = 0;

        if (annotation is AreaAnnotation area)
        {
            // Box is already measured from the top-left corner.
            x = area.Box.X;
            y = area.Box.Y;
            width = area.Box.Width;
            height = area.Box.Height;
        }
        else
        {
            List<Point> points = PointsOf(annotation);
            if (points.Count > 0)
            {
                double minX = points.Min(p => (double)p.X);
                double maxX = points.Max(p => (double)p.X);
                double minY = points.Min(p => (double)p.Y);
                double maxY = points.Max(p => (double)p.Y);

                // Text markup is measured from the bottom-left corner: flip it.
                x = minX;
                y = page.Height - maxY;
                width = maxX - minX;
                height = maxY - minY;
            }
        }

        List<ReplyDto> replies = annotation.Replies?
            .Select(r => new ReplyDto(r.User?.Name, r.Comment, r.RepliedOn))
            .ToList() ?? new List<ReplyDto>();

        return new AnnotationDto(
            annotation.Id,
            annotation.Type.ToString(),
            pageIndex + 1,
            x,
            y,
            width,
            height,
            annotation.Message,
            annotation.User?.Name,
            replies);
    }

    private static List<Point> PointsOf(AnnotationBase annotation)
    {
        return annotation switch
        {
            HighlightAnnotation highlight => highlight.Points?.ToList() ?? new List<Point>(),
            UnderlineAnnotation underline => underline.Points?.ToList() ?? new List<Point>(),
            StrikeoutAnnotation strikeout => strikeout.Points?.ToList() ?? new List<Point>(),
            SquigglyAnnotation squiggly => squiggly.Points?.ToList() ?? new List<Point>(),
            _ => new List<Point>()
        };
    }

    private SemaphoreSlim Lock(string name) => _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));

    private string Resolve(string name)
    {
        // Never let a request escape the storage folder.
        string safe = Path.GetFileName(name);
        string path = Path.Combine(_storage, safe);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No such document: " + safe);
        }

        return path;
    }

    private void InvalidateCache(string name)
    {
        string directory = Path.Combine(_cache, Slug(name));
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            TryDelete(file);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Could not delete {Path}", path);
        }
    }

    private static string Slug(string name)
    {
        return string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    }

    private void SeedFromSamples(string contentRoot)
    {
        if (Directory.EnumerateFiles(_storage).Any())
        {
            return;
        }

        foreach (string candidate in new[]
                 {
                     Path.Combine(contentRoot, "samples"),
                     Path.Combine(contentRoot, "..", "..", "samples")
                 })
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(candidate))
            {
                File.Copy(file, Path.Combine(_storage, Path.GetFileName(file)), overwrite: false);
            }

            _logger.LogInformation("Seeded {Storage} from {Samples}", _storage, Path.GetFullPath(candidate));
            return;
        }
    }

    /// <summary>Optional: without a licence the page images carry evaluation marks.</summary>
    private void ApplyLicense()
    {
        string? path = Environment.GetEnvironmentVariable("GROUPDOCS_ANNOTATION_LICENSE")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (File.Exists(path))
        {
            new License().SetLicense(path);
            _logger.LogInformation("GroupDocs.Annotation licence applied");
        }
        else
        {
            _logger.LogWarning("GROUPDOCS_ANNOTATION_LICENSE points at a missing file: {Path}", path);
        }
    }
}
