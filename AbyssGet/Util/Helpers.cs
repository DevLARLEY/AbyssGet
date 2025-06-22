using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AbyssGet.Crypto;

namespace AbyssGet.Util;

public static class Helpers
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonContext Context = new(Options);

    public static string Decode(this byte[] data) => Encoding.UTF8.GetString(data);
    
    public static void MergeFiles(string baseDir, string tempDir, string fileName)
    {
        var downloadDir = Path.Combine(baseDir, tempDir);
        
        var files = Directory.GetFiles(downloadDir, "*.bin")
            .Select(f => new
            {
                Path = f,
                Number = int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : int.MaxValue
            })
            .Where(f => f.Number != int.MaxValue)
            .OrderBy(f => f.Number)
            .Select(f => f.Path)
            .ToList();

        var outFile = Path.Combine(baseDir, fileName);
        using var output = File.Create(outFile);
        
        foreach (var file in files)
        {
            using var input = File.OpenRead(file);
            input.CopyTo(output);
        }
    }

    public static List<Video> ExtractVideos(string payload)
    {
        var jsonPayload = JsonSerializer.Deserialize(payload, Context.MetadataPayload)!;
   
        return Enumerable.Select<MetadataSource, Video>(jsonPayload.Sources, source => new Video
            {
                Domain = jsonPayload.Domain,
                Id = jsonPayload.Id,
                Md5Id = jsonPayload.Md5Id,
                Slug = jsonPayload.Slug,

                Codec = source.Codec,
                Label = source.Label,
                ResId = source.ResId,
                Size = source.Size,
                Type = source.Type
            })
            .OrderByDescending(video => int.Parse(video.Label![..^1]))
            .ToList();
    }
    
    public static List<List<Video>> ExtractVideos(IEnumerable<string> payloads) => payloads.Select(ExtractVideos).ToList();

    public static long GetEnd(long start, long totalSize)
    {
        if (totalSize < Abyss.StepSize)
            return totalSize - 1;

        var end = Math.Min(start + Abyss.StepSize, totalSize);
        return end - 1;
    }

    private static string GetRequestPayload(Video video, VideoRange range)
    {
        var videoPayload = new VideoCipherPayload
        {
            Slug = video.Slug!,
            Md5Id = video.Md5Id,
            Label = video.Label!,
            Size = video.Size,
            Range = range,
        };
        
        var videoJsonPayload = JsonSerializer.Serialize(videoPayload, Context.VideoCipherPayload);

        var cipher = AesCtr.FromMd5(video.Slug!);
        var payload = Encoding.UTF8.GetBytes(videoJsonPayload);
        cipher.EncryptDecrypt(payload);
        
        return JsonSerializer.Serialize(new VideoPayload
        {
            Hash = Encoding.Latin1.GetString(payload)
        }, Context.VideoPayload);
    }

    public static RequestPayload[] GeneratePayloads(Video video)
    {
        var totalSize = video.Size;
        var payloads = new RequestPayload[totalSize / Abyss.StepSize + 1];
        
        long start = 0;
        var i = 0;
        
        while (start < totalSize - 1)
        {
            var end = GetEnd(start, totalSize);
            var range = new VideoRange { Start = start, End = end};
            
            var message = GetRequestPayload(video, range);
            payloads[i] = new RequestPayload(message, start);
 
            start = end + 1;
            i++;
        }

        return payloads;
    }
}