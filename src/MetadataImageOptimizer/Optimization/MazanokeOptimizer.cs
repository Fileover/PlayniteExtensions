using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using MetadataImageOptimizer.Settings;
using Playnite.SDK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using Image = SixLabors.ImageSharp.Image;

namespace MetadataImageOptimizer.Optimization
{
    /// <summary>
    /// Image compression pipeline modelled after the behaviour of MAZANOKE (https://github.com/civilblur/mazanoke):
    /// an optional dimension limit, metadata stripping, format conversion and - most importantly - a
    /// "compress until the file fits within a size budget" mode instead of only honouring a fixed quality value.
    /// <para>
    /// This is an independent C# implementation built on ImageSharp. No MAZANOKE source code is copied: MAZANOKE
    /// is a browser application released under the GPL-3.0, while this extension is MIT licensed, so only the
    /// approach (not the implementation) is reused.
    /// </para>
    /// </summary>
    public static class MazanokeOptimizer
    {
        private const int MinimumQuality = 1;
        private const int MaximumQuality = 100;
        private const int MaximumSearchIterations = 8;

        /// <summary>
        /// Assumed pixel count for JPEG XL sources, whose real dimensions can't be read without a decoder.
        /// </summary>
        private const long AssumedJxlPixelCount = 1920L * 1080L;

        /// <summary>
        /// Palette sizes that are attempted, in order, when a PNG has to be shrunk below the configured budget.
        /// </summary>
        private static readonly int[] PaletteSizes = { 256, 128, 64, 32, 16 };

        private static readonly ILogger Logger = LogManager.GetLogger();

        /// <summary>
        /// Optimizes an image according to the supplied settings.
        /// </summary>
        /// <param name="imagePath">The full path of the image to optimize.</param>
        /// <param name="imageSettings">The per image type settings (format, dimensions, compression mode).</param>
        /// <param name="qualitySettings">The encoder quality settings and metadata/behaviour switches.</param>
        /// <returns>The result of the optimization. When <see cref="OptimizationResult.Converted"/> is false the
        /// original file was overwritten in place; otherwise <see cref="OptimizationResult.Path"/> points to a
        /// temporary file that the caller must move into place.</returns>
        public static OptimizationResult Optimize(string imagePath, ImageTypeSettings imageSettings, QualitySettings qualitySettings)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentNullException(nameof(imagePath));
            }

            imageSettings = imageSettings ?? new ImageTypeSettings();
            qualitySettings = qualitySettings ?? new QualitySettings();

            var originalBytes = new FileInfo(imagePath).Length;
            var sourceFormat = NormalizeFormat(Path.GetExtension(imagePath));
            var targetFormat = NormalizeFormat(imageSettings.Format);

            if (!IsSupportedFormat(targetFormat))
            {
                // Fall back to the source format, or to PNG for formats that cannot be written directly (icons)
                targetFormat = IsSupportedFormat(sourceFormat) ? sourceFormat : "png";
            }

            if (sourceFormat == "jxl")
            {
                return OptimizeJxlSource(imagePath, imageSettings, qualitySettings, targetFormat, originalBytes);
            }

            var loadPath = imagePath;
            string extractedIconPath = null;

            if (sourceFormat == "ico")
            {
                // Icons are extracted to a high resolution PNG before being processed like any other image.
                // The original format is kept so the result is written to a new file instead of over the icon.
                extractedIconPath = ExtractIconToPng(imagePath);
                loadPath = extractedIconPath;
            }

            try
            {
                using (var image = Image.Load(loadPath))
                {
                    // Apply the EXIF orientation while the information is still available
                    image.Mutate(x => x.AutoOrient());

                    var resized = ApplyDimensionLimit(image, imageSettings);

                    if (qualitySettings.StripMetadata)
                    {
                        StripMetadata(image);
                    }

                    var targetBytes = GetTargetBytes(imageSettings);
                    var attempt = Encode(image, targetFormat, qualitySettings, targetBytes);
                    var converted = !string.Equals(targetFormat, sourceFormat, StringComparison.OrdinalIgnoreCase);

                    if (!attempt.TargetReached)
                    {
                        Logger.Warn($"Could not reach the target size of {targetBytes} bytes for '{Path.GetFileName(imagePath)}' (smallest result: {attempt.Bytes.LongLength} bytes).");
                    }

                    if (!converted && !resized && qualitySettings.SkipIfLarger && attempt.Bytes.LongLength >= originalBytes)
                    {
                        Logger.Debug($"Skipping '{Path.GetFileName(imagePath)}': optimized size ({attempt.Bytes.LongLength} bytes) is not smaller than the original ({originalBytes} bytes).");
                        return OptimizationResult.Unchanged(imagePath, originalBytes, targetFormat);
                    }

                    if (!converted)
                    {
                        WriteAtomically(imagePath, attempt.Bytes);

                        var inPlaceResult = new OptimizationResult(
                            imagePath, false, false, originalBytes, attempt.Bytes.LongLength
                            , targetFormat, attempt.Quality, attempt.TargetReached);

                        Logger.Debug($"Optimized '{Path.GetFileName(imagePath)}' in place: {originalBytes} -> {attempt.Bytes.LongLength} bytes ({inPlaceResult.PercentSaved:0.0}% saved).");
                        return inPlaceResult;
                    }

                    var newPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Guid.NewGuid().ToString(), targetFormat));
                    File.WriteAllBytes(newPath, attempt.Bytes);

