using System.Text.Json;
using System.Text.Json.Serialization;

public class Configuration
{
    public uint listen_port { get; set; } = 9200;
    public string? encode_command { get; set; } = "-c:v libsvtav1 -preset 5 -crf 40 -g 240 -svtav1-params tune=0:fast-decode=1 -c:a libopus -ac 2 -b:a 128k";
    public string[] encode_types { get; set; } = [
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

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Configuration))]
public partial class JsonContext : JsonSerializerContext { }