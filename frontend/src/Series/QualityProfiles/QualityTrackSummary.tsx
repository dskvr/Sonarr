import React from 'react';
import { useQueueDetailsForSeries } from 'Activity/Queue/Details/QueueDetailsProvider';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import { QualityTrackStatistics } from 'Series/Series';
import QualityProfileName from 'Settings/Profiles/Quality/QualityProfileName';
import { useQualityProfilesData } from 'Settings/Profiles/Quality/useQualityProfiles';
import translate from 'Utilities/String/translate';
import { hasOverlappingQualities } from './qualityProfileSelection';
import styles from './AdditionalQualityProfiles.css';

interface QualityTrackSummaryProps {
  seriesId: number;
  seasonNumber?: number;
  tracks?: QualityTrackStatistics[];
  showOverlap?: boolean;
}

function QualityTrackSummary({
  seriesId,
  seasonNumber,
  tracks,
  showOverlap = false,
}: QualityTrackSummaryProps) {
  const profiles = useQualityProfilesData();
  const { queuedVersionsCount } = useQueueDetailsForSeries(
    seriesId,
    seasonNumber
  );

  if (!tracks || tracks.length < 2) {
    return null;
  }

  return (
    <div className={styles.profileList}>
      {showOverlap &&
      hasOverlappingQualities(
        profiles.filter((profile) =>
          tracks.some((track) => track.qualityProfileId === profile.id)
        )
      ) ? (
        <Alert kind="info">
          {translate('AdditionalQualityProfilesOverlap')}
        </Alert>
      ) : null}
      {tracks.map((track) => {
        let kind: 'danger' | 'warning' | 'success' = 'success';

        if (track.missingCount) {
          kind = 'danger';
        } else if (track.cutoffUnmetCount) {
          kind = 'warning';
        }

        const profile = profiles.find(
          (item) => item.id === track.qualityProfileId
        );
        const cutoff = profile?.items.find(
          (item) =>
            ('quality' in item ? item.quality.id : item.id) === profile.cutoff
        );
        let cutoffName = '';

        if (cutoff) {
          cutoffName = 'quality' in cutoff ? cutoff.quality.name : cutoff.name;
        }

        const cutoffSummary = profile?.upgradeAllowed
          ? `${translate('UpgradeUntil')}: ${cutoffName}`
          : translate('QualityTrackUpgradesDisabled');

        return (
          <Label
            key={track.trackId}
            kind={kind}
            title={`${translate('QualityTrackProgressDetail', {
              missing: track.missingCount,
              cutoffUnmet: track.cutoffUnmetCount,
            })} · ${cutoffSummary}`}
          >
            <QualityProfileName qualityProfileId={track.qualityProfileId} />
            {': '}
            {track.episodeFileCount} / {track.episodeCount}
            {track.cutoffUnmetCount
              ? ` · ${translate('QualityTrackCutoffUnmetCount', {
                  count: track.cutoffUnmetCount,
                })}`
              : ''}
          </Label>
        );
      })}
      {queuedVersionsCount ? (
        <Label kind="purple">
          {translate('QualityTrackQueuedCount', { count: queuedVersionsCount })}
        </Label>
      ) : null}
    </div>
  );
}

export default QualityTrackSummary;
