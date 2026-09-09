import React from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Series, { ApplyAdditionalQualityProfiles } from 'Series/Series';
import { useQualityProfilesData } from 'Settings/Profiles/Quality/useQualityProfiles';
import translate from 'Utilities/String/translate';
import {
  applyAdditionalQualityProfiles,
  getAdditionalQualityProfileIds,
  hasOverlappingQualities,
} from './qualityProfileSelection';
import styles from './AdditionalQualityProfiles.css';

interface AdditionalQualityProfilesPreviewProps {
  series: Series[];
  value: number[];
  qualityProfileId: number | string;
  apply: ApplyAdditionalQualityProfiles;
}

function AdditionalQualityProfilesPreview({
  series,
  value,
  qualityProfileId,
  apply,
}: AdditionalQualityProfilesPreviewProps) {
  const profiles = useQualityProfilesData();

  return (
    <div className={styles.preview} aria-live="polite">
      <p>{translate('AdditionalQualityProfilesPreview')}</p>

      {series.map((item) => {
        const current = getAdditionalQualityProfileIds(item);
        const result = applyAdditionalQualityProfiles(current, value, apply);
        const primary =
          typeof qualityProfileId === 'number'
            ? qualityProfileId
            : item.qualityProfileId;
        const retained =
          item.qualityTracks?.filter(
            (track) =>
              !track.isPrimary &&
              track.episodeFileCount > 0 &&
              !result.includes(track.qualityProfileId)
          ) ?? [];
        const overlaps = hasOverlappingQualities(
          profiles.filter(
            (profile) => profile.id === primary || result.includes(profile.id)
          )
        );

        return (
          <div key={item.id}>
            <strong>{item.title}</strong>

            <div className={styles.profileList}>
              {result.length
                ? result.map((id) => (
                    <Label
                      key={id}
                      kind={current.includes(id) ? 'info' : 'success'}
                    >
                      {profiles.find((profile) => profile.id === id)?.name ??
                        id}
                    </Label>
                  ))
                : translate('NoAdditionalQualityProfiles')}
            </div>

            {retained.length ? (
              <div className={styles.profileList}>
                {translate('RetainedVersions')}:{' '}
                {retained.map((track) => (
                  <Label key={track.id}>
                    {profiles.find(
                      (profile) => profile.id === track.qualityProfileId
                    )?.name ?? track.qualityProfileId}{' '}
                    {translate('EpisodeFilesCount', {
                      count: track.episodeFileCount,
                    })}
                  </Label>
                ))}
              </div>
            ) : null}

            {result.includes(primary) ? (
              <Alert kind="danger">
                {translate('AdditionalQualityProfileMustDiffer')}
              </Alert>
            ) : null}

            {overlaps ? (
              <Alert kind="warning">
                {translate('AdditionalQualityProfilesOverlap')}
              </Alert>
            ) : null}
          </div>
        );
      })}
    </div>
  );
}

export default AdditionalQualityProfilesPreview;
