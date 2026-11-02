namespace MetadataImageOptimizer.Optimization
{
    /// <summary>
    /// Describes the outcome of a single optimization run.
    /// </summary>
    public sealed class OptimizationResult
    {
        public OptimizationResult(
            string path
            , bool converted
            , bool keptOriginal
            , long originalBytes
            , long optimizedBytes
            , string format
            , int? quality
            , bool targetReached)
        {
            Path = path;
            Converted = converted;
            KeptOriginal = keptOriginal;
            OriginalBytes = originalBytes;
            OptimizedBytes = optimizedBytes;
            Format = format;
            Quality = quality;
            TargetReached = targetReached;
        }

        /// <summary>
        /// The path of the optimized image. When <see cref="Converted"/> is <c>false</c> this is the original
        /// file, which has been overwritten in place. When it is <c>true</c> this points to a temporary file
        /// that the caller is responsible for moving into place.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// <c>true</c> when the image was written to a new file because the format changed.
        /// </summary>
        public bool Converted { get; }

        /// <summary>
        /// <c>true</c> when the original file was left untouched because re-encoding would not have made it smaller.
        /// </summary>
        public bool KeptOriginal { get; }

        public long OriginalBytes { get; }

        public long OptimizedBytes { get; }

        /// <summary>
        /// The format that was written, normalized to lowercase without a leading dot.
        /// </summary>
        public string Format { get; }

        /// <summary>
        /// The quality that was used to encode the image. <c>null</c> for lossless formats (PNG).
        /// </summary>
        public int? Quality { get; }

        /// <summary>
        /// Whether the requested size budget was reached. Always <c>true</c> when no budget was configured.
        /// </summary>
        public bool TargetReached { get; }

        /// <summary>
        /// The number of bytes saved. Negative values mean the image grew.
        /// </summary>
        public long BytesSaved => OriginalBytes - OptimizedBytes;

        /// <summary>
        /// The percentage of the original size that was saved. Negative values mean the image grew.
        /// </summary>
        public double PercentSaved => OriginalBytes <= 0 ? 0 : (double)BytesSaved / OriginalBytes * 100.0;

        public static OptimizationResult Unchanged(string path, long originalBytes, string format)
        {
            return new OptimizationResult(path, false, true, originalBytes, originalBytes, format, null, true);
        }
    }
}
