// case-study-optimised.js — k6 equivalent of ../scenarios/case-study-optimised.json
// NOT EXECUTED on this build host — k6 binary not installed. See ./README.md.
import http from 'k6/http';

export const options = {
  scenarios: {
    optimised: {
      executor: 'constant-arrival-rate',
      rate: 40,
      timeUnit: '1s',
      duration: '30s',
      preAllocatedVUs: 16,
      maxVUs: 64,
    },
  },
  thresholds: {
    http_req_failed:   ['rate<0.01'],
    http_req_duration: ['p(95)<250'],
  },
};

const BASE = __ENV.LOADRUN_BASE || 'http://127.0.0.1:5027';
const headers = { 'X-Pathology-Optimised': 'true' };

export default function () {
  http.get(`${BASE}/api/v1/orders?page=1&pageSize=25`, { headers });
  http.get(`${BASE}/api/v1/catalog/products?category=coffee&page=1&pageSize=25`, { headers });
  http.get(`${BASE}/api/v1/catalog/products/SKU-00042`, { headers });
}
