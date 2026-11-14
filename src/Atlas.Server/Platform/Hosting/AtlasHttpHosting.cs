using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using Atlas.Server.Features.Security.Networks;

namespace Atlas.Server.Platform.Hosting;

public static partial class AtlasHttpHosting
{
    public static IServiceCollection AddAtlasHttpHosting(this IServiceCollection services, IConfiguration configuration)
    {
        // Startup validation of the explicit list stays; the list itself only seeds the database once (SecurityConfigurationCache).
        var proxies = ParseTrustedProxies(configuration.GetSection("Http:TrustedProxies").Get<string[]>() ?? []);
        services.AddSingleton(new TrustedProxyEnvironment(proxies, configuration.GetValue("Http:RecoverTrustedProxies", false)));
        services.AddResponseCompression(options =>
        {
            // Applied to fingerprinted /assets without a build-time variant and to the allow-listed API reads
            // below; never to authentication, recovery, token or share-link responses.
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["text/css", "text/javascript", "application/javascript", "image/svg+xml", "application/json", "application/problem+json"];
        });
        // Fastest (Brotli quality 1, gzip 1) keeps the CPU per read low; JSON still shrinks several-fold. ASP.NET Core has no
        // minimum size, and JSON streams without a length, so a small read is compressed too: at this level it costs microseconds.
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return services;
    }

    /// <summary>
    /// Compresses content reads. BREACH needs a secret and attacker-chosen text in one compressed response;
    /// the listed reads carry page content, trees, search results, requirements, relations, contacts, notifications,
    /// boards, entities and the administrator's logs and jobs, which are not secret to their reader. POST batch reads stay uncompressed.
    /// Authentication, CSRF, recovery, API token and share-link responses stay uncompressed.
    /// </summary>
    public static void UseAtlasApiCompression(this WebApplication app)
    {
        app.UseWhen(context => IsCompressedApiRead(context.Request), branch => branch.UseResponseCompression());
    }

