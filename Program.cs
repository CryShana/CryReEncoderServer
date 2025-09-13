using System.Collections.Concurrent;
using System.Net.Http.Headers;
using CryReEncoderServer;
using Microsoft.AspNetCore.Http.Features;

// clear temp directory from any last runs
const string TEMP_DIRECTORY = "temp";
if (Directory.Exists(TEMP_DIRECTORY))
    foreach (var f in Directory.GetFiles(TEMP_DIRECTORY))
        File.Delete(f);

// ---------------------------
// BUILDER CONFIGURATION
// ---------------------------
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

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolver = JsonContext.Default);

// no limit
long? limit_bytes = config.max_body_size_mb == 0 ? null : ((long)config.max_body_size_mb * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = limit_bytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = !limit_bytes.HasValue ? long.MaxValue : limit_bytes.Value);

// ---------------------------
// APP CONFIGURATION
// ---------------------------
var app = builder.Build();

var factory = app.Services.GetRequiredService<ILoggerFactory>();
var log = factory.CreateLogger("ReEncoder");

if (limit_bytes.HasValue)
    log.LogInformation("Max request body set to {0} bytes ({1} MB)", limit_bytes.Value, config.max_body_size_mb);

foreach (var p in config.encoding_profiles)
    log.LogInformation("Loaded profile '{0}'", p.command);

var active_tasks = new ConcurrentDictionary<string, EncodingProcess?>();
var csc = new CancellationTokenSource();
var http = new HttpClient();

Console.CancelKeyPress += (_, b) =>
{
    csc.Cancel();
    b.Cancel = true;
};

// semaphore for encders
if (config.max_concurrent_encoders <= 0)
    config.max_concurrent_encoders = 1;

var semaphore = new SemaphoreSlim(config.max_concurrent_encoders);

var check_transparency = config.encoding_profiles.Any(x => x.exclude_transparent_input);

