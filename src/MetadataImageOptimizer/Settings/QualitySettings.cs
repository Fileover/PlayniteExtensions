using System;
using System.Collections.Generic;
using Playnite.SDK.Data;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace MetadataImageOptimizer.Settings
{
    public class QualitySettings : ObservableObject
    {
        private int jpgQuality;
        private int jxlQuality;
        private bool jxlLossless;
        private int jxlEffort = 7;
        private int jxlBrotliEffort = 9;
        private PngCompressionLevel pngCompressionLevel;
        private bool pngUseOxipng = true;
        private bool pngUseZopfli;
        private WebpEncodingMethod webpEncodingMethod;
        private int webpQuality;
        private bool stripMetadata = true;
        private bool skipIfLarger = true;

        [DontSerialize]
        public Dictionary<PngCompressionLevel, string> AvailablePngCompressionLevels { get; }
            = new Dictionary<PngCompressionLevel, string>
            {
                { PngCompressionLevel.Level0, "0 (No compression)" }
                , { PngCompressionLevel.Level1, "1" }
                , { PngCompressionLevel.Level2, "2" }
                , { PngCompressionLevel.Level3, "3" }
                , { PngCompressionLevel.Level4, "4" }
                , { PngCompressionLevel.Level5, "5" }
                , { PngCompressionLevel.Level6, "6 (Default)" }
                , { PngCompressionLevel.Level7, "7" }
                , { PngCompressionLevel.Level8, "8" }
                , { PngCompressionLevel.Level9, "9 (Highest compression)" }
            };

        [DontSerialize]
        public Dictionary<WebpEncodingMethod, string> AvailableWebpEncodingMethods { get; }
            = new Dictionary<WebpEncodingMethod, string>
            {
                { WebpEncodingMethod.Level0, "0 (Fastest)" }
                , { WebpEncodingMethod.Level1, "1" }
                , { WebpEncodingMethod.Level2, "2" }
                , { WebpEncodingMethod.Level3, "3" }
                , { WebpEncodingMethod.Level4, "4 (Default)" }
                , { WebpEncodingMethod.Level5, "5" }
                , { WebpEncodingMethod.Level6, "6 (Slowest)" }
            };

        public int JpgQuality
        {
            get => jpgQuality;
            set
            {
                value = Math.Min(100, value);
                value = Math.Max(1, value);

                SetValue(ref jpgQuality, value);
            }
        }

        public int JxlQuality
        {
            get => jxlQuality;
            set
            {
                value = Math.Min(100, value);
                value = Math.Max(1, value);

                SetValue(ref jxlQuality, value);
            }
        }

        /// <summary>
        /// When enabled, JPEG XL images are encoded mathematically losslessly (cjxl --distance=0) instead of
        /// using a quality value. <see cref="JxlQuality"/> is ignored in that case.
        /// </summary>
        public bool JxlLossless { get => jxlLossless; set => SetValue(ref jxlLossless, value); }

        /// <summary>
        /// cjxl encoder effort, 1 to 11. Higher values take longer and produce smaller files. 11 is an expert
        /// mode that only makes sense for lossless output.
        /// </summary>
        public int JxlEffort
        {
            get => jxlEffort;
            set
            {
                value = Math.Min(11, value);
                value = Math.Max(1, value);

                SetValue(ref jxlEffort, value);
            }
        }

        /// <summary>
        /// cjxl Brotli effort, 0 to 11. Only affects the metadata boxes (colour profile, EXIF, XMP), so it has
        /// no effect on images whose metadata is removed.
        /// </summary>
        public int JxlBrotliEffort
        {
            get => jxlBrotliEffort;
            set
            {
                value = Math.Min(11, value);
                value = Math.Max(0, value);

                SetValue(ref jxlBrotliEffort, value);
            }
        }

        public PngCompressionLevel PngCompressionLevel { get => pngCompressionLevel; set => SetValue(ref pngCompressionLevel, value); }

        /// <summary>
        /// When enabled, PNG output is re-encoded by the bundled oxipng binary. The optimization is lossless:
        /// it changes filters, colour type and palette, but not the pixels.
        /// </summary>
        public bool PngUseOxipng { get => pngUseOxipng; set => SetValue(ref pngUseOxipng, value); }

        /// <summary>
        /// Uses Zopfli instead of the default deflate implementation in oxipng. It produces slightly smaller
        /// files and can take minutes per image.
        /// </summary>
        public bool PngUseZopfli { get => pngUseZopfli; set => SetValue(ref pngUseZopfli, value); }

        public WebpEncodingMethod WebpEncodingMethod { get => webpEncodingMethod; set => SetValue(ref webpEncodingMethod, value); }

        public int WebpQuality
        {
            get => webpQuality;
            set
            {
                value = Math.Min(100, value);
                value = Math.Max(0, value);

                SetValue(ref webpQuality, value);
            }
        }

        /// <summary>
        /// When enabled, EXIF, IPTC and XMP metadata is removed from optimized images. The colour profile is kept.
        /// </summary>
        public bool StripMetadata { get => stripMetadata; set => SetValue(ref stripMetadata, value); }

        /// <summary>
        /// When enabled, an image is left untouched if re-encoding it would not make the file smaller.
        /// </summary>
        public bool SkipIfLarger { get => skipIfLarger; set => SetValue(ref skipIfLarger, value); }
    }
}
