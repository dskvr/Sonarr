import type { OrganizePreviewModel } from './useOrganizePreview';

function getSafeRenameFiles(
  items: ReadonlyArray<OrganizePreviewModel>,
  selectedIds: ReadonlyArray<number>
) {
  return items
    .filter((item) => !item.error && selectedIds.includes(item.episodeFileId))
    .map((item) => item.episodeFileId);
}

export default getSafeRenameFiles;
