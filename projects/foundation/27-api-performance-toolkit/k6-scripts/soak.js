// soak.js — k6 equivalent of ../scenarios/soak.json
// NOT EXECUTED on this build host — k6 binary not installed. See ./README.md.
import http from 'k6/http';

export const options = {
  scenarios: {
    soak: {
      executor: 'constant-arrival-rate',
      rate: 15,
      timeUnit: '1s',
      duration: '3m',
      preAllocatedVUs: 8,
      maxVUs: 32,
    },
  },
  thresholds: {
    http_req_failed:   ['rate<0.01'],
    http_req_duration: ['p(95)<300'],
  },
};

const BASE = __ENV.LOADRUN_BASE || 'http://127.0.0.1:5027';

export default function () {
  http.get(`${BASE}/`);
  http.get(`${BASE}/api/v1/catalog/products/SKU-00042`);
}
