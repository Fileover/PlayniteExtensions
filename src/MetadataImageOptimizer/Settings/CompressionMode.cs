namespace MetadataImageOptimizer.Settings
{
    /// <summary>
    /// Determines how the optimizer decides how aggressively an image should be compressed.
    /// </summary>
    public enum CompressionMode
    {
        /// <summary>
        /// Compress using the quality values configured on the 'Quality' tab, without a hard size limit.
        /// </summary>
        Quality = 0,

        /// <summary>
        /// Keep lowering the quality until the resulting file fits within the configured size budget.
        /// </summary>
        TargetSize = 1
    }
}
