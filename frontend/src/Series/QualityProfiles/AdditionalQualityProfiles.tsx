import React, {
  ReactNode,
  SyntheticEvent,
  useCallback,
  useMemo,
  useState,
} from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes } from 'Helpers/Props';
import { useQualityProfilesData } from 'Settings/Profiles/Quality/useQualityProfiles';
import { InputChanged } from 'typings/inputs';
import { Failure } from 'typings/pending';
import translate from 'Utilities/String/translate';
import { hasOverlappingQualities } from './qualityProfileSelection';
import styles from './AdditionalQualityProfiles.css';

interface AdditionalQualityProfilesProps {
  qualityProfileId: number | string;
  value: number[];
  size?: 'small' | 'medium';
  isInitiallyOpen?: boolean;
  showInput?: boolean;
  errors?: Failure[];
  warnings?: Failure[];
  before?: ReactNode;
  children?: ReactNode;
  onChange: (change: InputChanged<number | number[]>) => void;
}

function AdditionalQualityProfiles({
  qualityProfileId,
  value,
  size = 'small',
  isInitiallyOpen = false,
  showInput = true,
  errors,
  warnings,
  before,
  children,
  onChange,
}: AdditionalQualityProfilesProps) {
  const [isOpen, setIsOpen] = useState(isInitiallyOpen);
  const handleToggle = useCallback(
    (event: SyntheticEvent<HTMLDetailsElement>) => {
      setIsOpen(event.currentTarget.open);
    },
    []
  );
  const profiles = useQualityProfilesData();
  const values = useMemo(() => {
    return profiles
      .filter(
        (profile) =>
          profile.id !== qualityProfileId || value.includes(profile.id)
      )
      .map((profile, order) => ({
        key: profile.id,
        value: profile.name,
        order,
      }));
  }, [profiles, qualityProfileId, value]);
  const overlaps = hasOverlappingQualities(
    profiles.filter(
      (profile) => profile.id === qualityProfileId || value.includes(profile.id)
    )
  );

  return (
    <details
      className={styles.disclosure}
      open={isOpen}
      onToggle={handleToggle}
    >
      <summary className={isOpen ? styles.openSummary : styles.summary}>
        {translate('KeepAnotherVersion')}
      </summary>

      {before}

      {showInput ? (
        <FormGroup size={size}>
          <FormLabel name="additionalQualityProfileIds">
            {translate('AdditionalQualityProfiles')}
          </FormLabel>

          <FormInputGroup
            type={inputTypes.TAG_SELECT}
            name="additionalQualityProfileIds"
            value={value}
            values={values}
            allowNew={false}
            minQueryLength={0}
            errors={errors}
            warnings={warnings}
            helpText={translate('AdditionalQualityProfilesHelpText')}
            helpTextWarning={
              overlaps
                ? translate('AdditionalQualityProfilesOverlap')
                : undefined
            }
            onChange={onChange}
          />
        </FormGroup>
      ) : null}

      {children}
    </details>
  );
}

export default AdditionalQualityProfiles;
