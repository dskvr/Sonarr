using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public class EpisodeTrackFile : ModelBase
    {
        public int EpisodeId { get; set; }
        public int TrackId { get; set; }
        public int EpisodeFileId { get; set; }
        public LazyLoaded<EpisodeFile> EpisodeFile { get; set; }
    }
}
