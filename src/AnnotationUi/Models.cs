namespace AnnotationUi;

/// <summary>A page as the browser needs it: its size in page units (points).</summary>
public record PageDto(int Number, double Width, double Height);

public record DocumentInfoDto(string Name, string FileType, int PageCount, IReadOnlyList<PageDto> Pages);

public record ReplyDto(string? User, string? Comment, DateTime? On);

/// <summary>
/// An annotation as the browser needs it: one rectangle in page units, measured from the
/// TOP-left corner of the page, plus a 1-based page number. The server does every conversion
/// so the front end never has to know which annotation types use which origin.
/// </summary>
public record AnnotationDto(
    int Id,
    string Type,
    int Page,
    double X,
    double Y,
    double Width,
    double Height,
    string? Message,
    string? User,
    IReadOnlyList<ReplyDto> Replies);

/// <summary>
/// What the canvas sends back. The rectangle is in preview-image pixels, together with the
/// size of the image it was drawn on, so the server can scale it to page units.
/// </summary>
public record CreateAnnotationRequest(
    int Page,
    double X,
    double Y,
    double Width,
    double Height,
    double ImageWidth,
    double ImageHeight,
    string? Message,
    string? User);

public record UpdateAnnotationRequest(string? Message, string? Reply, string? User);
