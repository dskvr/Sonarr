import type {
  ApplyAdditionalQualityProfiles,
  default as Series,
} from 'Series/Series';
import type { QualityProfileModel } from 'Settings/Profiles/Quality/useQualityProfiles';

export function getAdditionalQualityProfileIds(series: Series) {
  return (
    series.additionalQualityProfileIds ??
    series.qualityTracks
      ?.filter((track) => track.enabled && !track.isPrimary)
      .map((track) => track.qualityProfileId) ??
    []
  );
}

export function applyAdditionalQualityProfiles(
  current: number[],
  selected: number[],
  apply: ApplyAdditionalQualityProfiles | 'noChange'
) {
  switch (apply) {
    case 'add':
      return [...new Set([...current, ...selected])];
    case 'remove':
      return current.filter((id) => !selected.includes(id));
    case 'replace':
      return [...new Set(selected)];
    default:
      return current;
  }
}

export function hasOverlappingQualities(profiles: QualityProfileModel[]) {
  const qualities = new Set<number>();

  return profiles.some((profile) => {
    const allowed = profile.items.flatMap((item) => {
      if (!item.allowed) {
        return [];
      }

      return 'quality' in item
        ? [item.quality.id]
        : item.items.map((groupItem) => groupItem.quality.id);
    });

    const overlaps = allowed.some((id) => qualities.has(id));
    allowed.forEach((id) => qualities.add(id));
    return overlaps;
  });
}
