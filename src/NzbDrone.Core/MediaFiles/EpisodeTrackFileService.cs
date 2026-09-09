using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeTrackFileService
    {
        List<EpisodeTrackFile> GetAll();
        List<EpisodeTrackFile> GetForEpisode(int episodeId);
        List<EpisodeTrackFile> GetForSeries(int seriesId);
        List<EpisodeTrackFile> GetForFile(int fileId);
        List<int> ReplaceLinks(int seriesId, List<EpisodeTrackFile> links);
        List<int> ImportFile(EpisodeFile file, List<EpisodeTrackFile> links);
        List<int> UpdateFile(EpisodeFile file, List<EpisodeTrackFile> links, bool replaceAllFileLinks = false);
        void RemoveFile(int fileId);
        void DeleteFile(int fileId);
        bool IsFileReferenced(int fileId);
        Dictionary<int, int> GetFileCountsByTrack();
    }

    public class EpisodeTrackFileService : IEpisodeTrackFileService
    {
        private readonly IEpisodeTrackFileRepository _repository;

        public EpisodeTrackFileService(IEpisodeTrackFileRepository repository)
        {
            _repository = repository;
        }

        public List<EpisodeTrackFile> GetAll() => _repository.All().ToList();

        public List<EpisodeTrackFile> GetForEpisode(int episodeId) => _repository.GetForEpisode(episodeId);

        public List<EpisodeTrackFile> GetForSeries(int seriesId) => _repository.GetForSeries(seriesId);

        public List<EpisodeTrackFile> GetForFile(int fileId) => _repository.GetForFile(fileId);

        public List<int> ReplaceLinks(int seriesId, List<EpisodeTrackFile> links) => _repository.ReplaceLinks(seriesId, links);

        public List<int> ImportFile(EpisodeFile file, List<EpisodeTrackFile> links) => _repository.ImportFile(file, links);

        public List<int> UpdateFile(EpisodeFile file, List<EpisodeTrackFile> links, bool replaceAllFileLinks = false) => _repository.UpdateFile(file, links, replaceAllFileLinks);

        public void RemoveFile(int fileId) => _repository.RemoveFile(fileId);

        public void DeleteFile(int fileId) => _repository.DeleteFile(fileId);

        public bool IsFileReferenced(int fileId) => _repository.IsFileReferenced(fileId);

        public Dictionary<int, int> GetFileCountsByTrack() => _repository.GetFileCountsByTrack();
    }
}
