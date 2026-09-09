using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Tv
{
    public interface ISeriesQualityTrackService
    {
        List<SeriesQualityTrack> GetAllTracks();
        List<SeriesQualityTrack> GetTracks(int seriesId);
        List<SeriesQualityTrack> GetEnabledTracks(int seriesId);
        SeriesQualityTrack GetTrack(int trackId);
        bool IsProfileInUse(int profileId);
        void ValidateProfiles(int seriesId, int primaryId, IEnumerable<int> additionalIds);
    }

    public class SeriesQualityTrackService : ISeriesQualityTrackService
    {
        private readonly ISeriesQualityTrackRepository _repository;

        public SeriesQualityTrackService(ISeriesQualityTrackRepository repository)
        {
            _repository = repository;
        }

        public List<SeriesQualityTrack> GetAllTracks() => _repository.All().ToList();

        public List<SeriesQualityTrack> GetTracks(int seriesId) => _repository.GetForSeries(seriesId);

        public List<SeriesQualityTrack> GetEnabledTracks(int seriesId) => GetTracks(seriesId).Where(t => t.Enabled).ToList();

        public SeriesQualityTrack GetTrack(int trackId) => _repository.Get(trackId);

        public bool IsProfileInUse(int profileId) => _repository.IsProfileInUse(profileId);

        public void ValidateProfiles(int seriesId, int primaryId, IEnumerable<int> additionalIds) => _repository.ValidateProfiles(seriesId, primaryId, additionalIds);
    }
}
