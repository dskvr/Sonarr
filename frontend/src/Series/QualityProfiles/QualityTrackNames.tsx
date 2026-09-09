import React from 'react';
import Label from 'Components/Label';
import Series from 'Series/Series';
import QualityProfileName from 'Settings/Profiles/Quality/QualityProfileName';
import translate from 'Utilities/String/translate';
import { hasQualityTrackVersions } from './qualityTrackState';

interface QualityTrackNamesProps {
  series: Series;
  trackIds?: number[];
}

function QualityTrackNames({ series, trackIds }: QualityTrackNamesProps) {
  const tracks = series.qualityTracks ?? [];

  if (!hasQualityTrackVersions(tracks) || !trackIds?.length) {
    return null;
  }

  return (
    <div>
      {trackIds.map((id) => {
        const track = tracks.find((item) => item.id === id);
        return track ? (
          <Label key={id} kind={track.enabled ? 'info' : 'default'}>
            <QualityProfileName qualityProfileId={track.qualityProfileId} />
            {track.enabled ? '' : ` (${translate('Retained')})`}
          </Label>
        ) : null;
      })}
    </div>
  );
}

export default QualityTrackNames;
