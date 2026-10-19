// The heavy Finance paths one at a time, as one user on an idle server: how long each takes on its own.
//   k6 run tests/load/bench.js            (ITERATIONS=3 by default; the first run warms caches)
import { signIn, get, post, page } from './lib.js';

const fx = JSON.parse(open('./fixtures.json'));

export const options = {
  scenarios: { bench: { executor: 'per-vu-iterations', vus: 1, iterations: parseInt(__ENV.ITERATIONS || '3', 10), maxDuration: '30m' } },
  summaryTrendStats: ['avg', 'med', 'min', 'max', 'count'],
  noCookiesReset: true,
};

export default function () {
  signIn(fx.finance[0]);
  const project = fx.pms[0].projects[0];
  get(`/api/v1/accounting/periods/${fx.lastPeriod}/close`, 'GET period close checklist', '600s');
  post('/api/v1/reports/standard/project-status/run', { asOf: fx.today }, 'POST report project-status', '600s');
  post('/api/v1/reports/standard/project-status/run', { asOf: fx.today, projectId: project }, 'POST report project-status (one project)', '600s');
  get(`/api/v1/revenue/periods/${fx.currentPeriod}/preview`, 'GET revenue preview', '600s');
  const list = get('/api/v1/incurred-cost', 'GET incurred-cost list');
  const existing = (list.status === 200 ? list.json() : []).find(s => s.fiscalYear === fx.iceYear);
  if (existing) {
    post(`/api/v1/incurred-cost/${existing.id}/regenerate`, undefined, 'POST ICE regenerate', '600s');
  } else {
    post('/api/v1/incurred-cost', { fiscalYear: fx.iceYear }, 'POST ICE create', '600s');
  }

  get(`/api/v1/accounting/reports/trial-balance/${fx.currentPeriod}`, 'GET trial balance', '600s');
  get('/api/v1/project-hub', 'GET project-hub (finance: every project)', '600s');
  page('/projects', 'page /projects (finance)');
  get('/api/v1/approvals/inbox', 'GET approvals/inbox');
  page('/time', 'page /time');
  page(`/sheets/${fx.sheets[0].id}`, 'page /sheets/{id} (2,000 rows)');
  get(`/api/v1/sheets/${fx.sheets[0].id}/items`, 'GET sheet items (2,000 rows)');
}
