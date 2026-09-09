import React, { useId, useMemo } from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes } from 'Helpers/Props';
import { SeriesQualityTrack } from 'Series/Series';
import { useQualityProfilesData } from 'Settings/Profiles/Quality/useQualityProfiles';
import { InputChanged } from 'typings/inputs';
import translate from 'Utilities/String/translate';

interface QualityTrackSelectProps {
  tracks: SeriesQualityTrack[];
  value: number[];
  onChange: (change: InputChanged<number | number[]>) => void;
}

function QualityTrackSelect({
  tracks,
  value,
  onChange,
}: QualityTrackSelectProps) {
  const name = useId();
  const profiles = useQualityProfilesData();
  const values = useMemo(
    () =>
      tracks
        .filter((track) => track.enabled || value.includes(track.id))
        .map((track, order) => ({
          key: track.id,
          value: `${
            profiles.find((profile) => profile.id === track.qualityProfileId)
              ?.name ?? track.qualityProfileId
          }${track.enabled ? '' : ` (${translate('Retained')})`}`,
          order,
        })),
    [tracks, value, profiles]
  );

  return (
    <FormGroup>
      <FormLabel name={name}>{translate('TargetQualityProfiles')}</FormLabel>
      <FormInputGroup
        type={inputTypes.TAG_SELECT}
        name={name}
        value={value}
        values={values}
        allowNew={false}
        minQueryLength={0}
        helpText={translate('TargetQualityProfilesHelpText')}
        onChange={onChange}
      />
    </FormGroup>
  );
}

export default QualityTrackSelect;
