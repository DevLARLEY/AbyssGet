using System.CommandLine;
using System.CommandLine.NamingConventionBinder;

namespace AbyssGet.Util;

public class CommandLine
{
    private static readonly Argument<IEnumerable<string>> VideoIdArguments = new("videoJson", "Video JSON as Base64, e.g.: eyJ3aWR0aCI6IjEwMCUiLCJoZW...SI6Im1wNCJ9XX0=")
    {
        Arity = ArgumentArity.OneOrMore
    };
    
    private static readonly Option FirstUrlOnlyOption = new Option<bool>(["--first-url-only", "-f"], () => false, "Only use the first CDN url");
    private static readonly Option BestUrlPoolSizeOption = new Option<int>(["--best-url-pool-size", "-ps"], () => 3, "Size of the URL pool containing the best performing URLs");
    private static readonly Option MaxThreadsOption = new Option<int>(["--max-threads", "-t"], () => 16, "Maximum number of threads");
    private static readonly Option LogLevelOption = new Option<LogLevel>(["--log-level", "-l"], () => LogLevel.Information, "Log level");
    private static readonly Option RequestTimeoutOption = new Option<int>(["--request-timeout", "-rt"], () => 120, "Request timeout in seconds");
    private static readonly Option BlockTimeoutOption = new Option<int>(["--block-timeout", "-bt"], () => 60, "Block timeout in seconds");
    private static readonly Option RequestRetriesOption = new Option<int>(["--request-retries", "-rr"], () => 3, "Number of request retries");
    private static readonly Option DownloadInParallelOption = new Option<bool>(["--download-in-parallel", "-p"], () => false, "Download videos in parallel");
    private static readonly Option OutputDirectoryOption = new Option<string>(["--output-directory", "-o"], () => ".", "Output directory");
    
    public static Command GetRootCommand()
    {
        var rootCommand = new RootCommand("Download one or more videos from abyss.to by their video json as base64")
        {
            Handler = CommandHandler.Create<IEnumerable<string>, bool, int, int, LogLevel, int, int, int, bool, string>(HandleCommandAsync)
        };

        rootCommand.AddArgument(VideoIdArguments);
        
        rootCommand.AddOption(FirstUrlOnlyOption);
        rootCommand.AddOption(BestUrlPoolSizeOption);
        rootCommand.AddOption(MaxThreadsOption);
        rootCommand.AddOption(LogLevelOption);
        rootCommand.AddOption(RequestTimeoutOption);
        rootCommand.AddOption(BlockTimeoutOption);
        rootCommand.AddOption(RequestRetriesOption);
        rootCommand.AddOption(DownloadInParallelOption);
        rootCommand.AddOption(OutputDirectoryOption);
        
        return rootCommand;
    }
    
    private static async Task HandleCommandAsync(IEnumerable<string> videoJson, bool firstUrlOnly, int bestUrlPoolSize, int maxThreads, LogLevel logLevel, int requestTimeout, int blockTimeout, int requestRetries, bool downloadInParallel, string outputDirectory) 
    {
        var settings = new Settings
        {
            FirstUrlOnly = firstUrlOnly,
            BestUrlPoolSize = bestUrlPoolSize,
            MaxThreads = maxThreads,
            LogLevel = logLevel,
            RequestTimeout = TimeSpan.FromSeconds(requestTimeout),
            BlockTimeout = TimeSpan.FromSeconds(blockTimeout),
            RequestRetries = requestRetries,
            DownloadInParallel = downloadInParallel,
            OutputDirectory = outputDirectory
        };

        var abyss = new Abyss(settings);
        await abyss.DownloadVideosWithPrompt(videoJson.ToList());
    }
}