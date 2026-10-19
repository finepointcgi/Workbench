# Workbench demo in a GitHub Codespace

This codespace runs Workbench with its demo data so you can share a link for testing.

## Share a test link

1. On GitHub: **Code → Codespaces → Create codespace on master** (4-core machine). The first start builds the app,
   which takes a few minutes.
2. Workbench starts by itself on port **5104** (`bash .devcontainer/start-demo.sh` starts it again).
3. In the **Ports** tab, right-click port 5104 → **Port visibility → Public**, then copy the forwarded address
   (`https://<codespace-name>-5104.app.github.dev/`) and send it to testers.
4. Testers open the link and pick a demo user on the sign-in page. There are no passwords.

| Sign in as | Role |
|---|---|
| Dana Lee (dev@workbench.local) | Administrator |
| Pat Morgan (pm@example.com) | Finance and project manager of project 1001 |
| Sam Patel (sam.pm@example.com) | Project manager only (project 1002) |
| Alex Rivera (analyst@example.com) | Employee |
| Val Chen (viewer@example.com) | Employee with view-only sheet access |

## Things to know

- **Demo data only.** Anyone with the public link can sign in as any demo user, including the administrator. Never
  enter real company data here.
- The link works while the codespace is running. GitHub stops an idle codespace after 30 minutes by default (raise it
  in your GitHub Codespaces settings, up to 4 hours); starting it again brings back the same link and data.
- Start over with fresh demo data: `bash .devcontainer/start-demo.sh --reset`.
- Logs: `/tmp/workbench-demo.log`. Emails the app would send are written to `/tmp/workbench-mail`.
- Codespaces usage is billed to the GitHub account that owns the codespace (personal accounts get a monthly free
  allowance).
