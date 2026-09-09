import type { EpisodeQualityTrack } from 'Episode/Episode';
import type { QualityTrackStatistics, SeriesQualityTrack } from 'Series/Series';

export function getEpisodeQualityTrackState(tracks?: EpisodeQualityTrack[]) {
  const enabled = tracks?.filter((track) => track.enabled) ?? [];
  const present = enabled.filter((track) => track.hasFile).length;
  const cutoffUnmet = enabled.filter(
    (track) => track.hasFile && track.cutoffNotMet
  ).length;

  return {
    enabled,
    isMultiple: enabled.length > 1,
    present,
    missing: enabled.length - present,
    cutoffUnmet,
  };
}

export function getQualityTrackTotals(tracks: QualityTrackStatistics[]) {
  return tracks.reduce(
    (totals, track) => ({
      desired: totals.desired + track.episodeCount,
      present: totals.present + track.episodeFileCount,
      missing: totals.missing + track.missingCount,
      cutoffUnmet: totals.cutoffUnmet + track.cutoffUnmetCount,
    }),
    { desired: 0, present: 0, missing: 0, cutoffUnmet: 0 }
  );
}

export function getImportQualityTrackTargets(
  tracks: SeriesQualityTrack[] | undefined,
  targets: number[] | null | undefined
) {
  // Preserve saved intent, including removed targets. Import validation must
  // explain stale intent instead of redirecting a download to another profile.
  if (targets != null) {
    return targets;
  }

  const enabled = tracks?.filter((track) => track.enabled) ?? [];
  return enabled.length === 1 ? [enabled[0].id] : undefined;
}

export function hasQualityTrackVersions(tracks?: SeriesQualityTrack[]) {
  return (
    (tracks?.filter((track) => track.enabled).length ?? 0) > 1 ||
    (tracks?.some((track) => !track.enabled && track.episodeFileCount > 0) ??
      false)
  );
}

export function getImportTargetKeys(episodeIds: number[], targets?: number[]) {
  return episodeIds.flatMap((id) =>
    targets?.length
      ? targets.map((trackId) => `${id}:${trackId}`)
      : [`${id}:primary`]
  );
}

export function getHistoryQualityTrackIds(data: { qualityTrackIds?: string }) {
  if (!data.qualityTrackIds) {
    return undefined;
  }

  try {
    const ids: unknown = JSON.parse(data.qualityTrackIds);
    return Array.isArray(ids) &&
      ids.every((id) => Number.isInteger(id) && id > 0)
      ? (ids as number[])
      : undefined;
  } catch {
    return undefined;
  }
}
