import type Queue from 'typings/Queue';

function getSeriesQueueDetails(
  queue: ReadonlyArray<Queue> | undefined,
  seriesId: number,
  seasonNumber?: number
) {
  const episodes = new Map<number, boolean>();
  const versionSlots = new Set<string>();

  queue?.forEach((item) => {
    if (
      item.trackedDownloadState === 'imported' ||
      item.seriesId !== seriesId
    ) {
      return;
    }

    item.episodes?.forEach((episode) => {
      if (seasonNumber != null && episode.seasonNumber !== seasonNumber) {
        return;
      }

      episodes.set(episode.id, episodes.get(episode.id) || episode.hasFile);
      item.targetQualityTrackIds?.forEach((trackId) =>
        versionSlots.add(`${episode.id}:${trackId}`)
      );
    });
  });

  return {
    count: episodes.size,
    episodesWithFiles: [...episodes.values()].filter(Boolean).length,
    queuedVersionsCount: versionSlots.size,
  };
}

export default getSeriesQueueDetails;
