// Workbench load test: the common paths of a mid-size contractor at the same time.
//
//   k6 run tests/load/workbench.js                       # all scenarios (see docs/operations/performance.md)
//   k6 run -e SCENARIOS=time,approvals tests/load/workbench.js
//   k6 run -e DURATION=5m -e TIME_VUS=300 tests/load/workbench.js
//
// Needs a server with Diagnostics:ServerTiming=true (db_ms/db_commands come from the Server-Timing header), dev sign-in
// (Development, Authentication:RequireInvite=false), rate limiting off, and tests/load/fixtures.json from
// export-fixtures.sh. Approving is destructive: run against a copy of the generated database.
import { sleep, check } from 'k6';
import ws from 'k6/ws';
import http from 'k6/http';
import { Trend, Counter } from 'k6/metrics';
import { BASE, signIn, get, page, post, put, pick, mine } from './lib.js';

const fx = JSON.parse(open('./fixtures.json'));
const DURATION = __ENV.DURATION || '4m';
const enabled = (__ENV.SCENARIOS || 'time,approvals,pm,finance,sheets').split(',');
const liveLatency = new Trend('sheet_live_update_ms', true);
const liveReceived = new Counter('sheet_live_updates');

// Every scenario ramps up over RAMP (people don't all sign in in the same second), then holds for DURATION.
const RAMP = __ENV.RAMP || '30s';
function users(vus, exec) {
  return { executor: 'ramping-vus', startVUs: 0, stages: [{ duration: RAMP, target: vus }, { duration: DURATION, target: vus }], gracefulRampDown: '10s', exec };
}

const all = {
  // 300 people entering time for the current week: open the timesheet, save a day at a time, think in between.
  time: users(parseInt(__ENV.TIME_VUS || '300', 10), 'timeEntry'),
  // 50 approvers clearing last week's timesheets from their inboxes (approval posts the labor to the ledger).
  approvals: users(Math.min(50, fx.approvers.length), 'approvals'),
  // Project managers on the projects hub and their project's financials.
  pm: users(Math.min(30, fx.pms.length), 'projectManager'),
  // Finance: period close checklist, project status report, revenue preview, ICE build, trial balance.
  finance: users(Math.min(5, fx.finance.length), 'finance'),
  // Sheet users: load a 500–2,000 row grid and edit cells; listeners on the same sheets measure live-update delivery.
  sheets: users(parseInt(__ENV.SHEET_VUS || '40', 10), 'sheetEditor'),
  sheetListeners: users(parseInt(__ENV.LISTENER_VUS || '40', 10), 'sheetListener'),
};

