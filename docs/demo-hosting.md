# Hosting the public demo

An always-on Workbench with sample data that you can send testers a link to. It replaces the GitHub Codespace demo
(`.devcontainer/README.md`), which stops after at most 4 idle hours and has to be restarted by hand.

**Demo data only.** Anyone with the link can sign in as any demo user, including the administrator. Never enter real
company data, and never point the demo at a real database (it refuses one: see [Safeguards](#safeguards)).

## What runs

- **Host:** one always-on [Fly.io](https://fly.io) machine (shared CPU, 1 GB memory) with a 1 GB volume at `/data`,
  in the `iad` (Virginia) region. About **US$6 a month** at Fly.io's pay-as-you-go prices; it needs a Fly.io account
  with a card on file. Fly.io terminates HTTPS and gives the app `https://<app>.fly.dev`.
- **Image:** the `demo` target of the repository's `Dockerfile` (the same build stage as production, on the regular
  ASP.NET image so the entrypoint can hand the root-owned volume to the non-root `app` user).
- **Settings:** `ASPNETCORE_ENVIRONMENT=Demo` loads `src/Workbench.Web/appsettings.Demo.json`, and `Demo__Enabled=true`
  turns on demo mode. Both are set in the image. Demo mode means:
  - SQLite at `/data/workbench.db`, migrated and seeded with the sample data on a fresh database;
  - the demo sign-in page (`/account/dev-signin`): pick a sample user, no passwords and no identity provider;
  - the AI assistant turned on with the **fake provider** (canned answers, no model and no cost), up to 100 questions
    per person per day;
  - email written to `/data/mail` instead of being sent; SCIM off; the sample Costpoint CSVs as the Costpoint source;
  - a **nightly reset at 08:00 UTC** (`Demo:ResetHourUtc`; turn off with `Demo:NightlyReset=false`): deletes the
    database, attachments and mail, then stops; Fly.io restarts the machine and it seeds fresh data. Testers signed
    in at that moment are sent back to the sign-in page.

| Sign in as | Try |
|---|---|
| Dana Lee (dev@workbench.local) | Administrator: Admin, settings, the assistant's settings |
| Pat Morgan (pm@example.com) | Finance and project manager of project 1001 |
| Sam Patel (sam.pm@example.com) | Project manager only (project 1002) |
| Harper Reed (hr@example.com) | HR: people, positions, benefits, open enrollment |
| Mia Torres (mia.materials@example.com) | Materials: parts, bills of material, inventory, work orders |
| Blake Diaz (bd@example.com) | Business development: pipeline, opportunities, accounts |
| Riley Quinn (pricing@example.com) | Pricing: forward pricing rates, proposal PRP-2027-014 |
| Alex Rivera (analyst@example.com) | Employee (timesheets, expenses) |
| Val Chen (viewer@example.com) | Employee with view-only sheet access |

## First-time setup (the account owner does this once)

1. Create a Fly.io account at <https://fly.io/app/sign-up> and add a card. Install `flyctl`
   (`brew install flyctl`), then `fly auth login`.
2. From the repository root, create the app and its volume (pick another app name if `workbench-demo` is taken, and
   change `app` and `Notifications__PublicBaseUrl` in `deploy/demo/fly.toml` to match):

   ```bash
   fly apps create workbench-demo
   fly volumes create workbench_demo_data --app workbench-demo --region iad --size 1 --yes
   ```

3. Deploy (Fly.io builds the image on its own builders):

   ```bash
   fly deploy --config deploy/demo/fly.toml --dockerfile Dockerfile --remote-only
   ```

4. Open `https://workbench-demo.fly.dev/`: it should show **Workbench demo** with the users above. Send testers that
   link.

## Redeploy on every push to master

`.github/workflows/demo.yml` redeploys the demo when master changes, and smoke-tests the sign-in page. It does
nothing until you set it up in GitHub (**Settings → Secrets and variables → Actions**):

- variable `FLY_DEMO_APP` = `workbench-demo`
- secret `FLY_API_TOKEN` = the output of `fly tokens create deploy --app workbench-demo` (a token that can only
  deploy this app)

A redeploy keeps the day's data and applies new migrations. New sample data from a change to the seeds appears after
the next nightly reset, or right away with a manual reset.

## Reset by hand

From GitHub: **Actions → Demo → Run workflow**, tick **Start over with fresh demo data**. Or from a terminal:

```bash
fly ssh console --app workbench-demo --command "workbench-demo --reset-demo"
```

```bash
fly apps restart workbench-demo
```

## Look after it

- Logs: `fly logs --app workbench-demo`. Status and health checks: `fly status --app workbench-demo`.
- Emails the app would have sent: `fly ssh console --app workbench-demo`, then look in `/data/mail`.
- Take it down: `fly scale count 0 --app workbench-demo` (stops billing for the machine; the volume still costs a few
  cents), or `fly apps destroy workbench-demo` to delete everything.

## Safeguards

Demo mode is deliberately hard to switch on anywhere else (`DemoMode`, `ProductionConfigurationCheck`, tested in
`tests/Workbench.Tests/Operations/DemoModeTests.cs`):

- It needs **both** `ASPNETCORE_ENVIRONMENT=Demo` and `Demo:Enabled=true`. `Demo:Enabled` in Production, Staging or
  any other environment stops the host at startup, and the dev sign-in page and fake assistant stay off there.
- The fake assistant provider is refused outside Development and demo mode.
- Demo mode is SingleTenant only and refuses to send email (`Email:Mode` must be `Pickup`).
- Demo mode only seeds or resets a demo database: empty, or one with the demo administrator `dev@workbench.local`.
  A database with other users and no demo administrator stops startup and `--reset-demo` before anything is written
  or deleted.
- Invitations stay required: only the listed demo users can sign in; another email is refused.

## Other hosts

The `demo` image runs anywhere that keeps a container running with a persistent volume at `/data` and restarts it when
it exits (the nightly reset relies on that): Render (a web service with a disk mounted at `/data`, about US$7.25 a
month), Azure Container Apps (minimum replicas 1, but its Azure Files volumes don't suit SQLite's locking) or any
Docker host (`docker run -d --restart always -p 80:8080 -v workbench-demo:/data workbench:demo` behind a TLS proxy).
The workflow only targets Fly.io.
