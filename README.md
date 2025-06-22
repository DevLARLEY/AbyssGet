# AbyssGet
C# abyss.to Parallel CLI downloader

# Usage
```
Description:
  Download one or more videos from abyss.to by their video json as base64

Usage:
  AbyssGet <videoJson>... [options]

Arguments:
  <videoJson>  Video JSON as Base64, e.g.: eyJ3aWR0aCI6IjEwMCUiLCJoZW...SI6Im1wNCJ9XX0=

Options:
  -f, --first-url-only                               Only use the first CDN url [default: False]
  -ps, --best-url-pool-size <best-url-pool-size>     Size of the URL pool containing the best performing URLs [default: 3]
  -t, --max-threads <max-threads>                    Maximum number of threads [default: 16]
  -l, --log-level <Debug|Error|Information|Warning>  Log level [default: Information]
  -rt, --request-timeout <request-timeout>           Request timeout in seconds [default: 120]
  -bt, --block-timeout <block-timeout>               Block timeout in seconds [default: 60]
  -rr, --request-retries <request-retries>           Number of request retries [default: 3]
  -p, --download-in-parallel                         Download videos in parallel [default: False]
  -o, --output-directory <output-directory>          Output directory [default: .]
  --version                                          Show version information
  -?, -h, --help                                     Show help and usage information
```