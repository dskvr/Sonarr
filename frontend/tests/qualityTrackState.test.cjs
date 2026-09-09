const assert = require('node:assert/strict');
const { test } = require('node:test');
const {
  getEpisodeQualityTrackState,
  getQualityTrackTotals,
  getImportQualityTrackTargets,
  getImportTargetKeys,
  getHistoryQualityTrackIds,
  hasQualityTrackVersions
} = require('../src/Series/QualityProfiles/qualityTrackState.ts');

test('legacy and migrated single-profile presentation stays unchanged', () => {
  assert.equal(getEpisodeQualityTrackState().isMultiple, false);
  assert.equal(
    getEpisodeQualityTrackState([{ enabled: true, hasFile: false }]).isMultiple,
    false
  );
  assert.equal(hasQualityTrackVersions(), false);
  assert.equal(hasQualityTrackVersions([{ enabled: true }]), false);
});

test('enabled profile presence and cutoff are independent; retained files satisfy neither', () => {
  const state = getEpisodeQualityTrackState([
    { enabled: true, hasFile: true, cutoffNotMet: true },
    { enabled: true, hasFile: false, cutoffNotMet: true },
    { enabled: false, hasFile: true, cutoffNotMet: false }
  ]);
  assert.equal(state.isMultiple, true);
  assert.equal(state.present, 1);
  assert.equal(state.missing, 1);
  assert.equal(state.cutoffUnmet, 1);
});

test('default presentation returns after additional files and profiles are gone', () => {
  assert.equal(
    hasQualityTrackVersions([
      { enabled: true },
      { enabled: false, episodeFileCount: 3 }
    ]),
    true
  );
  assert.equal(
    hasQualityTrackVersions([
      { enabled: true },
      { enabled: false, episodeFileCount: 0 }
    ]),
    false
  );
  assert.equal(
    hasQualityTrackVersions([{ enabled: true }, { enabled: true }]),
    true
  );
});

test('version totals report partial completion without changing source episode counts', () => {
  const tracks = [
    {
      episodeCount: 10,
      episodeFileCount: 10,
      missingCount: 0,
      cutoffUnmetCount: 2
    },
    {
      episodeCount: 10,
      episodeFileCount: 3,
      missingCount: 7,
      cutoffUnmetCount: 0
    }
  ];
  assert.deepEqual(getQualityTrackTotals(tracks), {
    desired: 20,
    present: 13,
    missing: 7,
    cutoffUnmet: 2
  });
  assert.equal(tracks[0].episodeCount, 10);
  assert.deepEqual(getQualityTrackTotals([]), {
    desired: 0,
    present: 0,
    missing: 0,
    cutoffUnmet: 0
  });
});

test('imports choose sole enabled target automatically and exclude disabled targets', () => {
  const tracks = [
    { id: 10, enabled: false },
    { id: 11, enabled: true }
  ];
  assert.deepEqual(getImportQualityTrackTargets(tracks, undefined), [11]);
  assert.deepEqual(getImportQualityTrackTargets(tracks, null), [11]);
  assert.equal(getImportQualityTrackTargets(undefined, undefined), undefined);
  assert.equal(
    getImportQualityTrackTargets([{ id: 10, enabled: false }], undefined),
    undefined
  );
  assert.equal(
    getImportQualityTrackTargets(
      [
        { id: 10, enabled: true },
        { id: 11, enabled: true }
      ],
      undefined
    ),
    undefined
  );
});

test('persisted import intent is never silently redirected or filled after clearing', () => {
  const tracks = [
    { id: 10, enabled: false },
    { id: 11, enabled: true }
  ];
  assert.deepEqual(getImportQualityTrackTargets(tracks, [10]), [10]);
  assert.deepEqual(getImportQualityTrackTargets(tracks, []), []);
});

test('two files may target different versions of same episode but conflicting targets collide', () => {
  const first = getImportTargetKeys([101, 102], [10]);
  const second = getImportTargetKeys([101], [11]);
  const shared = getImportTargetKeys([101], [10, 11]);
  assert.equal(
    first.some((key) => second.includes(key)),
    false
  );
  assert.equal(
    first.some((key) => shared.includes(key)),
    true
  );
  assert.equal(
    second.some((key) => shared.includes(key)),
    true
  );
  assert.deepEqual(getImportTargetKeys([101]), ['101:primary']);
});

test('history tolerates absent, legacy, and malformed metadata without inventing targets', () => {
  assert.deepEqual(
    getHistoryQualityTrackIds({ qualityTrackIds: '[10,11]' }),
    [10, 11]
  );

  for (const qualityTrackIds of [
    undefined,
    '',
    'bad json',
    '{}',
    '["10"]',
    '[0]',
    '[-1]',
    '[1.5]'
  ]) {
    assert.equal(getHistoryQualityTrackIds({ qualityTrackIds }), undefined);
  }
});