                    var convertedResult = new OptimizationResult(
                        newPath, true, false, originalBytes, attempt.Bytes.LongLength
                        , targetFormat, attempt.Quality, attempt.TargetReached);

                    Logger.Debug($"Converted '{Path.GetFileName(imagePath)}' to {targetFormat}: {originalBytes} -> {attempt.Bytes.LongLength} bytes ({convertedResult.PercentSaved:0.0}% saved).");
                    return convertedResult;
                }
            }
            finally
            {
                if (extractedIconPath != null)
                {
                    TryDelete(extractedIconPath);
                }
            }
        }

        /// <summary>
        /// Scales the image down so it fits inside the configured maximum dimensions. Images are never upscaled.
        /// </summary>
        /// <returns><c>true</c> when the image was resized.</returns>
        private static bool ApplyDimensionLimit(Image image, ImageTypeSettings settings)
        {
            var maxWidth = settings.MaxWidth;
            var maxHeight = settings.MaxHeight;

            if (maxWidth <= 0 && maxHeight <= 0)
            {
                return false;
            }

            // A missing dimension means "unlimited"
            if (maxWidth <= 0)
            {
                maxWidth = image.Width;
            }

            if (maxHeight <= 0)
            {
                maxHeight = image.Height;
            }

            if (image.Width <= maxWidth && image.Height <= maxHeight)
            {
                return false;
            }

            var scale = Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height);
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));

            image.Mutate(x => x.Resize(width, height));
            return true;
        }

        /// <summary>
        /// Removes personally identifying metadata. The colour profile is kept so colours don't shift.
        /// </summary>
        private static void StripMetadata(Image image)
        {
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;
        }

        private static EncodeAttempt Encode(Image image, string format, QualitySettings qualitySettings, long? targetBytes)
        {
            switch (format)
            {
                case "jpg":
                    return EncodeLossy(image, targetBytes, qualitySettings.JpgQuality, quality => new JpegEncoder { Quality = quality });

                case "webp":
                    return EncodeLossy(
                        image
                        , targetBytes
                        , qualitySettings.WebpQuality
                        , quality => new WebpEncoder { Quality = quality, Method = qualitySettings.WebpEncodingMethod });

                case "png":
                    return EncodePng(image, targetBytes, qualitySettings);

                case "jxl":
                    return EncodeJxl(image, targetBytes, qualitySettings);

                default:
                    throw new NotSupportedException($"Format '{format}' is not supported by the image optimizer.");
            }
        }

        /// <summary>
        /// JPEG XL files cannot be decoded by ImageSharp, so they are re-encoded by cjxl.exe itself. That means
        /// dimension limits and conversion to another format cannot be applied to them.
        /// </summary>
        private static OptimizationResult OptimizeJxlSource(
            string imagePath
            , ImageTypeSettings imageSettings
            , QualitySettings qualitySettings
            , string targetFormat
            , long originalBytes)
        {
            if (targetFormat != "jxl")
            {
                Logger.Warn($"Skipping '{Path.GetFileName(imagePath)}': JPEG XL images can only be re-encoded to JPEG XL, not converted to {targetFormat}.");
                return OptimizationResult.Unchanged(imagePath, originalBytes, "jxl");
            }

            if (imageSettings.MaxWidth > 0 || imageSettings.MaxHeight > 0)
            {
                Logger.Debug($"'{Path.GetFileName(imagePath)}': dimension limits are not applied to JPEG XL images.");
            }

            var targetBytes = GetTargetBytes(imageSettings);
            var attempt = EncodeJxlFile(imagePath, targetBytes, qualitySettings);

            if (!attempt.TargetReached)
            {
                Logger.Warn($"Could not reach the target size of {targetBytes} bytes for '{Path.GetFileName(imagePath)}' (smallest result: {attempt.Bytes.LongLength} bytes).");
            }

            if (qualitySettings.SkipIfLarger && attempt.Bytes.LongLength >= originalBytes)
            {
                Logger.Debug($"Skipping '{Path.GetFileName(imagePath)}': optimized size ({attempt.Bytes.LongLength} bytes) is not smaller than the original ({originalBytes} bytes).");
                return OptimizationResult.Unchanged(imagePath, originalBytes, "jxl");
            }

            WriteAtomically(imagePath, attempt.Bytes);

            var result = new OptimizationResult(
                imagePath, false, false, originalBytes, attempt.Bytes.LongLength
                , "jxl", attempt.Quality, attempt.TargetReached);

            Logger.Debug($"Optimized '{Path.GetFileName(imagePath)}' in place: {originalBytes} -> {attempt.Bytes.LongLength} bytes ({result.PercentSaved:0.0}% saved).");
            return result;
        }

        /// <summary>
        /// Encodes the image and, when a size budget was supplied, searches for the highest quality that still
        /// fits inside it.
        /// </summary>
        private static EncodeAttempt EncodeLossy(Image image, long? targetBytes, int requestedQuality, Func<int, IImageEncoder> encoderFactory)
        {
            return EncodeLossy(targetBytes, requestedQuality, quality => EncodeToArray(image, encoderFactory(quality)));
        }

        /// <summary>
        /// Encodes the image and, when a size budget was supplied, searches for the highest quality that still
        /// fits inside it.
        /// </summary>
        private static EncodeAttempt EncodeLossy(long? targetBytes, int requestedQuality, Func<int, byte[]> encode)
        {
            var initialQuality = Math.Min(MaximumQuality, Math.Max(MinimumQuality, requestedQuality));
            var bytes = encode(initialQuality);

            if (targetBytes == null)
            {
                return new EncodeAttempt(bytes, initialQuality, true);
            }

            if (bytes.LongLength <= targetBytes.Value)
            {
                return new EncodeAttempt(bytes, initialQuality, true);
            }

            // Binary search for the highest quality that still fits within the budget
            var low = MinimumQuality;
            var high = initialQuality - 1;
            byte[] best = null;
            var bestQuality = 0;

            for (var iteration = 0; low <= high && iteration < MaximumSearchIterations; iteration++)
            {
                var mid = low + ((high - low) / 2);
                var candidate = encode(mid);

                if (candidate.LongLength <= targetBytes.Value)
                {
                    best = candidate;
                    bestQuality = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (best != null)
            {
                return new EncodeAttempt(best, bestQuality, true);
            }

            // The budget can't be met at any quality: fall back to the smallest result we can produce
            var smallest = encode(MinimumQuality);
            return new EncodeAttempt(smallest, MinimumQuality, smallest.LongLength <= targetBytes.Value);
        }

        /// <summary>
        /// JPEG XL is encoded through cjxl.exe, which only reads files, so the processed image is written to a
        /// temporary lossless PNG first.
        /// </summary>
        private static EncodeAttempt EncodeJxl(Image image, long? targetBytes, QualitySettings qualitySettings)
        {
            var inputPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Guid.NewGuid().ToString(), "png"));

            try
            {
                // Level 1 keeps the intermediate quick to write while the image data itself stays lossless
                using (var stream = File.Create(inputPath))
                {
                    image.Save(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.Level1 });
                }

                return EncodeJxlFile(inputPath, targetBytes, qualitySettings, (long)image.Width * image.Height);
            }
            finally
            {
                TryDelete(inputPath);
            }
        }

        /// <summary>
        /// Encodes a file cjxl can read (the temporary PNG above, or an existing JPEG XL image) using the
        /// configured JPEG XL options.
        /// </summary>
        /// <param name="pixelCount">Used to scale the encoder timeout. JPEG XL sources can't be decoded, so their
        /// dimensions are unknown and a typical 1080p size is assumed.</param>
        private static EncodeAttempt EncodeJxlFile(
            string inputPath
            , long? targetBytes
            , QualitySettings qualitySettings
            , long? pixelCount = null)
        {
            var effort = qualitySettings.JxlEffort;
            var brotliEffort = qualitySettings.JxlBrotliEffort;
            var timeout = JxlEncoder.GetTimeout(pixelCount ?? AssumedJxlPixelCount, effort);

            if (qualitySettings.JxlLossless)
            {
                // Lossless output can't be shrunk by lowering a quality value, so the budget is only checked
                var bytes = JxlEncoder.Encode(inputPath, null, effort, brotliEffort, timeout);
                return new EncodeAttempt(bytes, null, targetBytes == null || bytes.LongLength <= targetBytes.Value);
            }

            return EncodeLossy(
                targetBytes
                , qualitySettings.JxlQuality
                , quality => JxlEncoder.Encode(inputPath, quality, effort, brotliEffort, timeout));
        }

        /// <summary>
        /// The size budget in bytes, or <c>null</c> when the image type uses a fixed quality instead.
        /// </summary>
        private static long? GetTargetBytes(ImageTypeSettings imageSettings)
        {
            return imageSettings.Compression == CompressionMode.TargetSize
                ? Math.Max(1L, imageSettings.TargetSizeKb) * 1024L
                : (long?)null;
        }

        /// <summary>
        /// PNG is lossless, so the encoder and compression level are the only lossless levers. oxipng improves
        /// the filters and colour type without changing pixels; when a size budget still isn't met, the image is
        /// quantized to a 256 colour palette as a last resort.
        /// </summary>
        private static EncodeAttempt EncodePng(Image image, long? targetBytes, QualitySettings qualitySettings)
        {
            var bytes = EncodeToArray(image, new PngEncoder { CompressionLevel = qualitySettings.PngCompressionLevel });
            bytes = OptimizePng(bytes, image, qualitySettings);

            if (targetBytes == null || bytes.LongLength <= targetBytes.Value)
            {
                return new EncodeAttempt(bytes, null, true);
            }

            if (qualitySettings.PngCompressionLevel != PngCompressionLevel.Level9)
            {
                var maximumCompression = EncodeToArray(image, new PngEncoder { CompressionLevel = PngCompressionLevel.Level9 });
                maximumCompression = OptimizePng(maximumCompression, image, qualitySettings);
                if (maximumCompression.LongLength < bytes.LongLength)
                {
                    bytes = maximumCompression;
                }
            }

            if (bytes.LongLength <= targetBytes.Value)
            {
                return new EncodeAttempt(bytes, null, true);
            }

            // Last resort: quantize to a progressively smaller palette. Dithering is disabled because it makes
            // the result much harder to compress, which defeats the purpose of this step.
            foreach (var colors in PaletteSizes)
            {
                try
                {
                    var paletteEncoder = new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.Level9
                        , ColorType = PngColorType.Palette
                        , BitDepth = PngBitDepth.Bit8
                        , Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = colors, Dither = null })
                    };

                    var quantized = EncodeToArray(image, paletteEncoder);
                    if (quantized.LongLength < bytes.LongLength)
                    {
                        bytes = quantized;
                    }

                    if (bytes.LongLength <= targetBytes.Value)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to quantize PNG image to {colors} colours: {ex.Message}");
                    break;
                }
            }

            bytes = OptimizePng(bytes, image, qualitySettings);
            return new EncodeAttempt(bytes, null, bytes.LongLength <= targetBytes.Value);
        }

        /// <summary>
        /// Re-encodes PNG output with oxipng. The optimization is lossless and only kept when it makes the file
        /// smaller. A failure here is not fatal: the ImageSharp output is used instead.
        /// </summary>
        private static byte[] OptimizePng(byte[] bytes, Image image, QualitySettings qualitySettings)
        {
            if (!qualitySettings.PngUseOxipng || !OxipngEncoder.IsAvailable)
            {
                return bytes;
            }

            try
            {
                return OxipngEncoder.Optimize(bytes, (long)image.Width * image.Height, qualitySettings.PngUseZopfli);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to optimize PNG with oxipng: {ex.Message}");
                return bytes;
            }
        }

        private static byte[] EncodeToArray(Image image, IImageEncoder encoder)
        {
            using (var stream = new MemoryStream())
            {
                image.Save(stream, encoder);
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Writes the bytes to the target path without leaving a half written image behind when something fails.
        /// </summary>
        private static void WriteAtomically(string path, byte[] bytes)
        {
            var temporaryPath = path + ".optimizing";
            File.WriteAllBytes(temporaryPath, bytes);

            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            catch (Exception)
            {
                File.Copy(temporaryPath, path, true);
                TryDelete(temporaryPath);
            }
        }

        /// <summary>
        /// Extracts the largest frame of an .ico file into a temporary PNG so it can be handled by ImageSharp.
        /// </summary>
        private static string ExtractIconToPng(string iconPath)
        {
            using (var fileStream = File.OpenRead(iconPath))
            {
                var decoder = new IconBitmapDecoder(fileStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);

                using (var pngStream = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(decoder.Frames.OrderByDescending(frame => frame.PixelWidth * frame.PixelHeight).First());
                    encoder.Save(pngStream);

                    var newPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Guid.NewGuid().ToString(), "png"));
                    File.WriteAllBytes(newPath, pngStream.ToArray());
                    return newPath;
                }
            }
        }

        private static string NormalizeFormat(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return string.Empty;
            }

            var normalized = format.Trim().TrimStart('.').ToLowerInvariant();
            return normalized == "jpeg" ? "jpg" : normalized;
        }

        private static bool IsSupportedFormat(string format)
        {
            return format == "jpg" || format == "png" || format == "webp" || format == "jxl";
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

        private sealed class EncodeAttempt
        {
            public EncodeAttempt(byte[] bytes, int? quality, bool targetReached)
            {
                Bytes = bytes;
                Quality = quality;
                TargetReached = targetReached;
            }

            public byte[] Bytes { get; }

            public int? Quality { get; }

            public bool TargetReached { get; }
        }
    }
}
