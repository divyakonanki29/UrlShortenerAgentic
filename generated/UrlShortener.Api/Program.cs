using UrlShortener.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IUrlStore, InMemoryUrlStore>();

var app = builder.Build();

app.MapPost("/shorten", (ShortenRequest req, IUrlStore store, HttpRequest http) =>
{
    try
    {
        var entry = store.Create(req.Url);
        var shortUrl = $"{http.Scheme}://{http.Host}/{entry.Code}";
        return Results.Ok(new ShortenResponse(entry.Code, shortUrl));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/{code}", (string code, IUrlStore store) =>
{
    var entry = store.Get(code);
    if (entry is null) return Results.NotFound();
    store.RecordClick(code);
    return Results.Redirect(entry.OriginalUrl, permanent: false);
});

app.MapGet("/analytics/{code}", (string code, IUrlStore store) =>
{
    var entry = store.Get(code);
    if (entry is null) return Results.NotFound();
    return Results.Ok(new AnalyticsResponse(entry.ClickCount, entry.CreatedAt, entry.LastAccessedAt));
});

app.Run();

public partial class Program { } // exposed for WebApplicationFactory in tests