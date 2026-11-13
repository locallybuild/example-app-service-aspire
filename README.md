```
ooooo                                      oooo  oooo              
`888'                                      `888  `888              
 888          .ooooo.   .ooooo.   .oooo.    888   888  oooo    ooo 
 888         d88' `88b d88' `"Y8 `P  )88b   888   888   `88.  .8'
 888         888   888 888        .oP"888   888   888    `88..8'
 888       o 888   888 888   .o8 d8(  888   888   888     `888'
o888ooooood8 `Y8bod8P' `Y8bod8P' `Y888""8o o888o o888o     .8'
                                                       .o..P'
                                                       `Y8P'
```


# Example: Deploy a .NET Aspire app to App Service within Locally

This example shows how to deploy a [.NET Aspire](https://aspire.dev) app to App Service on [Locally Build](https://locally.build), using the Bicep that Aspire generates. The app is a guestbook. A web front end calls an API, which keeps entries in Blob Storage, queues each new one for a moderator, and caches the list in Azure Managed Redis. Both apps send their telemetry to Application Insights and to the Aspire dashboard.

No Terraform is involved. The AppHost in [`app/AppHost`](./app/AppHost) describes the whole system, and there are two ways to run it:

* **Deployed to App Service inside Locally**, with `locally run dotnet aspire deploy`. Aspire turns the AppHost into Bicep, deploys it, and builds and pushes the apps' images, the same way `aspire deploy` does for Azure. See [Deploying it to App Service in Locally](#deploying-it-to-app-service-in-locally).
* **On your machine, against Locally**, with `locally run dotnet aspire run`. Aspire runs the projects on your machine, as it usually does during development, and provisions the cache and Storage account they use in Locally. See [Running it on your machine](#running-it-on-your-machine-against-locally).

```
 browser ─▶ web (App Service) ─── service discovery ──▶ api (App Service)
                                                          │
                                                          ├─ entries ◀──▶ Blob Storage (one blob per entry)
                                                          ├─ new entry ─▶ Queue Storage ─▶ moderator ─▶ Approved / Rejected
                                                          └─ GET /entries ◀──▶ Azure Managed Redis (output cache)

 web, api ── OpenTelemetry ──▶ Application Insights
                          └──▶ Aspire dashboard (App Service)
```

Both apps are written in C# on ASP.NET Core (.NET 10). The source lives in [`app/`](./app), alongside the AppHost that describes how they run.

* **web** serves the guestbook page and passes its requests on to the api. It finds the api by service discovery (`https+http://api`), which the AppHost wires up both when you run the app locally and once it's deployed.
* **api** stores each entry as a blob and queues its ID. A background service takes each ID off the queue and approves the entry, or rejects it if the message mentions `spam`. The list of entries is cached in Redis for 30 seconds, or until an entry changes.

## The Aspire packages

| Package | Where | What it does here |
| --- | --- | --- |
| [`Aspire.Hosting.Azure.AppService`](https://www.nuget.org/packages/Aspire.Hosting.Azure.AppService) | AppHost | Publishes each project as an App Service web app, with an App Service plan, a Container Registry, managed identities, Application Insights and the Aspire dashboard. |
| [`Aspire.Hosting.Azure.Redis`](https://www.nuget.org/packages/Aspire.Hosting.Azure.Redis) | AppHost | Azure Managed Redis, with access keys turned off and Microsoft Entra sign-in turned on. |
| [`Aspire.Hosting.Azure.Storage`](https://www.nuget.org/packages/Aspire.Hosting.Azure.Storage) | AppHost | A Storage account with the `entries` blob container and the `submissions` queue. |
| [`Locally.Aspire.Hosting`](https://www.nuget.org/packages/Locally.Aspire.Hosting) | AppHost | `builder.AddLocally()`: under `locally run`, `aspire deploy` deploys the whole app into Locally, and `aspire run` provisions the cache and Storage account there. |
| [`Aspire.Azure.Storage.Blobs`](https://www.nuget.org/packages/Aspire.Azure.Storage.Blobs), [`Aspire.Azure.Storage.Queues`](https://www.nuget.org/packages/Aspire.Azure.Storage.Queues) | api | Build the blob container and queue clients from the connection strings the AppHost hands the app, with health checks and telemetry. |
| [`Aspire.StackExchange.Redis.OutputCaching`](https://www.nuget.org/packages/Aspire.StackExchange.Redis.OutputCaching) | api | Stores ASP.NET Core's output cache in Redis. |
| [`Locally.Aspire.Client`](https://www.nuget.org/packages/Locally.Aspire.Client) | api | `builder.UseLocallyCredential()`: signs the api's Azure clients, Redis included, in to Locally. See [Running on Locally](#running-on-locally). |
| [`Azure.Monitor.OpenTelemetry.AspNetCore`](https://www.nuget.org/packages/Azure.Monitor.OpenTelemetry.AspNetCore) | ServiceDefaults | Sends each app's traces, metrics and logs to Application Insights. |

[`app/ServiceDefaults`](./app/ServiceDefaults) is the project Aspire's templates call ServiceDefaults: OpenTelemetry, health checks, service discovery and resilient HttpClients, shared by both apps.

### How the apps authenticate

Nothing in the app or its settings holds a key, password or connection string with a secret in it. Aspire turns off shared-key access on the Storage account and access keys on the Redis cache, and gives the api a **user-assigned managed identity**. The Aspire client integrations sign in as that identity through `DefaultAzureCredential` (by way of `UseLocallyCredential()`), which App Service points at the managed identity endpoint (`AZURE_CLIENT_ID` and `AZURE_TOKEN_CREDENTIALS` are set by the published Bicep).

### Permissions

These are all generated by Aspire from the AppHost's `WithReference` calls:

| Identity | Permission | Scope | Why |
| --- | --- | --- | --- |
| api | Storage Blob Data Contributor | the Storage account | read and write the entries |
| api | Storage Queue Data Contributor | the Storage account | send and receive the queue's messages |
| api | Storage Table Data Contributor | the Storage account | Aspire grants it with the other two, though this app doesn't use tables |
| api | Redis `default` access policy | the cache's database | the output cache |
| registry pull identity | AcrPull | the Container Registry | App Service pulls both apps' images as this identity |
| dashboard identity | Reader, Website Contributor | the resource group, each web app | lets the Aspire dashboard list the apps |

## Requirements

* [Locally Build](https://locally.build).
* The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The Aspire CLI is pinned as a local tool in [`.config/dotnet-tools.json`](./.config/dotnet-tools.json), so `dotnet aspire` works without installing it globally.
* Either [Docker](https://www.docker.com) or [Podman](https://podman.io) (recommended).
* The [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), which Aspire uses to compile its Bicep, and which the commands below use to query Locally.
* The Locally Plugin for `Microsoft.Cache` installed (`locally plugin install --name Microsoft.Cache`).
* The Locally Plugin for `Microsoft.Storage` installed (`locally plugin install --name Microsoft.Storage`).

Running it on your machine needs only those. Deploying it to App Service also needs:

* The Locally Plugin for `Microsoft.Web` installed (`locally plugin install --name Microsoft.Web`).
* The Locally Plugin for `Microsoft.ContainerRegistry` installed (`locally plugin install --name Microsoft.ContainerRegistry`).
* The Locally Plugin for `Microsoft.Insights` installed (`locally plugin install --name Microsoft.Insights`).
* The Locally Plugin for `Microsoft.OperationalInsights` installed (`locally plugin install --name Microsoft.OperationalInsights`).

## Running the example

First, make sure your container runtime (Docker or Podman) is running, then start Locally:

```bash
locally build
```

With Locally running, open another terminal and restore the Aspire CLI:

```bash
dotnet tool restore
```

Then either [deploy it to App Service](#deploying-it-to-app-service-in-locally) or [run it on your machine](#running-it-on-your-machine-against-locally).

## Deploying it to App Service in Locally

```bash
locally run dotnet aspire deploy --apphost app/AppHost/AppHost.csproj
```

Run under `locally run`, `AddLocally()` points Aspire at Locally, and `aspire deploy` does there what it does for Azure:

1. It creates a resource group, `rg-aspire-apphost`, and deploys the Bicep it generates from the AppHost into it: the App Service plan, Container Registry, Redis, Storage, Application Insights, the Aspire dashboard, and the api's managed identity and role assignments.
2. It builds the api and web projects as container images with the .NET SDK, so there's no Dockerfile, and pushes them into the Container Registry. The images are built for your machine's architecture rather than `linux/amd64`, since Locally runs them on your machine.
3. It deploys each project as a web app, pointing it at its image and wiring it to the resources from step 1.

The summary Aspire prints at the end shows `*.azurewebsites.net` addresses and Azure Portal links for the apps, since Aspire builds those itself rather than reading them from the deployment. The dashboard address it prints is the real one. For the apps' addresses, ask Locally:

```bash
locally run az webapp list --resource-group rg-aspire-apphost --query '[].defaultHostName' --output tsv
```

```
api-bf8457123a94d.furnace.locally:5663
env-aspiredashboard-bf8457123a94d.furnace.locally:5663
web-bf8457123a94d.furnace.locally:5663
```

The names end in a hash of the resource group's ID, so yours will differ. Open the web app (`https://web-<hash>.furnace.locally:5663`) and sign the guestbook. Your entry shows as `Pending`, then a few seconds later as `Approved`. Mention `spam` in a message and it's `Rejected` instead. Below the list, the page says when the api built the list it's showing: until an entry changes, the same list is served from the Redis output cache for up to 30 seconds.

To change the app, edit it and run the same command again. It builds new images and rolls both apps onto them, and everything in Blob Storage and the queue is still there.

### Telemetry

Both apps send their traces, metrics and logs to two places.

**The Aspire dashboard.** Open the dashboard's address (`https://env-aspiredashboard-<hash>.furnace.locally:5663`) to see the structured logs, traces and metrics from `web` and `api`. Signing the guestbook produces one trace that runs from the web app's `POST /api/entries`, through the api's `POST /entries`, into its Blob upload and queue send. Listing the entries shows the api's Redis output cache in the same way. The dashboard keeps its telemetry in memory, so it starts empty again whenever its container restarts.

**Application Insights.** Query it with the Azure CLI:

```bash
APP_ID=$(locally run az monitor app-insights component show \
  --resource-group rg-aspire-apphost --query '[0].appId' --output tsv)
locally run az monitor app-insights query --app "$APP_ID" \
  --analytics-query 'requests | summarize count() by cloud_RoleName' \
  --query 'tables[0].rows' --output tsv
```

which lists the requests each app has served:

```
api-bf8457123a94d	8
web-bf8457123a94d	9
```

## Running it on your machine, against Locally

The same AppHost runs the projects on your machine, the way Aspire apps are usually developed. Run under `locally run`, Aspire provisions the cache and Storage account they use in Locally, as it would in an Azure subscription:

```bash
locally run dotnet aspire run --apphost app/AppHost/AppHost.csproj
```

On its first run Aspire creates a resource group of its own, `rg-aspire-apphost-<hash>`, deploys Azure Managed Redis and a Storage account into it, and grants your `locally run` identity access to them, which the projects sign in as. It records what it created in the AppHost's user secrets and reuses it on later runs. It prints a link to the Aspire dashboard, and the web app is at `https://localhost:7152`.

`locally run` provides what this needs: Locally's endpoints, a subscription, tenant and region, and an identity to sign in as. Aspire itself only knows how to provision into Azure, so the AppHost's first line points it at Locally:

```csharp
builder.AddLocally();
```

`AddLocally()` comes from [`Locally.Aspire.Hosting`](https://github.com/locallybuild/locally-dotnet/tree/main/src/Locally.Aspire.Hosting), whose README explains what it changes and why. Outside `locally run` its only effect is the host-name and port overrides described under [Running on Locally](#running-on-locally), which produce the values Aspire would have written for Azure.

Locally starts empty each time it's launched, so after a restart the resources Aspire recorded have gone. `AddLocally()` notices and Aspire provisions them again, which takes a minute or so; later runs reuse them, and start in seconds.

The AppHost doesn't call `RunAsContainer()` or `RunAsEmulator()`, since Locally provides the cache and Storage account. Without `locally run`, `dotnet aspire run` therefore provisions them in Azure, as it would for any Aspire app. Either way it needs a trusted ASP.NET Core development certificate (`dotnet dev-certs https --trust`).

## Running on Locally

Aspire's Bicep deploys to Locally without changes to its structure: the `SITECONTAINERS` web apps, the managed identities, role assignments and Redis access policies, and the Aspire dashboard all come straight from Aspire. A few things differ from Azure, and the app works around them in ways that are harmless on Azure.

**Host names and ports.** Aspire writes three values into its Bicep that assume Azure's fixed host names and ports, and on Locally they're different. `builder.AddLocally()` overrides each one to read the real value from the resource, so the AppHost needs no code of its own for them. On Azure, that produces exactly what Aspire would have written:

* Web apps on Azure are always `<name>.azurewebsites.net`, so Aspire builds the api's address for service discovery from that, and the dashboard's address too. Locally gives web apps names of their own (`<name>.furnace.locally:5663`), so the overrides use each site's `defaultHostName`.
* Azure Managed Redis always serves its database on port 10000, so Aspire writes that port into the connection string. Locally serves each database on a port of its own, so the override reads the database's `port` instead.

**Signing in.** The api's first line, `builder.UseLocallyCredential()` from [`Locally.Aspire.Client`](https://github.com/locallybuild/locally-dotnet/tree/main/src/Locally.Aspire.Client), takes care of two differences. On Azure it changes nothing.

* Aspire's `WithAzureAuthentication()` only turns on Microsoft Entra sign-in for a cache whose host name is one of Azure's (`*.redis.azure.net` and so on). For any other host, including Locally's `*.redis.locally`, it quietly connects with no credential, and the cache refuses the connection. `UseLocallyCredential()` signs in to `*.locally` caches with a token instead.
* Before the Azure SDK's sign-in library uses a Microsoft Entra endpoint it doesn't recognise, it asks `login.microsoftonline.com` about it: instance discovery. Run on your machine under `locally run`, the api signs in through Locally's own endpoint. `locally run` sets `AZURE_DISABLE_INSTANCE_DISCOVERY=true` to turn instance discovery off, but Azure.Identity for .NET doesn't read that variable, so `UseLocallyCredential()` applies it. On App Service the api signs in through the managed identity endpoint, which never does instance discovery.

## Notes

This example has no sign-in of its own: anyone who can reach the web app can sign the guestbook, and the api is public too, because App Service has no internal-only web apps. That keeps the example focused on Aspire. A real app would sit behind authentication.

The dashboard is published with Aspire's defaults for App Service, which leave its UI and its telemetry endpoint unsecured. Don't publish it like this to Azure for anything you care about.

## Tearing it down

```bash
locally run az group delete --name rg-aspire-apphost --yes
```
