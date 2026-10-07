// Task Center pull simulation (T004-21, NFR-004-01).
//
// Walks GET /tasks exactly like Task Center's INITIAL load: first page with modifiedAfter, then keyset paging with the
// (modifiedAt, urn) of the last task, until an empty page comes back. Asserts every task is delivered exactly once and
// that a page of 1000 is served in < 2 s (p95); SAP's own budget is 30 s.
//
// Prerequisites: a deployed (or local) instance with ~10 000 tasks, e.g. created with the admin bulk generator
// (spec 005, POST /admin/api/tasks/bulk, sameTimestamp=true for the worst case), and a technical client.
//
//   k6 run -e BASE_URL=https://<fqdn> -e CLIENT_ID=tc-tech -e CLIENT_SECRET=... -e EXPECTED_TASKS=10000 tests/perf/k6-pull.js
//
// Optional: -e SPI_BASE=/api/task-provider/v2   -e PAGE_SIZE=1000   -e LANGUAGES=en-US,de-DE
//
// The in-process counterpart that runs in CI is SpiPerformanceTests (5 000 tasks).

import http from 'k6/http';
import { check, fail } from 'k6';
import { Trend, Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const SPI_BASE = __ENV.SPI_BASE || '/task-provider/v2';
const PAGE_SIZE = parseInt(__ENV.PAGE_SIZE || '1000', 10);
const LANGUAGES = __ENV.LANGUAGES || 'en-US,de-DE';
const EXPECTED = parseInt(__ENV.EXPECTED_TASKS || '10000', 10);
const START = __ENV.MODIFIED_AFTER || '2000-01-01T00:00:00.000Z';

const pageDuration = new Trend('pull_page_duration', true);
const tasksSeen = new Counter('pull_tasks_seen');
const duplicates = new Counter('pull_duplicates');

export const options = {
  // One virtual user: the pull is inherently sequential, and we need one view of "seen".
  scenarios: {
    pull: { executor: 'shared-iterations', vus: 1, iterations: 1, maxDuration: '30m' },
  },
  thresholds: {
    pull_page_duration: ['p(95)<2000'],
    pull_duplicates: ['count==0'],
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
  },
};

export default function () {
  const tokenRes = http.post(
    `${BASE_URL}/oauth/token`,
    { grant_type: 'client_credentials', client_id: __ENV.CLIENT_ID, client_secret: __ENV.CLIENT_SECRET },
    { tags: { name: 'token' } },
  );
  if (!check(tokenRes, { 'token issued': (r) => r.status === 200 })) fail('could not get a token: ' + tokenRes.body);
  const headers = { Authorization: `Bearer ${tokenRes.json('access_token')}` };

  const seen = new Set();
  let after = START;
  let lastId = null;

  for (let page = 0; page < 10000; page++) {
    let url = `${BASE_URL}${SPI_BASE}/tasks?languages=${LANGUAGES}&$top=${PAGE_SIZE}&modifiedAfter=${after}`;
    if (lastId) url += `&lastId=${encodeURIComponent(lastId)}`;

    const res = http.get(url, { headers, tags: { name: 'pull' } });
    pageDuration.add(res.timings.duration);
    if (!check(res, { 'page 200': (r) => r.status === 200 })) fail(`pull failed: ${res.status} ${res.body}`);

    const tasks = res.json('value');
    if (tasks.length === 0) break; // an empty page means "no more data" (TC-PULL-03)

    for (const t of tasks) {
      if (seen.has(t.urn)) duplicates.add(1);
      seen.add(t.urn);
    }
    tasksSeen.add(tasks.length);

    const last = tasks[tasks.length - 1];
    after = last.modifiedAt;
    lastId = last.urn;
  }

  check(seen, { [`all ${EXPECTED} tasks delivered exactly once`]: (s) => s.size === EXPECTED });
}
