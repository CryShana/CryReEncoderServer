namespace CryReEncoderServer;

public static class ContentTypeDetector
{
    public static string Fix(FileStream stream, string? content_type)
    {
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Length < 30) return content_type ?? "application/octet-stream";

        Span<byte> buffer = stackalloc byte[30];
        stream.ReadExactly(buffer);
        stream.Seek(0, SeekOrigin.Begin);

        // now determine common media types based on header

        // image
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (buffer is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..])
            return "image/png";

        // JPEG: FF D8 FF
        if (buffer is [0xFF, 0xD8, 0xFF, ..])
            return "image/jpeg";

        // GIF: 47 49 46 38
        if (buffer is [0x47, 0x49, 0x46, 0x38, ..])
            return "image/gif";

        // BMP: 42 4D
        if (buffer is [0x42, 0x4D, ..])
            return "image/bmp";

        // WEBP: RIFF....WEBP
        if (buffer is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..])
            return "image/webp";

        // TIFF: II*. (little endian) or MM.* (big endian)
        if (buffer is [0x49, 0x49, 0x2A, 0x00, ..] or
                      [0x4D, 0x4D, 0x00, 0x2A, ..])
            return "image/tiff";

        // ICO: 00 00 01 00
        if (buffer is [0x00, 0x00, 0x01, 0x00, ..])
            return "image/x-icon";

        // PDF: %PDF
        if (buffer is [0x25, 0x50, 0x44, 0x46, ..])
            return "application/pdf";

        // ZIP: PK
        if (buffer is [0x50, 0x4B, 0x03, 0x04, ..] or
                      [0x50, 0x4B, 0x05, 0x06, ..] or
                      [0x50, 0x4B, 0x07, 0x08, ..])
            return "application/zip";

        // MP3: ID3 or FF FB/FF F3/FF F2
        if (buffer is [0x49, 0x44, 0x33, ..] or
                      [0xFF, 0xFB, ..] or
                      [0xFF, 0xF3, ..] or
                      [0xFF, 0xF2, ..])
            return "audio/mpeg";

        // WAV: RIFF....WAVE
        if (buffer is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x41, 0x56, 0x45, ..])
            return "audio/wav";


        // OGG: OggS
        if (buffer is [0x4F, 0x67, 0x67, 0x53, ..])
            return "audio/ogg";

        // FLAC: fLaC
        if (buffer is [0x66, 0x4C, 0x61, 0x43, ..])
            return "audio/flac";

        // AVI: RIFF....AVI 
        if (buffer is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x41, 0x56, 0x49, 0x20, ..])
            return "video/avi";

        // WEBM: 1A 45 DF A3 (MKV has same header)
        if (buffer is [0x1A, 0x45, 0xDF, 0xA3, ..])
            return "video/webm";

        // FLV: FLV + version
        if (buffer is [0x46, 0x4C, 0x56, 0x01, ..])
            return "video/x-flv";

        // ASF/WMV: 30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C
        if (buffer is [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C, ..])
            return "video/x-ms-asf";

        // MPEG: 00 00 01 B*
        if (buffer is [0x00, 0x00, 0x01, var b, ..] && b >= 0xB0 && b <= 0xBF)
            return "video/mpeg";

        // Check ftyp-based formats (MP4, MOV, AVIF, HEIF, 3GP)
        if (buffer.Length >= 12 && buffer is [_, _, _, _, 0x66, 0x74, 0x79, 0x70, ..])
        {
            var brand = buffer.Slice(8, 4);
            if (brand is [0x61, 0x76, 0x69, 0x66]) return "image/avif";     // "avif"
            if (brand is [0x68, 0x65, 0x69, 0x63]) return "image/heic";     // "heic"
            if (brand is [0x6D, 0x69, 0x66, 0x31]) return "image/heif";     // "mif1"
            if (brand is [0x33, 0x67, 0x70, 0x34]) return "video/3gpp";     // "3gp4"
            if (brand is [0x33, 0x67, 0x70, 0x35]) return "video/3gpp";     // "3gp5"
            if (brand is [0x33, 0x67, 0x70, 0x36]) return "video/3gpp";     // "3gp6"
            if (brand is [0x33, 0x67, 0x70, 0x37]) return "video/3gpp";     // "3gp7"
            if (brand is [0x6D, 0x70, 0x34, 0x31]) return "video/mp4";      // "mp41"
            if (brand is [0x6D, 0x70, 0x34, 0x32]) return "video/mp4";      // "mp42"
            if (brand is [0x69, 0x73, 0x6F, 0x6D]) return "video/mp4";      // "isom"
            if (brand is [0x61, 0x76, 0x63, 0x31]) return "video/mp4";      // "avc1"
            if (brand is [0x71, 0x74, 0x20, 0x20]) return "video/mp4";      // "qt  "
            return "video/mp4"; // default for unknown ftyp
        }

        // Text-based formats
        if (buffer[0] == 0x3C) // starts with <
        {
            // "<!DOCTYPE" = [0x3C, 0x21, 0x44, 0x4F, 0x43, 0x54, 0x59, 0x50, 0x45]
            if (buffer is [0x3C, 0x21, 0x44, 0x4F, 0x43, 0x54, 0x59, 0x50, 0x45, ..])
                return "text/html";

            // "<html" = [0x3C, 0x68, 0x74, 0x6D, 0x6C]  
            if (buffer is [0x3C, 0x68, 0x74, 0x6D, 0x6C, ..])
                return "text/html";

            // "<?xml" = [0x3C, 0x3F, 0x78, 0x6D, 0x6C]
            if (buffer is [0x3C, 0x3F, 0x78, 0x6D, 0x6C, ..])
                return "application/xml";

            // "<svg" = [0x3C, 0x73, 0x76, 0x67]
            if (buffer is [0x3C, 0x73, 0x76, 0x67, ..])
                return "image/svg+xml";
        }

        // JSON: starts with { or [
        if (buffer is [0x7B, ..] or [0x5B, ..])
            return "application/json";

        return content_type ?? "application/octet-stream";
    }
}