// ---------------------------
// ENDPOINTS
// ---------------------------
app.MapGet("/", () => Results.Ok("CryReEncoder is running. Please use the POST endpoint to forward files"));
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

    log.Log(LogLevel.Information, "Started receiving file '{0}' ({1})", file.FileName, file.ContentType);

    // first copy it to local file
    Directory.CreateDirectory(TEMP_DIRECTORY);
    var out_path = Path.Combine(TEMP_DIRECTORY, Path.GetRandomFileName() + Path.GetExtension(file.FileName));

    if (!active_tasks.TryAdd(out_path, null))
        throw new Exception("Somehow a random path already existed, this should not occur: " + out_path);

    var final_path = out_path;
    var final_filename = file.FileName;
    var final_content_type = file.ContentType;
    var original_file_exists = false;
    try
    {
        // download it
        bool has_transparency = false;
        using (var file_stream = File.Create(out_path))
        {
            await file.OpenReadStream().CopyToAsync(file_stream);

            log.LogInformation("File '{0}' downloaded to '{1}' ({2})", file.FileName, out_path, GetHumanSize(file_stream.Length));

            if (config.fix_content_type)
            {
                var fixed_content_type = ContentTypeDetector.Fix(file_stream, final_content_type);
                if (fixed_content_type != final_content_type)
                    log.LogInformation("File '{0}' type fixed from '{1}' to '{2}'",
                        file.FileName, final_content_type, fixed_content_type);

                final_content_type = fixed_content_type;
            }
        }

        if (check_transparency)
        {
            if (final_content_type.Contains("image/") || final_content_type.Contains("video/"))
            {
                using var investigator = new InvestigationProcess(out_path);
                var t = await investigator.HasTransparency();
                if (t.HasValue)
                {
                    has_transparency = t.Value;
                    log.LogInformation("File '{0}' has transparency: {1}", file.FileName, has_transparency);
                }
                else
                {
                    log.LogInformation("File '{0}' has transparency: {1}", file.FileName, false);
                }
            }
        }

        // check if we can/should encode it
        EncodingProfile? profile = null;
        if (config.encoding_profiles != null)
            foreach (var p in config.encoding_profiles)
            {
                if (p.target_types != null &&
                    p.target_types.Contains(final_content_type, StringComparer.OrdinalIgnoreCase) == true &&
                    (!p.exclude_transparent_input || !has_transparency))
                {
                    profile = p;
                    break;
                }
            }

        if (profile != null)
        {
            if (semaphore.CurrentCount == 0)
                log.LogInformation("File '{0}' waiting to start encoding", file.FileName);

            await semaphore.WaitAsync();
            try
            {
                log.LogInformation("File '{0}' now encoding", file.FileName);

                using var encoder = new EncodingProcess(out_path, profile);
                if (!active_tasks.TryUpdate(out_path, encoder, null))
                    throw new Exception("Failed to register encoder for file: " + out_path);

                if (!await encoder.RunAsync(log))
                    throw new Exception("Failed to encode file: " + out_path + ", FFmpeg output:\n" + encoder.Log);

                final_path = encoder.OutputPath ?? throw new Exception("Missing encoded path");
                final_content_type = profile.content_type ?? final_content_type;
                log.LogInformation("File '{0}' encoded to '{1}' ({2})", file.FileName, encoder.OutputPath, GetHumanSize(new FileInfo(final_path).Length));

                var ext = profile.extension ?? Path.GetExtension(file.FileName);
                if (!ext.StartsWith('.')) ext = $".{ext}";

                final_filename = Path.GetFileNameWithoutExtension(file.FileName) + ext;
            }
            finally
            {
                semaphore.Release();
            }
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
        log.LogInformation("File '{0}' forwarding to '{1}'", final_filename, target_url);
        using var response = await http.SendAsync(request, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        log.LogInformation("File '{0}' forward response: {1}", final_filename, response.StatusCode);

        // Copy headers from response (we cannot copy everything)
        foreach (var h in response.Headers)
            if (h.Key.ToLower() is not "transfer-encoding" and not "connection" and not "alt-svc" and not "cache-control")
                context.Response.Headers[h.Key] = h.Value.ToArray();

        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();

        // Copy content
        await response.Content.CopyToAsync(context.Response.Body);

        // HANDLE POST effects
        var orig_path = GetOriginalDirectory(config);
        var orig_file_path = Path.Combine(orig_path ?? "", file.FileName);
        original_file_exists = File.Exists(orig_file_path);

        // DELETE ORIGINAL FILE (if enabled)
        if (response.IsSuccessStatusCode && config.delete_original_after_upload && orig_path != null)
        {
            _ = Task.Delay(1000).ContinueWith(t =>
                {
                    if (Directory.Exists(orig_path))
                    {
                        if (File.Exists(orig_file_path))
                        {
                            File.Delete(orig_file_path);
                            log.LogInformation("File '{0}' deleted from original directory", file.FileName);
                        }
                        else
                        {
                            //log.LogInformation("File '{0}' missing in original directory, deletion skipped", file.FileName);
                        }
                    }
                    else
                    {
                        log.LogWarning("Failed to delete original file, directory not found: {0}", orig_path);
                    }
                });
        }

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
        _ = Task.Delay(1500).ContinueWith(t =>
        {
            // FIRST TRY TO MOVE IT IF NEEDED (we move only if original file was also there, otherwise no point)
            var orig_path = GetOriginalDirectory(config);
            if (orig_path != null && config.move_forwarded_to_original_directory)
            {
                if (original_file_exists)
                {
                    var file_to_move = File.Exists(final_path) ? final_path : out_path;
                    if (File.Exists(file_to_move))
                    {
                        var file_destination = Path.Combine(orig_path, Path.GetFileName(final_filename));
                        try
                        {
                            if (File.Exists(file_destination)) throw new Exception("Destination file already exists");
                            File.Move(file_to_move, file_destination);
                            log.LogInformation("File '{0}' moved to original directory", final_filename);
                        }
                        catch (Exception ex)
                        {
                            log.LogError("Failed to move file '{0}' to '{1}', {2}", file_to_move, file_destination, ex.Message);
                        }
                    }
                    else
                    {
                        log.LogWarning("Failed to move file '{0}' to original folder, it doesn't exist anymore", file_to_move);
                    }
                }
                else
                {
                    log.LogInformation("File '{0}' skipped movement due to original file not existing", final_filename);
                }
            }

            // DELETE THE REST / CLEANUP
            if (File.Exists(out_path))
                File.Delete(out_path);

            if (final_path != out_path && File.Exists(final_path))
                File.Delete(final_path);
        });
    }
});

app.Urls.Add($"http://127.0.0.1:{config.listen_port}");
await app.RunAsync(csc.Token);


// ---------------------------
// FUNCTIONS
// ---------------------------
static string? GetOriginalDirectory(Configuration config)
{
    if (string.IsNullOrEmpty(config.original_file_directory))
        return null;

    // Replace following:
    // - $YYYY  = current year
    // - $MM    = zero-padded month
    // - $M     = month
    // - $dd    = zero-padded day
    // - $d     = day
    var now = DateTime.Now;
    var path = config.original_file_directory
        .Replace("$YYYY", now.Year.ToString())
        .Replace("$MM", now.Month.ToString("00"))
        .Replace("$M", now.Month.ToString())
        .Replace("$dd", now.Day.ToString("00"))
        .Replace("$d", now.Day.ToString());

    return path;
}

static string GetHumanSize(long size_bytes)
{
    return size_bytes switch
    {
        < 1000 => $"{size_bytes} bytes",
        < 1000_000 => $"{size_bytes / 1000.0:0.00} kB",
        < 1000_000_000 => $"{size_bytes / 1000_000.0:0.00} MB",
        < 1000_000_000_000 => $"{size_bytes / 1000_000_000.0:0.00} GB",
        _ => $"{size_bytes / 1000_000_000_000.0:0.00} TB",
    };
}