// ---------------------------------------------------------------------------
// The app model: everything the guestbook is made of, and how it fits together.
//
// Under `locally run`, AddLocally() points Aspire at Locally instead of Azure:
// `aspire deploy` deploys the whole app to App Service there, and `aspire run`
// runs the projects on your machine, against a cache and Storage account
// provisioned in Locally. Either way, AddLocally() also has the Bicep read the
// real host names and ports from each resource, rather than assume Azure's
// fixed ones.
// ---------------------------------------------------------------------------
var builder = DistributedApplication.CreateBuilder(args);
builder.AddLocally();

// The App Service environment the projects are published to: an App Service
// plan, a Container Registry for their images, and a managed identity to pull
// them with. It also creates Application Insights and points every app's
// OpenTelemetry at it.
builder.AddAzureAppServiceEnvironment("env")
    .WithAzureApplicationInsights();

// Azure Managed Redis, for the api's output cache. The apps sign in with
// their managed identity, so the cache's access keys are switched off.
var cache = builder.AddAzureManagedRedis("cache");

// A Storage account holding the entries (a blob container) and the queue of
// entries waiting for the moderator. Also accessed with a managed identity.
var storage = builder.AddAzureStorage("storage");
var entries = storage.AddBlobContainer("entries");
var submissions = storage.AddQueue("submissions");

// App Service has no internal-only apps, so the api is published with a public
// endpoint too. The web app reaches it there, by service discovery.
var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(cache).WaitFor(cache)
    .WithReference(entries).WaitFor(entries)
    .WithReference(submissions).WaitFor(submissions);

builder.AddProject<Projects.Web>("web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(api).WaitFor(api);

builder.Build().Run();
