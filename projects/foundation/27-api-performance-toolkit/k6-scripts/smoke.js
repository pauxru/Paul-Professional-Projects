// smoke.js — k6 equivalent of ../scenarios/smoke.json
// NOT EXECUTED on this build host — k6 binary not installed. See ./README.md.
import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  vus: 2,
  duration: '5s',
  thresholds: {
    http_req_failed:  ['rate<0.01'],
    http_req_duration: ['p(95)<250'],
  },
};

const BASE = __ENV.LOADRUN_BASE || 'http://127.0.0.1:5027';

export default function () {
  check(http.get(`${BASE}/`), { 'root 200': (r) => r.status === 200 });
  check(http.get(`${BASE}/health/ready`), { 'ready 200': (r) => r.status === 200 });
  check(http.get(`${BASE}/api/v1/catalog/products/SKU-00042`), { 'product 200': (r) => r.status === 200 });
}
