using System;
using System.Collections.Generic;

namespace MetadataImageOptimizer.Settings
{
    public class ImageTypeSettings : ObservableObject
    {
        private string format;
        private int maxHeight;
        private int maxWidth;
        private bool optimize;
        private CompressionMode compressionMode = CompressionMode.Quality;
        private int targetSizeKb = 500;

        public string Format { get => format; set => SetValue(ref format, value); }
        public int MaxHeight { get => maxHeight; set => SetValue(ref maxHeight, value); }
        public int MaxWidth { get => maxWidth; set => SetValue(ref maxWidth, value); }
        public bool Optimize { get => optimize; set => SetValue(ref optimize, value); }

        /// <summary>
        /// Whether the image should be compressed using a fixed quality, or compressed until it fits a size budget.
        /// </summary>
        public CompressionMode Compression
        {
            get => compressionMode;
            set => SetValue(ref compressionMode, value);
        }

        /// <summary>
        /// The maximum file size in kilobytes that should be targeted when the compression mode is set to
        /// target size.
        /// </summary>
        public int TargetSizeKb
        {
            get => targetSizeKb;
            set
            {
                value = Math.Max(1, Math.Min(102400, value));
                SetValue(ref targetSizeKb, value);
            }
        }
    }
}
