namespace NzbDrone.Core.SeriesStats
{
    public class QualityTrackStatistics
    {
        public int TrackId { get; set; }
        public int QualityProfileId { get; set; }
        public int EpisodeCount { get; set; }
        public int EpisodeFileCount { get; set; }
        public int CutoffUnmetCount { get; set; }
        public int MissingCount => EpisodeCount - EpisodeFileCount;
    }
}