export const options = {
  scenarios: Object.fromEntries(Object.entries(all).filter(([k]) => enabled.includes(k) || (k === 'sheetListeners' && enabled.includes('sheets')))),
  thresholds: {
    'http_req_duration{kind:page}': ['p(95)<500'],
    failed_requests: ['rate<0.01'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],
  discardResponseBodies: false,
  noCookiesReset: true, // each VU signs in once and keeps its session
};

function weekdays(today) {
  const d = new Date(`${today}T12:00:00Z`);
  const monday = new Date(d);
  monday.setUTCDate(d.getUTCDate() - ((d.getUTCDay() + 6) % 7));
  return [0, 1, 2, 3, 4].map(i => {
    const x = new Date(monday);
    x.setUTCDate(monday.getUTCDate() + i);
    return x.toISOString().slice(0, 10);
  });
}

const week = weekdays(fx.today);

export function timeEntry() {
  signIn(mine(fx.staff));
  page('/time', 'page /time');
  const opened = post(`/api/v1/time/timesheets/mine?date=${week[0]}`, undefined, 'POST timesheets/mine');
  if (!check(opened, { 'timesheet opened': r => r.status === 200 })) {
    sleep(5);
    return;
  }

  const detail = opened.json();
  const codes = (detail.chargeCodes || []).map(c => c.chargeCode).filter(c => c.kind === 0 || c.kind === 'Direct');
  const code = codes.length > 0 ? codes[0] : (detail.chargeCodes[0] || {}).chargeCode;
  if (!code || detail.timesheet.status !== 0) {
    sleep(10); // Already submitted, or nothing to charge.
    return;
  }

  for (const day of week) {
    sleep(2 + Math.random() * 4);
    put(`/api/v1/time/timesheets/${detail.timesheet.id}/entries`,
      { entries: [{ wbsElementId: code.wbsElementId, workDate: day, hours: 8 }], day }, 'PUT timesheet entries');
  }

  get(`/api/v1/time/timesheets/${detail.timesheet.id}`, 'GET timesheet');
  sleep(5 + Math.random() * 5);
}

export function approvals() {
  signIn(mine(fx.approvers));
  page('/approvals', 'page /approvals');
  const inbox = get('/api/v1/approvals/inbox', 'GET approvals/inbox');
  const items = inbox.status === 200 ? inbox.json() : [];
  for (const item of items.slice(0, 5)) {
    sleep(2 + Math.random() * 3); // Look at it.
    get(`/api/v1/approvals/${item.documentType}/${item.documentId}/history`, 'GET approval history');
    post(`/api/v1/approvals/${item.documentType}/${item.documentId}/decision`, { decision: 0, comment: null }, 'POST approval decision',
      '120s', [{ min: 200, max: 399 }, 400]); // 400: someone else (an admin) decided it first
  }

  get('/api/v1/approvals/inbox/count', 'GET approvals/inbox/count');
  sleep(items.length === 0 ? 15 : 3);
}

export function projectManager() {
  const pm = mine(fx.pms);
  signIn(pm.email);
  page('/projects', 'page /projects');
  get('/api/v1/project-hub', 'GET project-hub');
  const project = pick(pm.projects);
  sleep(2 + Math.random() * 3);
  page(`/projects/${project}`, 'page /projects/{id}');
  get(`/api/v1/project-hub/${project}`, 'GET project-hub/{id}');
  sleep(2 + Math.random() * 3);
  page(`/projects/${project}/financials`, 'page /projects/{id}/financials');
  get(`/api/v1/projects/${project}/financials/summary`, 'GET financials/summary');
  get(`/api/v1/projects/${project}/financials/wbs`, 'GET financials/wbs');
  get(`/api/v1/projects/${project}/financials/time-phased`, 'GET financials/time-phased');
  sleep(5 + Math.random() * 5);
}

export function finance() {
  signIn(mine(fx.finance));
  const step = (__ITER + __VU) % 5;
  if (step === 0) {
    page('/accounting/close', 'page /accounting/close');
    get(`/api/v1/accounting/periods/${fx.lastPeriod}/close`, 'GET period close checklist', '300s');
  } else if (step === 1) {
    post('/api/v1/reports/standard/project-status/run', { asOf: fx.today }, 'POST report project-status', '300s');
  } else if (step === 2) {
    get(`/api/v1/revenue/periods/${fx.currentPeriod}/preview`, 'GET revenue preview', '300s');
  } else if (step === 3) {
    const list = get('/api/v1/incurred-cost', 'GET incurred-cost list');
    const existing = (list.status === 200 ? list.json() : []).find(s => s.fiscalYear === fx.iceYear);
    if (existing) {
      post(`/api/v1/incurred-cost/${existing.id}/regenerate`, undefined, 'POST ICE regenerate', '300s');
    } else {
      post('/api/v1/incurred-cost', { fiscalYear: fx.iceYear }, 'POST ICE create', '300s');
    }
  } else {
    get(`/api/v1/accounting/reports/trial-balance/${fx.currentPeriod}`, 'GET trial balance', '300s');
  }

  sleep(10 + Math.random() * 10);
}

function sheetFor(vu) {
  return fx.sheets[vu % Math.min(fx.sheets.length, 8)];
}

export function sheetEditor() {
  const sheet = sheetFor(__VU);
  signIn(sheet.editors[__VU % sheet.editors.length]);
  page(`/sheets/${sheet.id}`, 'page /sheets/{id}');
  get(`/api/v1/sheets/${sheet.id}`, 'GET sheet');
  const items = get(`/api/v1/sheets/${sheet.id}/items`, 'GET sheet items');
  if (items.status !== 200) {
    sleep(5);
    return;
  }

  const rows = items.json().filter(i => i.parentId);
  for (let i = 0; i < 6; i++) {
    sleep(3 + Math.random() * 4);
    const item = pick(rows);
    const values = Object.assign({}, item.values, { [sheet.notes]: `Edited by load test at ${Date.now()}` });
    put(`/api/v1/sheets/${sheet.id}/items/${item.id}`,
      { title: item.title, start: item.start, finish: item.finish, costpointWbsId: item.costpointWbsId, values }, 'PUT sheet item');
  }
}

/** A desktop-style client on the sheets hub (SignalR JSON protocol over a WebSocket), timing each change it receives. */
export function sheetListener() {
  const sheet = sheetFor(__VU + 3);
  signIn(sheet.editors[(__VU + 7) % sheet.editors.length]);
  const cookies = http.cookieJar().cookiesForURL(BASE);
  const cookie = Object.entries(cookies).map(([k, v]) => `${k}=${v[0]}`).join('; ');
  const url = `${BASE.replace('http', 'ws')}/hubs/sheets`;
  const RS = '\u001e';
  ws.connect(url, { headers: { Cookie: cookie } }, socket => {
    socket.on('open', () => {
      socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
      socket.send(JSON.stringify({ type: 1, invocationId: '1', target: 'JoinSheet', arguments: [sheet.id] }) + RS);
    });
    socket.on('message', data => {
      for (const frame of String(data).split(RS).filter(f => f.length > 0)) {
        const msg = JSON.parse(frame);
        if (msg.type === 1 && msg.target === 'SheetChanged') {
          const change = msg.arguments[0];
          liveLatency.add(Date.now() - Date.parse(change.occurredAtUtc));
          liveReceived.add(1);
        } else if (msg.type === 6) {
          socket.send(JSON.stringify({ type: 6 }) + RS); // ping
        }
      }
    });
    socket.setTimeout(() => socket.close(), 60000);
  });
}