    public static bool IsCompressedApiRead(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && CompressedApiReads.Any(prefix => request.Path.StartsWithSegments(prefix))
        && !(request.Path.Value ?? "").Contains("/share-links", StringComparison.OrdinalIgnoreCase);

    private static readonly PathString[] CompressedApiReads =
        ["/api/pages", "/api/spaces", "/api/search", "/api/me", "/api/tags", "/api/shared", "/api/integrations/youtrack", "/api/projector", "/api/objects", "/api/contacts", "/api/ai", "/api/notifications", "/api/boards", "/api/entities", "/api/admin/logs", "/api/admin/jobs", "/api/v1/spaces", "/api/v1/pages", "/api/v1/search"];

    /// <summary>
    /// Forwarded headers from the trusted proxies of the database (WP4 V4), replacing the framework middleware: the raw evidence is
    /// kept as a request feature for security decisions, then the verified client address and scheme are applied. An empty list
    /// trusts no proxy, including framework loopback defaults; X-Forwarded-Host is never honoured.
    /// </summary>
    public static void UseAtlasForwardedHeaders(this WebApplication app)
    {
        var cache = app.Services.GetRequiredService<SecurityConfigurationCache>();
        app.Use(async (context, next) =>
        {
            var evidence = ClientAddressEvidence.Capture(context);
            TrustedProxyTransport.Apply(context, evidence, cache.Transport());
            await next();
        });
    }

    public static void UseAtlasPublicSharingHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/s") || context.Request.Path.StartsWithSegments("/api/shared"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            }
            await next();
        });
    }

    /// <summary>
    /// Adds the document policy to HTML responses: the SPA shell, which also serves the public share
    /// pages /s/*. JSON, assets and workers get none. /scalar/* is exempt because Scalar.AspNetCore
    /// bootstraps with an inline script; a response that already carries its own policy (the OIDC
    /// mapping check, attachments) keeps it. Enforced by default (SEC-I-2): it is the one defense against a script injected through a
    /// third-party renderer of author content, such as Scalar in its same-origin srcdoc frame, which inherits the policy.
    /// Http:ContentSecurityPolicy:Enforce=false sends it Report-Only, an explicit operator choice honoured only in Development, as the
    /// developer sign-in is: elsewhere the setting is ignored with a warning, because Scalar's frame (allow-scripts allow-same-origin) is safe only under it.
    /// </summary>
    public static void UseAtlasContentSecurityPolicy(this WebApplication app)
    {
        var enforce = app.Configuration.GetValue("Http:ContentSecurityPolicy:Enforce", true);
        if (!enforce && !app.Environment.IsDevelopment())
        {
            app.Logger.LogWarning("Http:ContentSecurityPolicy:Enforce=false is ignored outside Development: the content security policy stays enforced.");
            enforce = true;
        }
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var response = context.Response;
                if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
                    && !context.Request.Path.StartsWithSegments("/scalar")
                    && response.Headers.ContentSecurityPolicy.Count == 0)
                {
                    if (enforce)
                    {
                        response.Headers.ContentSecurityPolicy = DocumentPolicy;
                    }
                    else
                    {
                        response.Headers.ContentSecurityPolicyReportOnly = DocumentPolicy;
                    }
                }
                return Task.CompletedTask;
            });
            await next();
        });
    }

    // 'self' also matches same-host ws:/wss: (CSP Level 3), which covers SignalR's WebSocket transport.
    // font-src data: is Monaco's codicon font, inlined as a data: URL in its stylesheet.
    private const string DocumentPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; " +
        "font-src 'self' data:; connect-src 'self'; worker-src 'self'; frame-src 'self'; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static void UseAtlasStaticFiles(this IApplicationBuilder app, IFileProvider files)
    {
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        ServeStaticFiles(app, files);
    }

    /// <summary>
    /// Answers fingerprinted assets ahead of authentication. They are public, immutable and the same for every visitor, so
    /// none of the hundred or so files of a cold start reads the session cookie. A request this does not answer (an unknown
    /// file) continues down the pipeline unchanged; the shell, print.html and every other unversioned file are served only by
    /// <see cref="UseAtlasStaticFiles"/> at its end, after authentication, where the cookie handler skips its
    /// session-revocation lookup for them (<see cref="IsAnsweredByStaticFiles"/>).
    /// </summary>
    public static void UseAtlasFingerprintAssets(this IApplicationBuilder app, IFileProvider files)
    {
        app.UseWhen(context => IsFingerprintAssetRead(context.Request), branch => ServeStaticFiles(branch, files));
    }

    private static void ServeStaticFiles(IApplicationBuilder app, IFileProvider files)
    {
        app.Use(async (context, next) =>
        {
            if (!SelectPrecompressed(context, files))
            {
                return;
            }
            await next(context);
        });
        app.UseWhen(context => IsFingerprintAssetRead(context.Request), branch => branch.UseResponseCompression());
        app.UseStaticFiles(CreateStaticFileOptions(files));
    }

    private static bool IsFingerprintAssetRead(HttpRequest request) =>
        IsFingerprintAsset(request.Path.Value ?? "") && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));

    /// <summary>
    /// True for a GET or HEAD that is the SPA shell, or that no endpoint claims and the web root has a file for. The static file
    /// machinery answers these with a file, except for two public cases that also reach it with an existing file: a precompressed .br
    /// or .gz asset requested directly, and a file of unknown content type, both of which answer 404. No such answer depends on who
    /// asks, so the cookie handler skips its session-revocation lookup for them (AuthCookieModule). Every other request keeps the
    /// lookup: the whole API, and a file that does not exist. Authentication still runs, so the sliding renewal of the cookie and the
    /// headers it adds stay as they were. Routing has chosen the endpoint by the time authentication runs. A fingerprinted asset
    /// that exists never gets here: <see cref="UseAtlasFingerprintAssets"/> answers it before authentication, so the files this
    /// decides are the unversioned ones (index.html, print.html, the favicon).
    /// </summary>
    public static bool IsAnsweredByStaticFiles(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }
        if (context.GetEndpoint() is { } endpoint)
        {
            return endpoint.Metadata.GetMetadata<SpaShellMarker>() is not null;
        }
        return context.RequestServices.GetRequiredService<PhysicalFileProvider>().GetFileInfo(request.Path.Value ?? "").Exists;
    }

    /// <summary>
    /// The web build writes .br (quality 11) and .gz (level 9) next to each compressible asset
    /// (src/Atlas.Web/scripts/atlasBundle.mjs). Serve the best variant the client accepts,
    /// instead of compressing the same immutable file again on every first load. Returns false when the
    /// request was answered here.
    /// </summary>
    public static bool SelectPrecompressed(HttpContext context, IFileProvider files)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "";
        if (!path.StartsWith("/assets/", StringComparison.Ordinal) || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            return true;
        }
        if (path.EndsWith(".br", StringComparison.Ordinal) || path.EndsWith(".gz", StringComparison.Ordinal))
        {
            // The variants are an encoding of their asset, not resources of their own.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }
        if (!IsFingerprintAsset(path))
        {
            return true;
        }
        var accepted = request.GetTypedHeaders().AcceptEncoding;
        foreach (var (encoding, extension) in Precompressed)
        {
            if (accepted.Any(value => value.Value.Equals(encoding, StringComparison.OrdinalIgnoreCase) && value.Quality is not 0)
                && files.GetFileInfo(path + extension).Exists)
            {
                context.Items[OriginalAssetPath] = path;
                request.Path = path + extension;
                context.Response.Headers.ContentEncoding = encoding;
                context.Response.Headers.Vary = HeaderNames.AcceptEncoding;
                return true;
            }
        }
        return true;
    }

    public static StaticFileOptions CreateStaticFileOptions(IFileProvider files) => new()
    {
        FileProvider = files,
        ContentTypeProvider = new PrecompressedContentTypes(),
        OnPrepareResponse = context =>
        {
            var path = context.Context.Items[OriginalAssetPath] as string ?? context.Context.Request.Path.Value ?? "";
            var headers = context.Context.Response.Headers;
            // index.html and other unversioned files revalidate by ETag (304, back/forward cache) instead of
            // being fetched again. A public share keeps the no-store UseAtlasPublicSharingHeaders already set:
            // the SPA fallback has rewritten its path to index.html by now.
            headers.CacheControl =
                IsFingerprintAsset(path) && context.File.Name != "index.html" ? "public,max-age=31536000,immutable"
                : headers.CacheControl == "no-store" ? "no-store"
                : "no-cache";
        }
    };

    private const string OriginalAssetPath = "Atlas.OriginalAssetPath";

    private static readonly (string Encoding, string Extension)[] Precompressed = [("br", ".br"), ("gzip", ".gz")];

    /// <summary>A precompressed variant has the media type of the asset it encodes.</summary>
    private sealed class PrecompressedContentTypes : IContentTypeProvider
    {
        private readonly FileExtensionContentTypeProvider m_types = new();

        public bool TryGetContentType(string subpath, out string contentType) =>
            m_types.TryGetContentType(subpath.EndsWith(".br", StringComparison.Ordinal) || subpath.EndsWith(".gz", StringComparison.Ordinal) ? subpath[..^3] : subpath, out contentType!);
    }

    public static IReadOnlyList<IPAddress> ParseTrustedProxies(IEnumerable<string> values)
    {
        var result = new List<IPAddress>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value, out var address))
            {
                throw new ArgumentException("Http:TrustedProxies must contain explicit IPv4 or IPv6 addresses; wildcards, networks and hostnames are not supported.");
            }
            if (!result.Contains(address))
            {
                result.Add(address);
            }
        }
        return result;
    }

    public static bool IsFingerprintAsset(string path) => FingerprintAsset().IsMatch(path);

    [GeneratedRegex(@"\A/assets/[^/]+-[A-Za-z0-9_-]{8,}\.(?:js|css|woff2?|ttf|svg|png|jpe?g|webp|avif)\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex FingerprintAsset();

}
