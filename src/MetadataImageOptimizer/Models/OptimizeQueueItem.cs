using System;

namespace MetadataImageOptimizer.Models
{
    public class OptimizeQueueItem
    {
        public OptimizeQueueItem(Guid gameId, bool optimizeBackground, bool optimizeCover, bool optimizeIcon, bool force = false)
        {
            GameId = gameId;
            OptimizeBackground = optimizeBackground;
            OptimizeCover = optimizeCover;
            OptimizeIcon = optimizeIcon;
            Force = force;
        }

        public Guid GameId { get; set; }
        public bool OptimizeBackground { get; set; }
        public bool OptimizeCover { get; set; }
        public bool OptimizeIcon { get; set; }

        /// <summary>
        /// Set when the user explicitly asked for the images to be optimized, which bypasses the cache.
        /// </summary>
        public bool Force { get; set; }
    }
}
