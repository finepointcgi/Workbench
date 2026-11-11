# AI assistants (MCP)

Workbench runs a [Model Context Protocol](https://modelcontextprotocol.io) server at `/api/v1/mcp`. An MCP client, such as
Hearth, Claude Code, Claude Desktop or another AI assistant, can connect to it and answer questions with Workbench data, for
example "which CLINs run out of funding this quarter?" or "who hasn't submitted a timesheet?".

The server offers the in-app assistant's read tools, plus tools that **change sheets**: add and update rows, comment, and assign
tasks or set their status. Nothing approves, signs, submits or pays, and there is no SQL or free-form query. Changes are made as
the key's service user, so they show in activity and history under the key's name. The client brings its own model: Workbench's assistant settings, model connection
and token limits don't apply, and the assistant doesn't have to be turned on.

## What the client sends where

Tool results go to the AI client and to whatever model it uses. A local model (Ollama, LM Studio and so on, through Hearth)
keeps the data on your network. A hosted model sends it to that provider. Choose the key's scopes and roles with that in mind.

## Connecting a client

1. Under **Admin › Integrations › API keys**, create a key for the assistant (see [API keys](api-keys.md)):
    - **Roles:** what the assistant may see. It sees exactly what a person with those roles would, so an Employee-role key
      sees only an employee's data. Use the narrowest roles that answer the questions you want.
    - **Scopes:** **AI assistants (MCP)** read access, which lets the key connect, plus read access to each area whose tools
      it should have:

      | Area | Tools |
      | --- | --- |
      | Projects and contracts | `list_projects`, `get_project_financials`, `get_budget_versions`, `get_earned_value`, `list_clin_funding`, `get_ceilings`, `get_project_staffing` |
      | Accounting | `get_project_cost_ledger` |
      | Time and expenses | `get_timesheet_status`, `get_hours_by_charge_code`, `list_unallowable_costs` |
      | Setup and approvals | `list_pending_approvals`, `get_custom_fields`, `list_custom_field_matches` |
      | Sheets, dashboards and tasks | `list_sheets`, `read_sheet` |
      | Sheets, dashboards and tasks, **read and write** | `add_sheet_row`, `update_sheet_row`, `add_comment`, `assign_task`, `set_task_status` |

      A key only sees the tools its scopes allow: give the sheets area write access only to a key that should change sheets.
      Timesheet anomaly flags are never offered over MCP.
2. Add the server to the client with the address `https://<your Workbench>/api/v1/mcp` and the header
   `Authorization: Bearer wbk_…`.

**Hearth:** open **Tools and MCP**, choose **Workbench** under **Add an integration**, and paste the address and key.

**Claude Code:**

```bash
claude mcp add --transport http workbench https://workbench.example.com/api/v1/mcp --header "Authorization: Bearer wbk_…"
```

**Any other client** that supports remote (streamable HTTP) servers:

```json
{
  "mcpServers": {
    "workbench": {
      "type": "http",
      "url": "https://workbench.example.com/api/v1/mcp",
      "headers": { "Authorization": "Bearer wbk_…" }
    }
  }
}
```

## How it is protected

- **API keys only.** People signed in to Workbench can't call the endpoint with their own session; a key without the
  `mcp:read` scope gets `403`. Requests carrying a browser `Origin` header are refused, so web pages can't reach it.
- **The usual permission checks.** Each tool call runs as the key's service user through the same permission-checked services
  as the pages: restricted sheet columns stay hidden, masked custom fields stay masked, and Finance-only data needs the Finance
  role.
- **Audited.** Every tool call is recorded in the security event log as `mcp.tool_call`, with the tool, its arguments and the
  outcome (never the data). Find them under **Admin › Users and security › Security events**.
- **Bounded.** Results are cut at the assistant's result limit (`Assistant:MaxToolResultChars`, 24,000 characters by default),
  and requests are rate limited per key like any other API call.
- **Changes ask first in the client.** The write tools are marked as not read-only, so clients such as Hearth ask you before
  each one runs, unless you tell the client to run that server's tools without asking. `update_sheet_row`, `assign_task` and
  `set_task_status` are also marked as replacing what was there.
- **Results are data.** Text read from Workbench (sheet rows, project names) could contain instructions. The server tells the
  client that results are data, not instructions, but the client and model are responsible for treating them that way.

Revoke the key to disconnect the client at once.

## Protocol details

The server uses the streamable HTTP transport statelessly: each JSON-RPC request is a `POST` answered with a single JSON
response. There are no sessions and no server-sent event stream (`GET` and `DELETE` return `405`). It supports protocol versions
`2025-11-25`, `2025-06-18` and `2025-03-26`, and the methods `initialize`, `ping`, `tools/list` and `tools/call`. Tool results
contain the data as JSON text and `resource_link` entries for the Workbench pages that show it.

## Linking to Keel and Meridian

Workbench, Keel PLM and Meridian point at each other with ids, so an assistant can follow a question from one app to the next:

- Add a custom field **Keel program** (text) to projects (**Admin › Custom fields**) and fill in the Keel program code. An
  assistant finds the Workbench project for a Keel program with `list_custom_field_matches`.
- In Meridian, blocks and requirements carry the Workbench project code in the tagged value `workbench.project`.

Hearth's engineering team uses this convention.
