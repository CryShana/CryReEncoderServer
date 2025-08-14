using System.Text.Json;
using System.Text.Json.Serialization;

public class Configuration
{
    public uint listen_port { get; set; } = 9200;
    public uint max_body_size_mb { get; set; } = 5000;
    /// <summary>
    /// If true, will attempt to fix content type of sent file if mismatched
    /// </summary>
    public bool fix_content_type { get; set; } = false;
    /// <summary>
    /// Max amount of this many encoders will run at the same time (others will wait)
    /// </summary>
    public int max_concurrent_encoders { get; set; } = 2;
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
    /// <summary>
    /// If true and original_file_directory set, will try to move the forwarded file to original file directory after successful upload
    /// (replacing the file only if same name and content)
    /// </summary>
    public bool move_forwarded_to_original_directory { get; set; } = false;
    public EncodingProfile[] encoding_profiles { get; set; } = [
        new() {
            command = "-c:v libsvtav1 -preset 5 -crf 45 -g 240 -svtav1-params tune=0:fast-decode=1 -c:a libopus -ac 2 -b:a 128k",
            extension = "webm",
            content_type = "video/webm",
            target_types = [
                "video/mp4",                // .mp4
                "video/mpeg",               // .mpeg, .mpg
                "video/mts",                // .mts
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
            ]    
        },
        new() {
            command = "-c:v libaom-av1 -crf 18 -cpu-used 0 -row-mt 1 -tiles 4x4 -aq-mode 2",
            extension = "avif",
            content_type = "image/avif",
            target_types = [
                "image/jpeg",
                "image/png",
                "image/bmp"
            ]
        },
        new() {
            command = "-c:v libaom-av1 -crf 30 -cpu-used 6 -row-mt 1 -tiles 2x2 -aq-mode 2",
            extension = "avif",
            content_type = "image/avif",
            target_types = [
                "image/gif"
            ]
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
        "video/mp4",
        "video/mpeg",
        "video/webm",
        "video/x-msvideo"
    ];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Configuration))]
public partial class JsonContext : JsonSerializerContext { }