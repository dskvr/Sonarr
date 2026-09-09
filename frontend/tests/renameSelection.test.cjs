const assert = require('node:assert/strict');
const { test } = require('node:test');
const getSafeRenameFiles =
  require('../src/Organize/getSafeRenameFiles.ts').default;

test('collision rows stay visible in preview but cannot enter rename command', () => {
  const previews = [
    { episodeFileId: 1, existingPath: 'old.mkv', newPath: 'new.mkv' },
    {
      episodeFileId: 3,
      existingPath: 'retained.mp4',
      newPath: 'new.mp4',
      error: 'A retained version already uses the destination stem'
    }
  ];
  assert.deepEqual(getSafeRenameFiles(previews, [1, 3]), [1]);
  assert.deepEqual(getSafeRenameFiles(previews, [3]), []);
  assert.equal(previews.length, 2);
  assert.ok(previews[1].error);
});

test('empty, stale, and deselected IDs never produce an all-files rename', () => {
  const previews = [{ episodeFileId: 1 }];
  assert.deepEqual(getSafeRenameFiles(previews, []), []);
  assert.deepEqual(getSafeRenameFiles(previews, [99]), []);
  assert.deepEqual(getSafeRenameFiles([], [1]), []);
  assert.deepEqual(getSafeRenameFiles(previews, [1]), [1]);
});
