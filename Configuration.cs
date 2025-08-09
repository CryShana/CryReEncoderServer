using System.Text.Json;
using System.Text.Json.Serialization;

public class Configuration
{
    public uint listen_port { get; set; } = 9200;
    public uint max_body_size_mb { get; set; } = 5000;
    /// <summary>
    /// <para>
    /// Directory that contains original files that were uploaded to this server
    /// Accepts change-able path parameters like $dd (day) $MM (month) or $YYYY (year)
    /// </para>
    /// <para>
    /// Example: "C:/Users/Username/Documents/ShareX/Screenshots/$YYYY-$MM"
    /// </para>
    /// </summary>
    public string? original_file_directory { get; set; }
    /// <summary>
    /// If true and original_file_directory set, will try to delete original file after successful upload
    /// </summary>
    public bool delete_original_after_upload { get; set; } = false;
    public EncodingProfile[] encoding_profiles { get; set; } = [
        new() {
            command = "-c:v libsvtav1 -preset 5 -crf 40 -g 240 -svtav1-params tune=0:fast-decode=1 -c:a libopus -ac 2 -b:a 128k",
            extension = "webm",
            content_type = "video/webm"
        }
    ];

    public static Configuration LoadOrCreateNew()
    {
        Configuration conf;
        if (!File.Exists("config.json"))
        {
            conf = new Configuration();
            File.WriteAllText("config.json", JsonSerializer.Serialize(conf, JsonContext.Default.Configuration));
            return conf;
        }

        var text = File.ReadAllText("config.json");
        conf = JsonSerializer.Deserialize(text, JsonContext.Default.Configuration) ?? new();
        return conf;
    }
}

public class EncodingProfile
{
    /// <summary>
    /// FFmpeg command, excludes the input and output
    /// </summary>
    public string? command { get; set; } = "-c:v libx264 -crf 30 -preset medium";
    /// <summary>
    /// Output extension
    /// </summary>
    public string? extension { get; set; } = "mp4";
    /// <summary>
    /// Output content type
    /// </summary>
    public string? content_type { get; set; } = "video/mp4";
    /// <summary>
    /// Content types that are targeted by this profile
    /// </summary>
    public string[] target_types { get; set; } = [
        "video/mp4",                // .mp4
        "video/mpeg",               // .mpeg, .mpg
        "video/webm",               // .webm
        "video/x-msvideo",          // .avi
        "video/quicktime",          // .mov
        "video/x-flv",              // .flv
        "video/ogg",                // .ogv
        "application/vnd.rn-realmedia", // .rm
        "video/x-ms-wmv",           // .wmv
        "video/3gpp",               // .3gp
        "video/3gpp2",              // .3g2
        "video/x-matroska",         // .mkv
        "video/h264",               // rarely used standalone
        "video/h265",               // rarely used standalone
        "video/x-f4v",              // .f4v
        "video/x-ms-asf"            // .asf
    ];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Configuration))]
public partial class JsonContext : JsonSerializerContext { }