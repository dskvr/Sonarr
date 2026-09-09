import type Episode from './Episode';

function mergeEpisodeUpdate(current: Episode, updated: Episode): Episode {
  return {
    ...updated,
    // Omitted collections were not computed by this event. An explicit empty
    // array is authoritative and must clear versions that no longer exist.
    episodeFiles: updated.episodeFiles ?? current.episodeFiles,
    qualityTracks: updated.qualityTracks ?? current.qualityTracks,
  };
}

export default mergeEpisodeUpdate;
