using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AbyssGet.Column;
using AbyssGet.Crypto;
using AbyssGet.Util;
using Spectre.Console;
using JsonContext = AbyssGet.Util.JsonContext;

namespace AbyssGet;

public class Abyss
{
    private static readonly HttpClientHandler ClientHandler = new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        MaxConnectionsPerServer = 1024,
    };

    private static readonly HttpClient Client = new(ClientHandler)
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
    };

    private readonly Settings _settings;
    private readonly Logger _logger;
    
    public const int BlockSize = 16 * 4096;
    public const int StepSize = BlockSize * 32;

    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36 Edg/135.0.0.0";
    private const string Origin = "https://abysscdn.com";

    public Abyss(Settings settings)
    {
        _logger = new Logger(settings.LogLevel);

        _settings = settings;
        Client.Timeout = settings.RequestTimeout;
    }
    
    private static async Task<ConcurrentDictionary<string, TimeSpan>> GetUrls(Video video)
    {
        var queryParams = new Dictionary<string, string>
        {
            { "slug", video.Slug! },
            { "size", video.Size.ToString() },
            { "label", video.Label! },
            { "md5_id", video.Md5Id.ToString() }
        };

        var content = new FormUrlEncodedContent(queryParams);
        var queryString = await content.ReadAsStringAsync();
        
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{video.Domain}/tunnel/list?{queryString}");
        request.Headers.Add("user-agent", UserAgent);
        request.Headers.Add("origin", Origin);

        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        
        var responseBody = await response.Content.ReadAsStringAsync();
        var urls = Enumerable
            .Where<string>(JsonSerializer
                .Deserialize(responseBody, JsonContext.Default.ListString)!, url => url != "hello-world" && url.StartsWith("https://") && url.Contains("trycloudflare.com"))
            .Select(url => $"{url}/{video.Id}")
            .ToDictionary(url => url, _ => TimeSpan.Zero);

        return new ConcurrentDictionary<string, TimeSpan>(urls);
    }
    
    private static void DecryptResponse(Video video, byte[] data)
    {
        var videoCipher = AesCtr.FromMd5(video.Size);
        videoCipher.EncryptDecrypt(data, BlockSize);
    }
    
    private string GetBestUrl(ConcurrentDictionary<string, TimeSpan> urls)
    {
        if (_settings.FirstUrlOnly)
            return urls.First().Key;
        
        var unusedUrls = urls.Where(url => url.Value.Ticks == 0).ToArray();
        if (unusedUrls.Length != 0)
        {
            var chosen = unusedUrls.First().Key;
            urls[chosen] = TimeSpan.FromTicks(1);
            return chosen;
        }

        var poolUrls = urls
            .OrderBy(url => url.Value.Milliseconds)
            .Where(url => url.Value.Ticks != 1)
            .Take(_settings.BestUrlPoolSize)
            .ToList();

        var random = new Random();

        return poolUrls.Count == 0 
            ? urls.ToList()[random.Next(poolUrls.Count)].Key 
            : poolUrls[random.Next(poolUrls.Count)].Key;
    }

    private async Task RequestVideo(ProgressTask task, string url, ConcurrentDictionary<string, TimeSpan> urls,
        RequestPayload payload, Video video, CancellationToken cancellationToken, string downloadDir)
    {
        var triesRemaining = _settings.RequestRetries;

        while (true)
        {
            try
            {
                _logger.LogDebug($"Sending Request [{payload.Start}]: {url}", video);

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
                requestMessage.Headers.Add("user-agent", UserAgent);
                requestMessage.Headers.Add("origin", Origin);
                
                requestMessage.Content = new StringContent(payload.Body, Encoding.UTF8, "application/json");

                var requestStopwatch = Stopwatch.StartNew();
                var response = await Client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                requestStopwatch.Stop();
                
                urls[url] = requestStopwatch.Elapsed;

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Response status code {response.StatusCode}, retrying", video);
                    continue;
                }

                var fragmentNumber = payload.Start / StepSize;
                var filePath = Path.Combine(downloadDir, $"{fragmentNumber}.bin");
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                var buffer = new byte[BlockSize];
                var accumulated = 0;
                var firstBlock = true;

                while (accumulated < BlockSize)
                {
                    var blockStopwatch = Stopwatch.StartNew();
                    var bytesRead = await responseStream.ReadAsync(buffer, accumulated, BlockSize - accumulated, cancellationToken);
                    blockStopwatch.Start();
 
                    if (blockStopwatch.Elapsed > _settings.BlockTimeout)
                    {
                        _logger.LogWarn("Thread is falling behind", video);
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }
        
                    accumulated += bytesRead;
                    task.Increment(bytesRead);

                    if (accumulated != BlockSize) 
                        continue;

                    if (firstBlock)
                    {
                        DecryptResponse(video, buffer);
                        firstBlock = false;
                    }
            
                    await fileStream.WriteAsync(buffer, cancellationToken);
            
                    accumulated = 0;
                }

                if (accumulated > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, accumulated, cancellationToken);
                }
                break;
            }
            catch (Exception)
            {
                _logger.LogError($"Request failed, retrying ({triesRemaining}/{_settings.RequestRetries}) ...", video);
                url = GetBestUrl(urls);
                        
                await Task.Delay(1000, cancellationToken);

                if (triesRemaining == 0) return;

                triesRemaining--;
            }
        }
    }
    
    private async Task Process(ProgressTask task, Video video, ConcurrentDictionary<string,TimeSpan> urls, ConcurrentDictionary<string, int> threads, ConcurrentDictionary<string, (int, int)> fragments)
    {
        var tempDir = Guid.NewGuid().ToString();
        var downloadDir = Path.Combine(_settings.OutputDirectory, tempDir);
        
        if (Directory.Exists(downloadDir))
            Directory.Delete(downloadDir, recursive: true);
        
        Directory.CreateDirectory(downloadDir);
        
        _logger.LogInfo("Pre-generating payloads...", video);
        var payloads = Helpers.GeneratePayloads(video);
        fragments.TryAdd(task.Description, (0, payloads.Length));
        
        _logger.LogDebug("Payloads:");
        var padding = video.Size.ToString().Length;
        foreach (var payload in payloads)
        {
            _logger.LogDebug($"  {payload.Start.ToString().PadLeft(padding)} | {payload.Body}");
        }
        
        _logger.LogInfo($"Starting download of {payloads.Length} fragments...", video);
        var options = new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxThreads };

        task.StartTask();
        await Parallel.ForEachAsync(payloads, options, async (payload, cancellationToken) =>
        {
            threads.AddOrUpdate(task.Description, addValue: 1, updateValueFactory: (_, current) => current + 1);
            
            var url = GetBestUrl(urls);
            await RequestVideo(task, url, urls, payload, video, cancellationToken, downloadDir);

            threads.AddOrUpdate(task.Description, addValue: 0, updateValueFactory: (_, current) => current - 1);

            var fragmentCount = fragments[task.Description];
            fragmentCount.Item1 += 1;
            fragments[task.Description] = fragmentCount;
        });
        
        _logger.LogDebug("Finished downloading, server statistics", video);

        var orderedUrls = urls.OrderBy(e => e.Value.Milliseconds).ToList();
        var slowestTimeMs = orderedUrls.Last().Value.Milliseconds;
        
        foreach (var url in orderedUrls)
        {
            _logger.LogDebug($"  [{url.Value.Milliseconds.ToString().PadLeft(slowestTimeMs.ToString().Length)}ms]: {url.Key}", video);
        }
        
        _logger.LogInfo("Merging fragments", video);
        var fileName = $"{video.Slug!}_{video.Label}.mp4";
        Helpers.MergeFiles(_settings.OutputDirectory, tempDir, fileName);
        
        if (Directory.Exists(downloadDir))
            Directory.Delete(downloadDir, recursive: true);
        
        _logger.LogInfo($"Done, saved as {fileName}", video);
    }
    
    public async Task DownloadVideos(List<Video> videos)
    {
        var threads = new ConcurrentDictionary<string, int>();
        var fragments = new ConcurrentDictionary<string, (int, int)>();
        
        var progress = AnsiConsole
            .Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new RoundedPercentageColumn(2),
                new RemainingTimeColumn(),
                new TransferSpeedColumn { Base = FileSizeBase.Decimal },
                new ThreadUtilizationColumn(threads, _settings.MaxThreads),
                new FragmentColumn(fragments),
                new SpinnerColumn()
            )
            .AutoClear(true);

        await progress.StartAsync(async ctx =>
        {
            var tasks = new List<(ProgressTask, Video)>();
            
            foreach (var video in videos)
            {
                var task = ctx.AddTask($"{video.Slug!} | {video.Label, 5}", new ProgressTaskSettings { AutoStart = false });
                task.MaxValue = video.Size;
                
                tasks.Add((task, video));
            }

            if (_settings.DownloadInParallel)
            {
                await Parallel.ForEachAsync(tasks, async (task, _) =>
                {
                    var urls = await GetUrls(task.Item2);
                    await Process(task.Item1, task.Item2, urls, threads, fragments);
                });
            }
            else
            {
                foreach (var task in tasks)
                {
                    var urls = await GetUrls(task.Item2);
                    await Process(task.Item1, task.Item2, urls, threads, fragments);
                }
            }
        });
    }

    public async Task DownloadVideosWithPrompt(IEnumerable<string> videoIds)
    {
        var payloadList = new List<string>();

        foreach (var videoId in videoIds)
        {
            _logger.LogInfo($"Requesting payload for video {videoId}...");
            string payload;
            try
            {
                payload = await Helpers.RequestPayload(videoId);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unable to request payload: {ex.Message}");
                return;
            }

            if (payload == "NO_RETURN")
            {
                _logger.LogError("Unable to extract payload. Open an issue on GitHub.");
                return;
            }
            
            _logger.LogDebug($"{videoId} -> {payload}");
            payloadList.Add(payload);
        }

        var count = payloadList.Count;
        _logger.LogInfo($"Getting videos for {count} payload{(count == 1 ? "" : "s")}...");
        var videoList = Helpers.ExtractVideos(payloadList);
        
        var prompt = new MultiSelectionPrompt<Video>()
            .UseConverter(video =>
                (video.Label == "PARENT"
                    ? $"| {video.Slug!} | {video.Id} |"
                    : $"| {video.Label!, 5} | {video.Codec, 4} | {video.Type} | {(int)(video.Size / (1024.0 * 1024.0)), 4} MB")
                .EscapeMarkup())
            .Required()
            .PageSize(10);
        
        foreach (var videos in videoList)
        {
            var first = videos.First();
            prompt.AddChoiceGroup(new Video { Label = "PARENT", Slug = first.Slug, Id = first.Id }, videos);
        }
        
        var selectedVideos = AnsiConsole.Prompt(prompt);
        await DownloadVideos(selectedVideos);
    }
}