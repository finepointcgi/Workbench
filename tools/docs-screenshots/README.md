# Guide screenshots

Takes every screenshot in the user and administrator guide (`docs/assets/screenshots`) from a running Workbench with
the demo data, signed in as the demo user who'd use each page. Re-run it after a UI change so the guide matches the app.

`shots.mjs` lists the shots: the file name, the demo user, the page, and any clicks before the shot. `shoot.mjs` runs them
in Chromium at 1440 × 900.

## Run it

1. Start a fresh Workbench on its own database, so the shots always show the same demo data and your dev database is
   left alone. From the repository root:

   ```bash
   rm -f tools/docs-screenshots/docs.db*
   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5210 \
     ConnectionStrings__Workbench="Data Source=$PWD/tools/docs-screenshots/docs.db" \
     Email__PickupFolder="$PWD/tools/docs-screenshots/mail" \
     dotnet run --project src/Workbench.Web --no-launch-profile
   ```

   (In Claude Code, the `workbench-docs` entry in `.claude/launch.json` starts the same thing.)

2. In another terminal, from this folder:

   ```bash
   npm install
   npx playwright install chromium
   node shoot.mjs                  # every shot
   node shoot.mjs time admin-users # only shots whose names start with these
   ```

   `WORKBENCH_URL` points it at another address.

The welcome tour shot (`getting-started-welcome-tour`) needs the employee's first visit to Home, so take it on a fresh
database, before any other shot as that user (the list already does this).

## Preview the guide

```bash
python3 -m venv .venv && .venv/bin/pip install -r docs/requirements.txt
.venv/bin/mkdocs serve      # http://127.0.0.1:8000
```

Pushing to `master` publishes it to GitHub Pages (`.github/workflows/docs.yml`).
