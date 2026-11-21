using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Playnite.SDK;

namespace MetadataImageOptimizer.Optimization
{
    /// <summary>
    /// Lossless PNG optimizer built on oxipng (https://github.com/oxipng/oxipng). It re-encodes the image with
    /// better filters and can reduce the colour type or palette without changing any pixel. Zopfli can be enabled
    /// for the smallest output, at a much higher cost in time.
    /// </summary>
    internal static class OxipngEncoder
    {
        private const string ExecutableName = "oxipng.exe";

        /// <summary>
        /// Zopfli iterations. 255 is past the point where more iterations only save a few dozen bytes.
        /// </summary>
        private const int ZopfliIterations = 255;

        private const double SecondsPerMegapixel = 30;
        private const double ZopfliSecondsPerMegapixel = 120;

        private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MinimumZopfliTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaximumZopfliTimeout = TimeSpan.FromHours(2);

        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly Lazy<string> ResolvedExecutablePath = new Lazy<string>(FindExecutablePath);

        public static bool IsAvailable => ResolvedExecutablePath.Value != null;

        /// <summary>
        /// Re-encodes a PNG losslessly and returns the smaller result. When oxipng cannot improve the file, the
        /// original bytes are returned unchanged.
        /// </summary>
        public static byte[] Optimize(byte[] pngBytes, long pixelCount, bool useZopfli)
        {
            var path = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Guid.NewGuid().ToString(), "png"));

            try
            {
                File.WriteAllBytes(path, pngBytes);

                var arguments = useZopfli
                    ? $"-o max -z --zi {ZopfliIterations} --quiet \"{path}\""
                    : $"-o max --quiet \"{path}\"";

                Run(arguments, GetTimeout(pixelCount, useZopfli));

                var optimized = File.ReadAllBytes(path);
                return optimized.LongLength < pngBytes.LongLength ? optimized : pngBytes;
            }
            finally
            {
                TryDelete(path);
            }
        }

        /// <summary>
        /// How long oxipng may take for an image of the given size. Zopfli is about ten times slower.
        /// </summary>
        public static TimeSpan GetTimeout(long pixelCount, bool useZopfli)
        {
            var secondsPerMegapixel = useZopfli ? ZopfliSecondsPerMegapixel : SecondsPerMegapixel;
            var seconds = Math.Max(0L, pixelCount) / 1_000_000.0 * secondsPerMegapixel;
            var timeout = TimeSpan.FromSeconds(seconds);

            if (useZopfli)
            {
                if (timeout < MinimumZopfliTimeout)
                {
                    return MinimumZopfliTimeout;
                }

                return timeout > MaximumZopfliTimeout ? MaximumZopfliTimeout : timeout;
            }

            if (timeout < MinimumTimeout)
            {
                return MinimumTimeout;
            }

            return timeout > MaximumTimeout ? MaximumTimeout : timeout;
        }

        private static void Run(string arguments, TimeSpan timeout)
        {
            var executablePath = ResolvedExecutablePath.Value;
            if (executablePath == null)
            {
                throw new FileNotFoundException($"Could not find '{ExecutableName}'. It should be located in the Tools folder of the extension.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath
                , Arguments = arguments
                , UseShellExecute = false
                , CreateNoWindow = true
                , RedirectStandardOutput = true
                , RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException($"Failed to start '{executablePath}'.");
                }

                // Read both streams asynchronously so a chatty optimizer can't fill the pipe buffer and deadlock
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // The process is being torn down either way
                    }

                    throw new TimeoutException($"'{ExecutableName}' did not finish within {timeout.TotalMinutes:0.#} minutes.");
                }

                if (process.ExitCode != 0)
                {
                    var message = string.Join(" ", new[] { standardError.Result, standardOutput.Result }
                        .Where(x => !string.IsNullOrWhiteSpace(x)))
                        .Trim();

                    throw new InvalidOperationException($"'{ExecutableName}' exited with code {process.ExitCode}: {message}");
                }
            }
        }

        private static string FindExecutablePath()
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(OxipngEncoder).Assembly.Location);
            if (string.IsNullOrEmpty(assemblyDirectory))
            {
                assemblyDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }

            var candidates = new[]
            {
                Path.Combine(assemblyDirectory, "Tools", ExecutableName)
                , Path.Combine(assemblyDirectory, ExecutableName)
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (path == null)
            {
                Logger.Warn($"Could not find '{ExecutableName}' in '{assemblyDirectory}'. PNG optimization is unavailable.");
            }

            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to delete temporary file '{path}': {ex.Message}");
            }
        }
    }
}
