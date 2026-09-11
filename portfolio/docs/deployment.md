# Deployment and GitHub Pages operations

This site is built as static output from `portfolio/dist/`. CI should always be safe to run without Pages enabled; deployment is a separate, manual action.

The selected production address is **https://paulrukwaro.com/**. The code and workflow default to that origin at `/`. Local development remains at **http://127.0.0.1:4321/**; selecting a domain in code does not configure DNS or publish the site.

## Official references

- Astro GitHub Pages guide: <https://docs.astro.build/en/guides/deploy/github/>
- GitHub Pages publishing source: <https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site>
- GitHub Pages custom domains: <https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site>
- GitHub Pages domain setup and current DNS records: <https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site/managing-a-custom-domain-for-your-github-pages-site>
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
7. Build a separate `.repo-build/` variant with a reserved test origin and `SITE_BASE_PATH=/Paul-Professional-Projects`.
8. Exercise all public routes, metadata, source links, feeds and downloads against that isolated repository-path build.

Only `portfolio/dist` is eligible to become a Pages artifact. The alternate-base output is never deployed and does not overwrite the configured production build.

CI intentionally does **not** run `npm run assets`; committed social/share/favicon assets are treated as source-controlled delivery artifacts, not a deployment-time build requirement.

### Failure diagnostics

Browser failures produce GitHub annotations. When either browser step fails, the workflow also retains the Playwright HTML report, screenshots and traces in a `portfolio-browser-failure-<attempt>` artifact for seven days. Download it from the workflow run to investigate the failure. A failure before Playwright can generate diagnostics is reported as a missing-artifact warning; the original failing step still fails CI.

## Repository variables

Configure these in **Settings -> Secrets and variables -> Actions -> Variables**.

| Variable | Default | Rule |
| --- | --- | --- |
| `SITE_URL` | `https://paulrukwaro.com` | Must be an HTTPS origin only; no path, query, fragment, or credentials. |
| `SITE_BASE_PATH` | `/` | Use `/` for the selected custom-domain root deployment. |

Examples:

- Repository Pages URL:
  - `SITE_URL=https://pauxru.github.io`
  - `SITE_BASE_PATH=/Paul-Professional-Projects`
- Selected custom domain (default):
  - `SITE_URL=https://paulrukwaro.com`
  - `SITE_BASE_PATH=/`

Do **not** put the repo path into `SITE_URL`. The path belongs in `SITE_BASE_PATH`.

If repository variables still contain the old GitHub Pages origin or repository path, update them to the selected domain/root values or remove them to use the defaults.

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

### 2. Selected custom domain (default)

- URL: `https://paulrukwaro.com/`
- `SITE_URL=https://paulrukwaro.com`
- `SITE_BASE_PATH=/`

The apex domain is canonical. After both DNS names are configured, GitHub Pages can redirect `www.paulrukwaro.com` to `paulrukwaro.com`.

## paulrukwaro.com connection checklist

These are deployment steps, not actions performed by the local build. Follow the linked official instructions; DNS guidance below was checked on 2026-09-11.

1. Confirm ownership and access to the DNS account for `paulrukwaro.com`. Verify the domain in your GitHub account's **Settings -> Pages**, using the exact TXT record GitHub supplies. Keep that verification record.
2. In this repository's **Settings -> Pages**, set **Source** to **GitHub Actions**, then set **Custom domain** to `paulrukwaro.com` and save. Do this before pointing DNS at GitHub.
3. At the DNS provider, configure the apex and `www` records below. Preserve unrelated records, particularly mail `MX` and verification `TXT` records. Do not add wildcard records or change nameservers just for this setup.
4. Ensure repository Actions variables are absent (using defaults), or explicitly set `SITE_URL=https://paulrukwaro.com` and `SITE_BASE_PATH=/`.
5. Run **Actions -> Portfolio -> Run workflow**, choose `main`, and set `deploy=true`.
6. After GitHub's DNS check and certificate provisioning complete, enable **Enforce HTTPS** in repository Pages settings.
7. Open `https://paulrukwaro.com/`, a project detail page and the resume download. Confirm `https://www.paulrukwaro.com/` redirects to the apex and that sitemap/canonical URLs use the same domain.

### DNS records for GitHub Pages

Use these records when this repository is the intended website host. Resolve any existing conflicting web records deliberately; do not delete unrelated DNS records.

| Type | Host | Value |
| --- | --- | --- |
| A | `@` | `185.199.108.153` |
| A | `@` | `185.199.109.153` |
| A | `@` | `185.199.110.153` |
| A | `@` | `185.199.111.153` |
| CNAME | `www` | `pauxru.github.io` |

The `www` CNAME target has no scheme or repository path. GitHub also documents apex ALIAS/ANAME and optional IPv6 alternatives; use one appropriate apex setup rather than conflicting record types. DNS propagation and HTTPS availability can take up to 24 hours.

**No repository `CNAME` file is required for this GitHub Actions deployment.** GitHub ignores that file for custom workflow publishing; configure the domain in repository Pages settings instead.

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
