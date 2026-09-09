import React from 'react';
import Label from 'Components/Label';
import { EpisodeQualityTrack } from 'Episode/Episode';
import { EpisodeFile } from 'EpisodeFile/EpisodeFile';
import { useEpisodeFile } from 'EpisodeFile/EpisodeFileProvider';
import QualityProfileName from 'Settings/Profiles/Quality/QualityProfileName';
import translate from 'Utilities/String/translate';
import { getEpisodeQualityTrackState } from './qualityTrackState';
import styles from './AdditionalQualityProfiles.css';

interface EpisodeQualityTrackSummaryProps {
  tracks?: EpisodeQualityTrack[];
  files?: ReadonlyArray<EpisodeFile>;
  showFiles?: boolean;
}

function TrackFile({
  id,
  files,
}: {
  id: number;
  files?: ReadonlyArray<EpisodeFile>;
}) {
  const cached = useEpisodeFile(id);
  const file = files?.find((item) => item.id === id) ?? cached;

  return file ? (
    <div className={styles.file}>
      {file.relativePath} · {file.quality.quality.name}
    </div>
  ) : null;
}

function EpisodeQualityTrackSummary({
  tracks,
  files,
  showFiles = false,
}: EpisodeQualityTrackSummaryProps) {
  const state = getEpisodeQualityTrackState(tracks);
  const retained =
    tracks?.filter((track) => !track.enabled && track.hasFile) ?? [];

  if (!state.isMultiple && !retained.length) {
    return null;
  }

  const visible = state.isMultiple ? [...state.enabled, ...retained] : retained;

  return (
    <div className={styles.profileList}>
      {visible.map((track) => {
        let kind: 'default' | 'danger' | 'warning' | 'success' = 'success';
        let status = translate('Downloaded');

        if (!track.enabled) {
          kind = 'default';
          status = translate('Retained');
        } else if (!track.hasFile) {
          kind = 'danger';
          status = translate('Missing');
        } else if (track.cutoffNotMet) {
          kind = 'warning';
          status = translate('CutoffUnmet');
        }

        return (
          <div key={track.trackId}>
            <Label kind={kind}>
              <QualityProfileName qualityProfileId={track.qualityProfileId} />
              {': '}
              {status}
            </Label>
            {showFiles && track.hasFile ? (
              <TrackFile id={track.episodeFileId} files={files} />
            ) : null}
          </div>
        );
      })}
    </div>
  );
}

export default EpisodeQualityTrackSummary;
