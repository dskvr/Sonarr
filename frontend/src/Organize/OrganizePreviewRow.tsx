import React, { useCallback, useEffect } from 'react';
import { useSelect } from 'App/Select/SelectContext';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Icon from 'Components/Icon';
import { icons, kinds } from 'Helpers/Props';
import { CheckInputChanged } from 'typings/inputs';
import translate from 'Utilities/String/translate';
import { OrganizePreviewModel } from './useOrganizePreview';
import styles from './OrganizePreviewRow.css';

interface OrganizePreviewRowProps {
  id: number;
  existingPath: string;
  newPath: string;
  error?: string;
}

function OrganizePreviewRow({
  id,
  existingPath,
  newPath,
  error,
}: OrganizePreviewRowProps) {
  const { toggleSelected, useIsSelected } = useSelect<OrganizePreviewModel>();
  const isSelected = useIsSelected(id);

  const handleSelectedChange = useCallback(
    ({ value, shiftKey }: CheckInputChanged) => {
      toggleSelected({
        id,
        isSelected: value,
        shiftKey,
      });
    },
    [id, toggleSelected]
  );

  useEffect(() => {
    if (error) {
      return;
    }

    toggleSelected({
      id,
      isSelected: true,
      shiftKey: false,
    });
  }, [id, error, toggleSelected]);

  return (
    <div className={styles.row}>
      <CheckInput
        containerClassName={styles.selectedContainer}
        name={id.toString()}
        ariaLabel={translate('SelectRow')}
        value={isSelected}
        isDisabled={!!error}
        onChange={handleSelectedChange}
      />

      <div>
        <div>
          <Icon name={icons.SUBTRACT} kind={kinds.DANGER} />

          <span className={styles.path}>{existingPath}</span>
        </div>

        <div>
          <Icon name={icons.ADD} kind={kinds.SUCCESS} />

          <span className={styles.path}>{newPath}</span>
        </div>
        {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}
      </div>
    </div>
  );
}

export default OrganizePreviewRow;
