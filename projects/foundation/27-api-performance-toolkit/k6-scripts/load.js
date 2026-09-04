// load.js — k6 equivalent of ../scenarios/load.json
// NOT EXECUTED on this build host — k6 binary not installed. See ./README.md.
import http from 'k6/http';
import { check } from 'k6';

export const options = {
  stages: [
    { duration: '10s', target: 10 },
    { duration: '20s', target: 25 },
    { duration: '5s',  target: 0  },
  ],
  thresholds: {
    http_req_failed:   ['rate<0.02'],
    http_req_duration: ['p(95)<500'],
  },
};

const BASE = __ENV.LOADRUN_BASE || 'http://127.0.0.1:5027';

export default function () {
  check(http.get(`${BASE}/`), { 'root 200': (r) => r.status === 200 });
  check(http.get(`${BASE}/api/v1/catalog/products?page=1&pageSize=20`), { 'products 200': (r) => r.status === 200 });
}
