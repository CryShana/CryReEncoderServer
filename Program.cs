using System.Collections.Concurrent;
using System.Net.Http.Headers;

// clear temp directory from any last runs
const string TEMP_DIRECTORY = "temp";
if (Directory.Exists(TEMP_DIRECTORY))
    foreach (var f in Directory.GetFiles(TEMP_DIRECTORY))
        File.Delete(f);

// PREPARE SERVER
var config = Configuration.LoadOrCreateNew();

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);

var app = builder.Build();

var factory = app.Services.GetRequiredService<ILoggerFactory>();
var log = factory.CreateLogger("ReEncoder");

var active_tasks = new ConcurrentDictionary<string, EncodingProcess?>();
var csc = new CancellationTokenSource();
var http = new HttpClient();

Console.CancelKeyPress += (_, b) =>
{
    csc.Cancel();
    b.Cancel = true;
};

app.MapGet("/", () => "CryReEncoder is active and listening, please use POST method");
app.MapPost("/", async (HttpContext context) =>
{
    if (!context.Request.HasFormContentType)
        return Results.BadRequest("Only accepting form requests");

    if (context.Request.Form.Files.Count != 1)
        return Results.BadRequest("Only accepting form requests with a single file");

    if (!context.Request.Headers.TryGetValue("TargetUrl", out var target_url) || string.IsNullOrEmpty(target_url) || !target_url.ToString().StartsWith("http"))
        return Results.BadRequest("Missing 'TargetUrl' header for redirection");

    // other headers should be replicated
    var headers_to_forward = context.Request.Headers
        .Where(x => x.Key.ToLower() is not "host" and not "content-type" and not "content-length" and not "targeturl")
        .ToArray();

    var file = context.Request.Form.Files[0];

    log.Log(LogLevel.Information, "Forward request received for file '{0}' ({1})", file.FileName, file.ContentType);

    // first copy it to local file
    Directory.CreateDirectory(TEMP_DIRECTORY);
    var out_path = Path.Combine(TEMP_DIRECTORY, Path.GetRandomFileName() + Path.GetExtension(file.FileName));

    if (!active_tasks.TryAdd(out_path, null))
        throw new Exception("Somehow a random path already existed, this should not occur: " + out_path);

    var final_path = out_path;
    var final_filename = file.FileName;
    var final_content_type = file.ContentType;
    try
    {
        // download it
        using (var file_stream = File.Create(out_path))
            await file.OpenReadStream().CopyToAsync(file_stream);

        log.LogInformation("File '{0}' downloaded to '{1}'", file.FileName, out_path);

        // check if we can/should encode it
        EncodingProfile? profile = null;
        if (config.encoding_profiles != null)
            foreach (var p in config.encoding_profiles)
            {
                if (p.target_types != null && p.target_types.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase) == true)
                {
                    profile = p;
                    break;
                }
            }

        if (profile != null)
        {
            log.LogInformation("File '{0}' now encoding", out_path);

            using var encoder = new EncodingProcess(out_path, profile);
            if (!active_tasks.TryUpdate(out_path, encoder, null))
                throw new Exception("Failed to register encoder for file: " + out_path);

            if (!await encoder.RunAsync())
                throw new Exception("Failed to encode file: " + out_path);

            log.LogInformation("File '{0}' encoded to '{1}'", out_path, encoder.OutputPath);
            final_path = encoder.OutputPath ?? throw new Exception("Missing encoded path");
            final_content_type = profile.content_type ?? final_content_type;

            var ext = profile.extension ?? Path.GetExtension(file.FileName);
            if (!ext.StartsWith('.')) ext = $".{ext}";

            final_filename = Path.GetFileNameWithoutExtension(file.FileName) + ext;
        }

        // Prepare forward request
        using var request = new HttpRequestMessage(HttpMethod.Post, target_url);
        using var multipart = new MultipartFormDataContent();
        request.Content = multipart;

        await using var fs = File.OpenRead(final_path);

        var streamContent = new StreamContent(fs);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(final_content_type);
        multipart.Add(streamContent, file.Name, final_filename);

        // Forward headers
        foreach (var h in headers_to_forward)
            request.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());

        // Forward to target url
        log.LogInformation("File '{0}' forwarding to URL: '{1}'", final_path, target_url);
        using var response = await http.SendAsync(request, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        log.LogInformation("File '{0}' forward response: {1}", final_path, response.StatusCode);

        // Copy headers from response (we cannot copy everything)
        foreach (var h in response.Headers)
            if (h.Key.ToLower() is not "transfer-encoding" and not "connection" and not "alt-svc" and not "cache-control")
                context.Response.Headers[h.Key] = h.Value.ToArray();

        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();

        // Copy content
        await response.Content.CopyToAsync(context.Response.Body);

        return Results.Empty;

    }
    catch (Exception ex)
    {
        log.LogError("Failed to forward: {0}", ex);
        return Results.InternalServerError(ex.Message);
    }
    finally
    {
        if (active_tasks.TryRemove(out_path, out var encoding_process))
        {
            encoding_process?.Dispose();
        }

        // delete file later
        _ = Task.Delay(1000).ContinueWith(t =>
        {
            if (File.Exists(out_path))
                File.Delete(out_path);

            if (final_path != out_path && File.Exists(final_path))
                File.Delete(final_path);
        });
    }
});

app.Urls.Add($"http://127.0.0.1:{config.listen_port}");
await app.RunAsync(csc.Token);