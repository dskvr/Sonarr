const assert = require('node:assert/strict');
const { test } = require('node:test');
const {
  captureImportMapping,
  hasSameImportMapping
} = require('../src/InteractiveImport/importMapping.ts');

function existingFile() {
  return {
    id: 17,
    episodeFileId: 1,
    series: { id: 1 },
    seasonNumber: 1,
    episodes: [{ id: 101 }, { id: 102 }],
    targetQualityTrackIds: [1],
    quality: { quality: { id: 9 } },
    releaseGroup: 'Original'
  };
}

test('original mapping survives nested client edits and reprocess replacements', () => {
  const firstFetch = existingFile();
  const original = captureImportMapping(firstFetch);
  firstFetch.series.id = 2;
  firstFetch.episodes[0].id = 201;
  firstFetch.targetQualityTrackIds.push(3);

  assert.deepEqual(original, {
    id: 17,
    seriesId: 1,
    seasonNumber: 1,
    episodeIds: [101, 102],
    targetQualityTrackIds: [1]
  });
  assert.equal(hasSameImportMapping(firstFetch, original), false);
  const reprocessed = { ...firstFetch, releaseGroup: 'Reprocessed' };
  assert.equal(hasSameImportMapping(reprocessed, original), false);
});

test('ordinary metadata edits continue using metadata endpoint', () => {
  const file = existingFile();
  const original = captureImportMapping(file);
  assert.equal(
    hasSameImportMapping(
      { ...file, releaseGroup: 'Changed', quality: { quality: { id: 7 } } },
      original
    ),
    true
  );
});

test('adopting existing file into an additional profile requires import command', () => {
  const file = existingFile();
  const original = captureImportMapping(file);
  assert.equal(
    hasSameImportMapping({ ...file, targetQualityTrackIds: [1, 3] }, original),
    false
  );
  assert.equal(
    hasSameImportMapping({ ...file, targetQualityTrackIds: [3] }, original),
    false
  );
  assert.equal(
    hasSameImportMapping({ ...file, targetQualityTrackIds: [] }, original),
    false
  );
});

test('episode or series reassignment requires import even after reprocess', () => {
  const file = existingFile();
  const original = captureImportMapping(file);
  assert.equal(
    hasSameImportMapping({ ...file, series: { id: 2 } }, original),
    false
  );
  assert.equal(
    hasSameImportMapping({ ...file, seasonNumber: 2 }, original),
    false
  );
  assert.equal(
    hasSameImportMapping({ ...file, episodes: [{ id: 103 }] }, original),
    false
  );
  assert.equal(hasSameImportMapping(file, undefined), false);
});

test('order changes and omitted target fields do not force reimport', () => {
  const file = { ...existingFile(), targetQualityTrackIds: [1, 3] };
  const original = captureImportMapping(file);
  assert.equal(
    hasSameImportMapping(
      {
        ...file,
        episodes: [{ id: 102 }, { id: 101 }],
        targetQualityTrackIds: [3, 1]
      },
      original
    ),
    true
  );
  assert.equal(
    hasSameImportMapping(
      { ...file, targetQualityTrackIds: undefined },
      original
    ),
    true
  );
  const legacy = { ...existingFile(), targetQualityTrackIds: undefined };
  assert.equal(
    hasSameImportMapping(
      { ...legacy, targetQualityTrackIds: null },
      captureImportMapping(legacy)
    ),
    true
  );
  assert.equal(
    hasSameImportMapping(legacy, captureImportMapping(legacy)),
    true
  );
});
