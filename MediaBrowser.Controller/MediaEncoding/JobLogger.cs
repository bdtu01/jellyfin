using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Extensions;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.MediaEncoding
{
    public class JobLogger
    {
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        private readonly ILogger _logger;

        public JobLogger(ILogger logger)
        {
            _logger = logger;
        }

        public async void StartStreamingLog(EncodingJobInfo state, Stream source, Stream target)
        {
            try
            {
                using (var reader = new StreamReader(source))
                {
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);

                        ParseLogLine(line, state);

                        var bytes = Encoding.UTF8.GetBytes(Environment.NewLine + line);

                        // If ffmpeg process is closed, the state is disposed, so don't write to target in that case
                        if (!target.CanWrite)
                        {
                            break;
                        }

                        await target.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        await target.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading ffmpeg log");
            }
        }

        private void ParseLogLine(string line, EncodingJobInfo state)
        {
            float? framerate = null;
            double? percent = null;
            TimeSpan? transcodingPosition = null;
            long? bytesTranscoded = null;
            int? bitRate = null;

            var parts = line.Split(' ');

            var totalMs = state.RunTimeTicks.HasValue
                ? TimeSpan.FromTicks(state.RunTimeTicks.Value).TotalMilliseconds
                : 0;

            var startMs = state.BaseRequest.StartTimeTicks.HasValue
                ? TimeSpan.FromTicks(state.BaseRequest.StartTimeTicks.Value).TotalMilliseconds
                : 0;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

                if (string.Equals(part, "fps=", StringComparison.OrdinalIgnoreCase) &&
                    (i + 1 < parts.Length))
                {
                    var rate = parts[i + 1];

                    if (float.TryParse(rate, NumberStyles.Any, _usCulture, out var val))
                    {
                        framerate = val;
                    }
                }
                else if (state.RunTimeTicks.HasValue &&
                    part.StartsWith("time=", StringComparison.OrdinalIgnoreCase))
                {
                    var time = part.Split(new[] { '=' }, 2).Last();

                    if (TimeSpan.TryParse(time, _usCulture, out var val))
                    {
                        var currentMs = startMs + val.TotalMilliseconds;

                        var percentVal = currentMs / totalMs;
                        percent = 100 * percentVal;

                        transcodingPosition = val;
                    }
                }
                else if (part.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
                {
                    var size = part.Split(new[] { '=' }, 2).Last();

                    int? scale = null;
                    if (size.IndexOf("kb", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        scale = 1024;
                        size = size.Replace("kb", string.Empty, StringComparison.OrdinalIgnoreCase);
                    }

                    if (scale.HasValue)
                    {
                        if (long.TryParse(size, NumberStyles.Any, _usCulture, out var val))
                        {
                            bytesTranscoded = val * scale.Value;
                        }
                    }
                }
                else if (part.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase))
                {
                    var rate = part.Split(new[] { '=' }, 2).Last();

                    int? scale = null;
                    if (rate.IndexOf("kbits/s", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        scale = 1024;
                        rate = rate.Replace("kbits/s", string.Empty, StringComparison.OrdinalIgnoreCase);
                    }

                    if (scale.HasValue)
                    {
                        if (float.TryParse(rate, NumberStyles.Any, _usCulture, out var val))
                        {
                            bitRate = (int)Math.Ceiling(val * scale.Value);
                        }
                    }
                }
            }

            if (framerate.HasValue || percent.HasValue)
            {
                state.ReportTranscodingProgress(transcodingPosition, 0, percent, 0, bitRate);
            }
        }
    }
}
