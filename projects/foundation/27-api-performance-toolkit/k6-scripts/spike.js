// spike.js — k6 equivalent of ../scenarios/spike.json
// NOT EXECUTED on this build host — k6 binary not installed. See ./README.md.
import http from 'k6/http';

export const options = {
  scenarios: {
    spike: {
      executor: 'ramping-vus',
      startVUs: 2,
      stages: [
        { duration: '5s',  target: 2 },
        { duration: '2s',  target: 40 },
        { duration: '10s', target: 40 },
        { duration: '2s',  target: 2  },
        { duration: '11s', target: 2 },
      ],
      gracefulRampDown: '3s',
    },
  },
  thresholds: {
    http_req_failed:  ['rate<0.10'],
    http_req_duration: ['p(99)<2000'],
  },
};

const BASE = __ENV.LOADRUN_BASE || 'http://127.0.0.1:5027';

export default function () {
  http.get(`${BASE}/`);
  http.get(`${BASE}/api/v1/catalog/products/SKU-00042`);
}
