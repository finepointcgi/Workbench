# Quick start

## Try it on your own computer (about two minutes)

You need [Docker](https://docs.docker.com/get-docker/) and Git. This runs Workbench with sample data so you can click around.
**It is for trying only.** Anyone who can reach the page can sign in as any sample user, including the administrator, and the
database resets every night. Do not put real company data in it.

```bash
git clone https://github.com/finepointcgi/workbench.git
cd workbench
docker build --target demo -t workbench-demo .
docker run --rm -p 8080:8080 workbench-demo
```

Open <http://localhost:8080>, pick **Dana Lee** (the administrator), and look around. If port 8080 is already in use on your
machine, change the first number: `-p 8090:8080`, then open <http://localhost:8090>.

The first build takes a few minutes. Press Ctrl+C to stop; the sample data goes with the container.

Good things to try: the **Plan** tab on project 1001, an invoice under Finance, **Setup › Migration** (the Costpoint importer),
and the phone app at `/m/timesheet` (shrink the browser window, or open the page on your phone).

## Run it for real

A real installation runs on one server with PostgreSQL, behind HTTPS, with people signing in through your identity provider
(Microsoft Entra ID, Google, Okta or any OpenID Connect provider). `docker-compose.prod.yml` is a complete single-host example:
PostgreSQL, a migration step, the web host and a TLS proxy.

1. Point a DNS name at a Linux server with Docker.
2. Run `scripts/prod/init-secrets.sh` to create the database password and keys, and fill in your identity provider's details.
3. Run `docker compose -f docker-compose.prod.yml up -d --build`.
4. Sign in as the bootstrap administrator and follow the setup checklist on the home page.

The full guide, with every setting, backups, upgrades and monitoring, is in [`docs/operations/README.md`](operations/README.md).
It assumes someone comfortable running a server; if that is not you, ask us about hosting.

## Moving your data

Under **Setup › Migration** there are importers for Costpoint, Smartsheet and Unanet, with a checklist that takes you through
importing each kind of data, checking the totals and running a month in parallel. Try it on a copy of your data first.

## Business features

Community is complete for one company. To use single sign-on, more than one company, integrations, customer links, the supplier
portal, custom approval workflows or incurred cost submission, add a Business license key under **Setup › License**. See
[`editions.md`](editions.md).
