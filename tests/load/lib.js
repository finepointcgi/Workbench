// Shared helpers for the Workbench k6 scenarios: dev sign-in per virtual user, JSON calls, and the Server-Timing
// header (db;dur=…;desc="commands") recorded as db_ms / db_commands trends tagged by endpoint.
import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Trend, Rate, Counter } from 'k6/metrics';

export const BASE = __ENV.BASE_URL || 'http://localhost:5300';
export const dbMs = new Trend('db_ms', true);
export const dbCommands = new Trend('db_commands');
export const appMs = new Trend('app_ms', true);
export const failures = new Rate('failed_requests');
export const signIns = new Counter('sign_ins');

const signedIn = {};

/** Signs the VU in once as `email` through the Development sign-in page (needs Authentication:RequireInvite=false). */
export function signIn(email) {
  if (!email) {
    fail('no user for this virtual user in fixtures.json');
  }

  if (signedIn[__VU] === email) {
    return;
  }

  const jar = http.cookieJar();
  jar.clear(BASE);
  const page = http.get(`${BASE}/account/dev-signin`, { tags: { name: 'signin-page' }, responseType: 'text' });
  const token = /name="__RequestVerificationToken" value="([^"]+)"/.exec(page.body || '');
  if (!token) {
    fail(`no antiforgery token on the sign-in page (${page.status})`);
  }

  const res = http.post(`${BASE}/account/dev-signin`, { email, returnUrl: '/', __RequestVerificationToken: token[1] },
    { redirects: 0, tags: { name: 'signin' } });
  if (!check(res, { 'signed in': r => r.status === 302 && !String(r.headers.Location || '').includes('access-denied') })) {
    sleep(30); // Don't hammer the sign-in page: a refused user stays refused.
    fail(`sign-in failed for ${email}: ${res.status} ${res.headers.Location}`);
  }

  signedIn[__VU] = email;
  signIns.add(1);
}

function record(res, name, expected = null) {
  const header = res.headers['Server-Timing'] || '';
  const db = /db;dur=([\d.]+);desc="(\d+)"/.exec(header);
  const app = /app;dur=([\d.]+)/.exec(header);
  const tags = { name };
  if (db) {
    dbMs.add(parseFloat(db[1]), tags);
    dbCommands.add(parseInt(db[2], 10), tags);
  }

  if (app) {
    appMs.add(parseFloat(app[1]), tags);
  }

  const ok = (res.status >= 200 && res.status < 400) || (expected !== null && expected.includes(res.status));
  failures.add(!ok, tags);
  if (!ok && __ENV.VERBOSE) {
    console.warn(`${name}: ${res.status} ${String(res.body).slice(0, 300)}`);
  }

  return res;
}

const json = { 'Content-Type': 'application/json', Accept: 'application/json' };

export function get(path, name, timeout = '60s') {
  return record(http.get(`${BASE}${path}`, { headers: json, tags: { name }, timeout }), name);
}

/** A page as the browser first loads it: the server prerenders the Blazor component (its data loads run). */
export function page(path, name) {
  return record(http.get(`${BASE}${path}`, { headers: { Accept: 'text/html' }, tags: { name, kind: 'page' }, timeout: '60s' }), name);
}

export function post(path, body, name, timeout = '120s', expected = null) {
  const params = { headers: json, tags: { name }, timeout };
  if (expected) {
    params.responseCallback = http.expectedStatuses(...expected);
  }

  return record(http.post(`${BASE}${path}`, body === undefined ? null : JSON.stringify(body), params), name, expected);
}

export function put(path, body, name) {
  return record(http.put(`${BASE}${path}`, JSON.stringify(body), { headers: json, tags: { name }, timeout: '60s' }), name);
}

export function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

/** Each VU gets its own person from a list (wrapping around), so two VUs rarely edit the same timesheet. */
export function mine(list) {
  return list[(__VU - 1) % list.length];
}
