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

## Run it for real (about ten minutes)

You need a Linux server (or any machine) with Docker and Docker Compose, a name such as `workbench.yourcompany.com` pointing at
it, and ports 80 and 443 reachable from the internet so it can get an HTTPS certificate for you.

```bash
git clone https://github.com/finepointcgi/workbench.git
cd workbench
scripts/install.sh --domain workbench.yourcompany.com --admin you@yourcompany.com
```

That creates the secrets, builds and starts PostgreSQL, Workbench and the HTTPS proxy, and prints what to do next. People sign
in with built-in email-and-password accounts (with an authenticator app); single sign-on can be added later.

1. Open the link the installer printed. You choose your password there and are signed in. (It works once, for 60 minutes;
   run `scripts/first-login-link.sh` for a new one.)
2. Follow the setup checklist on the home page.
3. Set up email so invites and password resets are sent ([Email](operations/email.md)).
4. Back up the `secrets/` folder somewhere safe. It holds the keys that protect your data
   ([Data Protection keys](operations/data-protection-keys.md)).
5. Set up nightly backups ([Backup and restore](operations/backup-restore.md)).

To try the installer on your own computer first, use `--domain localhost --http-port 8088 --https-port 8443`; your browser will
warn about the test certificate.

**Upgrade** with `scripts/upgrade.sh` (take a backup first). The database migration finishes before the new version starts.

Other ways to run it (Kubernetes, your own identity provider, several servers) and every setting are in
[Operations](operations/README.md); `docker-compose.prod.yml` is the file the installer uses.

## Moving your data

Under **Setup › Migration** there are importers for Costpoint, Smartsheet and Unanet, with a checklist that takes you through
importing each kind of data, checking the totals and running a month in parallel. Try it on a copy of your data first.

## Business features

Community is complete for one company. To use single sign-on, more than one company, integrations, customer links, the supplier
portal, custom approval workflows or incurred cost submission, add a Business license key under **Setup › License**. See
[Editions and licensing](editions.md).
