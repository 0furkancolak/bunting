# Node sample

Bunting implements [OFREP](https://openfeature.dev/docs/reference/other-technologies/ofrep/), so Node uses the stock
OpenFeature SDK and the OFREP provider — no Bunting package required.

```bash
node ../seed.mjs            # creates project "demo", two flags and prints a server key
npm install
BUNTING_API_KEY=bnt_srv_… npm start
```
