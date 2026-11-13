// ---------------------------------------------------------------------------
// The web front end: serves the guestbook page and passes its requests on to
// the api.
//
// "https+http://api" is resolved by service discovery: the AppHost's
// WithReference(api) gives this app the api's endpoints as configuration
// (services__api__https__0 and so on), whether that's a localhost port under
// `aspire run` or the api's App Service hostname once deployed.
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddHttpClient("api", client => client.BaseAddress = new Uri("https+http://api"));

var app = builder.Build();
app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Content(Page.Html, "text/html"));

app.MapGet("/api/entries", (IHttpClientFactory clients, CancellationToken cancellationToken) =>
    Forward(clients.CreateClient("api").GetAsync("/entries", cancellationToken), cancellationToken));

app.MapPost("/api/entries", async (HttpRequest request, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    var body = new StreamContent(request.Body);
    body.Headers.ContentType = new("application/json");
    return await Forward(clients.CreateClient("api").PostAsync("/entries", body, cancellationToken), cancellationToken);
});

app.Run();

static async Task<IResult> Forward(Task<HttpResponseMessage> call, CancellationToken cancellationToken)
{
    using var response = await call;
    var content = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Content(content, response.Content.Headers.ContentType?.ToString(), statusCode: (int)response.StatusCode);
}
