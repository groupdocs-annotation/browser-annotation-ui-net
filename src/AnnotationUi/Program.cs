using AnnotationUi;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DocumentService>();

WebApplication app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// --------------------------------------------------------------------- documents

app.MapGet("/api/documents", (DocumentService documents) => Results.Ok(documents.List()));

app.MapGet("/api/documents/{name}/info", (string name, DocumentService documents) =>
{
    try
    {
        return Results.Ok(documents.Info(name));
    }
    catch (FileNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
});

// pageNumber is 1-based, like PreviewOptions.PageNumbers
app.MapGet("/api/documents/{name}/pages/{pageNumber:int}/image", async (string name, int pageNumber, DocumentService documents) =>
{
    try
    {
        byte[] png = await documents.PageImageAsync(name, pageNumber);
        return Results.File(png, "image/png");
    }
    catch (FileNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
});

// --------------------------------------------------------------------- annotations

app.MapGet("/api/documents/{name}/annotations", (string name, DocumentService documents) =>
{
    try
    {
        return Results.Ok(documents.Annotations(name));
    }
    catch (FileNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
});

app.MapPost("/api/documents/{name}/annotations", async (string name, CreateAnnotationRequest request, DocumentService documents) =>
{
    if (request.Width <= 0 || request.Height <= 0)
    {
        return Results.BadRequest(new { error = "The rectangle is empty." });
    }

    await documents.AddAsync(name, request);
    return Results.Ok(documents.Annotations(name));
});

app.MapPut("/api/documents/{name}/annotations/{id:int}", async (string name, int id, UpdateAnnotationRequest request, DocumentService documents) =>
{
    int changed = await documents.UpdateAsync(name, id, request);
    return changed == 0
        ? Results.NotFound(new { error = $"No annotation with id {id}." })
        : Results.Ok(documents.Annotations(name));
});

app.MapDelete("/api/documents/{name}/annotations/{id:int}", async (string name, int id, DocumentService documents) =>
{
    int changed = await documents.DeleteAsync(name, id);
    return changed == 0
        ? Results.NotFound(new { error = $"No annotation with id {id}." })
        : Results.Ok(documents.Annotations(name));
});

app.Run();
