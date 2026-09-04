// stress.js — k6 equivalent of ../scenarios/stress.json
// NOT EXECUTED on this build host — k6 binary not installed. See ./README.md.
import http from 'k6/http';

export const options = {
  scenarios: {
    stress: {
      executor: 'ramping-arrival-rate',
      startRate: 20,
      timeUnit: '1s',
      preAllocatedVUs: 32,
      maxVUs: 128,
      stages: [
        { duration: '5s', target: 60 },
        { duration: '5s', target: 100 },
        { duration: '5s', target: 140 },
        { duration: '5s', target: 180 },
        { duration: '5s', target: 220 },
        { duration: '5s', target: 260 },
        { duration: '5s', target: 300 },
        { duration: '5s', target: 340 },
        { duration: '5s', target: 380 },
        { duration: '5s', target: 400 },
      ],
    },
  },
};

const BASE = __ENV.LOADRUN_BASE || 'http://127.0.0.1:5027';

export default function () {
  http.get(`${BASE}/`);
}
