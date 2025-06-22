using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AbyssGet.Crypto;
using AbyssGet.Tls;
using Jint;
using PuppeteerSharp;

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

    private static string Atob(string input)
    {
        var bytes = Convert.FromBase64String(input);
        return Encoding.UTF8.GetString(bytes);
    }
    
    public static async Task<string> RequestPayload(string videoId, Logger logger)
    {
        var httpClient = new CustomHttpClient("abysscdn.com");
        var request = new HttpRequestMessage(HttpMethod.Get, videoId.StartsWith("http") ? videoId : $"https://abysscdn.com/?v={videoId}");
        
        request.Headers.ConnectionClose = true;
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
        var response = await httpClient.SendAsync(request, TimeSpan.FromSeconds(30));
        response.EnsureSuccessStatusCode();

        var htmlCode = await response.Content.ReadAsStringAsync();

        var scriptRegex = new Regex("<script>(.*?)</script>");
        var scriptMatches = scriptRegex.Matches(htmlCode);

        var jsCode = scriptMatches.Select(m => m.Groups[1].Value).OrderByDescending(t => t.Length).First();

        logger.LogInfo("Downloading Chromium...");
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GoToAsync("about:blank");
        
        const string preCode = @"
        var output = 'NO_RETURN';

        Object.defineProperty(navigator, 'webdriver', {
          get: () => undefined
        });

        var top = {location: '.'};
        var self = {};
        var isUseExtension = false;
        var getParameterByName = function() { return false; };

        const customAlphabet = ""RB0fpH8ZEyVLkv7c2i6MAJ5u3IKFDxlS1NTsnGaqmXYdUrtzjwObCgQP94hoeW+/="";
        const standardAlphabet = ""ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/="";

        function remapBase64(input) {
            return input.split("""").map(c => {
            const idx = customAlphabet.indexOf(c);
            return idx >= 0 ? standardAlphabet[idx] : c;
            }).join("""");
        }

        window.atob = new Proxy(window.atob, {
            apply(target, thisArg, args) {
              let str = args[0];
              if (str.endsWith(""_"")) {
                str = str.slice(0, -1);
                const remapped = remapBase64(str);
                return target.call(thisArg, remapped);
              } else {
                return target.apply(thisArg, args);
              }
            }
        });

        document.body.innerHTML = '<div id=""player""><div class=""loader""><span></span></div></div>';

        window.SoTrym = function(name) {
          return {
            setup: function(config) {
              output = JSON.stringify(config);
              return this;
            }
          };
        };";
        await page.EvaluateExpressionAsync(preCode);

        await page.EvaluateExpressionAsync(jsCode);
        await page.EvaluateExpressionAsync("window.dispatchEvent(new Event('load'));");

        var output = await page.EvaluateExpressionAsync<string>("output");
        await browser.CloseAsync();
        
        return output;
    }

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