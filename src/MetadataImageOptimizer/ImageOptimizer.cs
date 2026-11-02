using MetadataImageOptimizer.Optimization;
using MetadataImageOptimizer.Settings;

namespace MetadataImageOptimizer
{
    public static class ImageOptimizer
    {
        /// <summary>
        /// Optimizes an image with the specified requirements
        /// </summary>
        /// <param name="imagePath">The full path to the image that should be optimized</param>
        /// <param name="imageSettings">The settings to use for optimizing the image</param>
        /// <param name="qualitySettings">The quality settings for optimizing the image</param>
        /// <returns>The path of the optimized image. This is the original path when the image was optimized in
        /// place, or a temporary file when the image was converted to another format.</returns>
        public static string Optimize(string imagePath, ImageTypeSettings imageSettings, QualitySettings qualitySettings)
        {
            return MazanokeOptimizer.Optimize(imagePath, imageSettings, qualitySettings).Path;
        }
    }
}
