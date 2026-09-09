import type InteractiveImport from './InteractiveImport';

export interface ImportMapping {
  id: number;
  seriesId?: number;
  seasonNumber: number;
  episodeIds: number[];
  targetQualityTrackIds?: number[];
}

export function captureImportMapping(item: InteractiveImport): ImportMapping {
  return {
    id: item.id,
    seriesId: item.series?.id,
    seasonNumber: item.seasonNumber,
    episodeIds: item.episodes.map((episode) => episode.id),
    targetQualityTrackIds: item.targetQualityTrackIds?.slice(),
  };
}

function sameIds(first: number[], second: number[]) {
  return (
    first.length === second.length && first.every((id) => second.includes(id))
  );
}

export function hasSameImportMapping(
  item: InteractiveImport,
  original?: ImportMapping
) {
  if (
    !original ||
    item.series?.id !== original.seriesId ||
    item.seasonNumber !== original.seasonNumber
  ) {
    return false;
  }

  if (
    !sameIds(
      item.episodes.map((episode) => episode.id),
      original.episodeIds
    )
  ) {
    return false;
  }

  // Omitted targets preserve ownership. An explicit changed set must use the
  // import command; the file metadata endpoint does not update associations.
  return (
    item.targetQualityTrackIds == null ||
    sameIds(item.targetQualityTrackIds, original.targetQualityTrackIds ?? [])
  );
}
