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


app.MapGet("/", () => "CryReEncoder is active and listening, please use POST method");

var active_paths = new HashSet<string>();
var active_paths_lock = new Lock();
var http = new HttpClient();

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

    lock (active_paths_lock)
        if (!active_paths.Add(out_path))
            throw new Exception("Somehow a random path already existed, this should not occur: " + out_path);

    var final_path = out_path;
    try
    {
        // download it
        using (var file_stream = File.Create(out_path))
            await file.OpenReadStream().CopyToAsync(file_stream);

        log.LogInformation("File '{0}' downloaded to '{1}'", file.FileName, out_path);

        // do we need to encode it or just forward it?
        var encode = config.encode_types?.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase) == true;
        if (encode)
        {
            log.LogInformation("File '{0}' started encoding", out_path);

            // TODO: encode locally first and override final_path
            final_path = out_path; // encoded path
        }

        // Prepare forward request
        using var request = new HttpRequestMessage(HttpMethod.Post, target_url);
        using var multipart = new MultipartFormDataContent();
        request.Content = multipart;

        await using var fs = File.OpenRead(final_path);

        var streamContent = new StreamContent(fs);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType); // TODO: determine programmatically
        multipart.Add(streamContent, file.Name, file.FileName);

        // Forward headers
        foreach (var h in headers_to_forward)
            request.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());

        // Forward to target url
        log.LogInformation("File '{0}' ({1}) being forwarded to target URL: '{2}'", file.FileName, final_path, target_url);
        using var response = await http.SendAsync(request, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        log.LogInformation("Received response {0} from forwarded request for file '{1}'", response.StatusCode, file.FileName);

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
        lock (active_paths_lock)
            active_paths.Remove(out_path);

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

app.Run($"http://127.0.0.1:{config.listen_port}");