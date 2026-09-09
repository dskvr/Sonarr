using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Tv
{
    public class SeriesQualityTrack : ModelBase
    {
        public int SeriesId { get; set; }
        public int QualityProfileId { get; set; }
        public bool IsPrimary { get; set; }
        public bool Enabled { get; set; }
        public LazyLoaded<QualityProfile> QualityProfile { get; set; }
        public LazyLoaded<List<EpisodeTrackFile>> TrackFiles { get; set; }
        public int? EpisodeFileCount { get; set; }
    }
}
