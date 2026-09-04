// case-study-baseline.js — k6 equivalent of ../scenarios/case-study-baseline.json
// NOT EXECUTED on this build host — k6 binary not installed. See ./README.md.
import http from 'k6/http';

export const options = {
  scenarios: {
    baseline: {
      executor: 'constant-arrival-rate',
      rate: 40,
      timeUnit: '1s',
      duration: '30s',
      preAllocatedVUs: 16,
      maxVUs: 64,
    },
  },
  thresholds: {
    http_req_failed:   ['rate<0.20'],
    http_req_duration: ['p(95)<5000'],
  },
};

const BASE = __ENV.LOADRUN_BASE || 'http://127.0.0.1:5027';

const pathHeaders = {
  'X-Pathology-NPlusOne': 'true',
  'X-Pathology-DownstreamLatencyMs': '20',
};
const indexHeaders = { 'X-Pathology-MissingIndex': 'true' };

export default function () {
  http.get(`${BASE}/api/v1/orders?page=1&pageSize=25`, { headers: pathHeaders });
  http.get(`${BASE}/api/v1/catalog/products?category=coffee&page=1&pageSize=25`, { headers: indexHeaders });
  http.get(`${BASE}/api/v1/catalog/products/SKU-00042`, {
    headers: { ...indexHeaders, 'X-Pathology-DownstreamLatencyMs': '20' },
  });
}
