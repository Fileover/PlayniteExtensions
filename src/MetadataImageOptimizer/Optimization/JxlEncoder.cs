using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Playnite.SDK;

namespace MetadataImageOptimizer.Optimization
{
    /// <summary>
    /// Wrapper around libjxl's cjxl.exe (https://github.com/libjxl/libjxl). ImageSharp has no JPEG XL encoder,
    /// so the only way to write that format is to shell out to the official encoder, which is shipped in the
    /// Tools folder of the extension.
    /// </summary>
    internal static class JxlEncoder
    {
        private const string ExecutableName = "cjxl.exe";

        /// <summary>
        /// libjxl's default Brotli effort. Only worth overriding when metadata is kept in the image.
        /// </summary>
        private const int DefaultBrotliEffort = 9;

        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(5);
        private static readonly Lazy<string> ResolvedExecutablePath = new Lazy<string>(FindExecutablePath);

        /// <summary>
        /// Encodes a PNG or JPEG XL file to JPEG XL.
        /// </summary>
        /// <param name="inputPath">The file to encode. cjxl reads PNG, APNG, GIF, JPEG and JPEG XL.</param>
        /// <param name="quality">Quality from 1 to 100, where 100 is mathematically lossless. Pass
        /// <c>null</c> to encode losslessly regardless of quality (<c>--distance=0</c>).</param>
        /// <param name="effort">Encoder effort from 1 to 11. 11 is an expert mode and needs
        /// <c>--allow_expert_options</c>; it is passed automatically.</param>
        /// <param name="brotliEffort">Brotli effort from 0 to 11, used for the metadata boxes only.</param>
        /// <returns>The encoded JPEG XL data.</returns>
        public static byte[] Encode(string inputPath, int? quality, int effort, int brotliEffort)
        {
            var outputPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Guid.NewGuid().ToString(), "jxl"));

            try
            {
                Run(BuildArguments(inputPath, outputPath, quality, effort, brotliEffort));
                return File.ReadAllBytes(outputPath);
            }
            finally
            {
                TryDelete(outputPath);
            }
        }

        private static string BuildArguments(string inputPath, string outputPath, int? quality, int effort, int brotliEffort)
        {
            var arguments = new StringBuilder();
            arguments.Append('"').Append(inputPath).Append("\" \"").Append(outputPath).Append("\" ");

            if (quality.HasValue)
            {
                arguments.Append("-q ").Append(quality.Value).Append(' ');
            }
            else
            {
                // Distance 0 is the mathematically lossless mode
                arguments.Append("-d 0 ");
            }

            arguments.Append("-e ").Append(effort).Append(' ');

            // Effort 11 only exists behind this flag; it produces slightly denser lossless files at a large cost
            if (effort > 10)
            {
                arguments.Append("--allow_expert_options ");
            }

            if (brotliEffort != DefaultBrotliEffort)
            {
                arguments.Append("--brotli_effort=").Append(brotliEffort).Append(' ');
            }

            arguments.Append("--quiet");
            return arguments.ToString();
        }

        private static void Run(string arguments)
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

                // Read both streams asynchronously so a chatty encoder can't fill the pipe buffer and deadlock
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)EncodeTimeout.TotalMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // The process is being torn down either way
                    }

                    throw new TimeoutException($"'{ExecutableName}' did not finish within {EncodeTimeout.TotalMinutes} minutes.");
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
            var assemblyDirectory = Path.GetDirectoryName(typeof(JxlEncoder).Assembly.Location);
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
                Logger.Warn($"Could not find '{ExecutableName}' in '{assemblyDirectory}'. JPEG XL output is unavailable.");
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
