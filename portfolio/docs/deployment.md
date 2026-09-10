# Deployment and GitHub Pages operations

This site is built as static output from `portfolio/dist/`. CI should always be safe to run without Pages enabled; deployment is a separate, manual action.

## Official references

- Astro GitHub Pages guide: <https://docs.astro.build/en/guides/deploy/github/>
- GitHub Pages publishing source: <https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site>
- GitHub Pages custom domains: <https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site>
- GitHub Pages domain verification: <https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site/verifying-your-custom-domain-for-github-pages>
- GitHub Pages HTTPS: <https://docs.github.com/en/pages/getting-started-with-github-pages/securing-your-github-pages-site-with-https>

## Verified action versions used here

Checked against the official repositories and release metadata on 2026-09-10:

- `actions/checkout@v7`
- `actions/setup-node@v6`
- `actions/configure-pages@v6`
- `actions/upload-pages-artifact@v5`
- `actions/deploy-pages@v5`
- `actions/upload-artifact@v7`

Astro's official guide also continues to recommend Node 24 for Pages builds.

## Workflow behavior

The workflow file is `.github/workflows/portfolio.yml`.

### CI triggers

- `push` to `main` on portfolio-relevant paths
- `pull_request` on the same paths
- `workflow_dispatch` for manual runs

Tracked paths include:

- `.github/workflows/portfolio.yml`
- `README.md`
- `docs/**`
- `assets/projects/**`
- `projects/**`
- `portfolio/**`

### CI steps

The validation job runs, in order:

1. `npm ci --registry=https://registry.npmjs.org`
2. `npx playwright install --with-deps chromium`
3. `npm run check`
4. `npm test`
5. `npm run build`
6. `npm run test:e2e`
7. Build a separate `.root-build/` variant with a reserved test origin and `SITE_BASE_PATH=/`.
8. Exercise all public routes, metadata, source links, feeds and downloads against that isolated root build.

Only `portfolio/dist` is eligible to become a Pages artifact. The root-test output is never deployed and does not overwrite the configured production build.

CI intentionally does **not** run `npm run assets`; committed social/share/favicon assets are treated as source-controlled delivery artifacts, not a deployment-time build requirement.

### Failure diagnostics

Browser failures produce GitHub annotations. When either browser step fails, the workflow also retains the Playwright HTML report, screenshots and traces in a `portfolio-browser-failure-<attempt>` artifact for seven days. Download it from the workflow run to investigate the failure. A failure before Playwright can generate diagnostics is reported as a missing-artifact warning; the original failing step still fails CI.

## Repository variables

Configure these in **Settings -> Secrets and variables -> Actions -> Variables**.

| Variable | Default | Rule |
| --- | --- | --- |
| `SITE_URL` | `https://pauxru.github.io` | Must be an HTTPS origin only; no path, query, fragment, or credentials. |
| `SITE_BASE_PATH` | `/Paul-Professional-Projects` | Use `/` for a custom-domain root deployment. |

Examples:

- Repository Pages URL:
  - `SITE_URL=https://pauxru.github.io`
  - `SITE_BASE_PATH=/Paul-Professional-Projects`
- Future custom domain:
  - `SITE_URL=https://your-real-domain.example`
  - `SITE_BASE_PATH=/`

Do **not** put the repo path into `SITE_URL`. The path belongs in `SITE_BASE_PATH`.

## Enabling GitHub Pages

Before any manual deployment:

1. Open **Settings -> Pages** in the repository.
2. Enable Pages for the repository if it is not already enabled.
3. Set **Source** to **GitHub Actions**.

The deploy job checks this explicitly. If Pages is disabled, or the source is still a branch/folder publish mode, the job fails with a clear error instead of pretending deployment succeeded.

CI validation on push/PR does **not** require Pages to be enabled.

## Manual deployment flow

Deployment is intentionally opt-in.

1. Ensure the branch is `main`.
2. Ensure CI is green for the commit you want.
3. Confirm `SITE_URL` and `SITE_BASE_PATH` variables are correct.
4. Confirm **Settings -> Pages -> Source** is **GitHub Actions**.
5. Open **Actions -> Portfolio -> Run workflow**.
6. Leave the branch as `main`.
7. Set `deploy=true`.
8. Run the workflow.

The workflow will:

- rebuild and re-test the site
- upload only `portfolio/dist`
- serialize production deploys with a dedicated concurrency group
- expose the published URL through the `github-pages` environment output

In-flight production deployments are **not** canceled by newer runs.

## Repo base path vs custom-domain root

Two supported deployment shapes exist:

### 1. GitHub Pages repository path

- URL pattern: `https://pauxru.github.io/Paul-Professional-Projects/`
- `SITE_URL=https://pauxru.github.io`
- `SITE_BASE_PATH=/Paul-Professional-Projects`

### 2. Future custom domain

- URL pattern: `https://your-real-domain.example/`
- `SITE_URL=https://your-real-domain.example`
- `SITE_BASE_PATH=/`

Do not invent or commit a placeholder domain. A real domain, CNAME value, and DNS records should only be added once the domain is actually owned and ready.

## Custom-domain checklist

When the real domain is ready:

1. Follow GitHub's **Managing a custom domain for your GitHub Pages site** instructions from the docs above.
2. Configure the required DNS records with the actual domain provider.
3. Verify the domain ownership in GitHub to reduce takeover risk.
4. Set repository Actions variables:
   - `SITE_URL=https://your-real-domain.example`
   - `SITE_BASE_PATH=/`
5. Add `portfolio/public/CNAME` with the real domain only after the domain and DNS are final.
6. Re-run the workflow with `deploy=true`.
7. After the certificate is issued, enforce HTTPS in **Settings -> Pages**.

Do not publish guessed IP addresses or placeholder DNS values in repo documentation; use the current official GitHub Pages docs for those details at the time of setup.

## Canonicals, sitemap, robots, and social metadata

The site configuration and tests assume:

- canonical URLs are derived from `SITE_URL + SITE_BASE_PATH`
- `robots.txt` points to `${home}sitemap-index.xml`
- sitemap entries include every generated route under the active base path
- social images live in `portfolio/public/images/social-card.png` and `.svg`
- resume downloads resolve under `/resume/Paul-Rukwaro-Resume.pdf`

This is why the base-path settings must be correct before deployment.

## What the automated tests cover

The e2e suite verifies:

- 50 project cards and matching README/docs links
- 3 curated study labels
- search/filter URL behavior
- keyboard and no-JavaScript navigation
- canonical metadata and JSON-LD on public routes
- internal links and fragment IDs
- no external runtime requests
- mobile/tablet/desktop overflow checks
- RSS, robots, sitemap, 404, and resume download wiring

## Alternative static hosting

No extra repo configuration is required to use another static host. You can upload `portfolio/dist` to:

- Netlify
- Cloudflare Pages
- Azure Static Web Apps

If you do that, keep the same rule set:

- `SITE_URL` must match the final HTTPS origin
- `SITE_BASE_PATH` must match whether the site is served from `/` or a subpath
- verify canonicals, sitemap, social image URLs, and robots after switching hosts
