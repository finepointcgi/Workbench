# AI assistant: data flows and controls

For ISO 27001 and NIST SP 800-171 reviewers. Workbench's assistant uses Claude (Anthropic) to answer questions about the
organization's own data, draft the monthly PM narrative, and explain timesheet review flags in plain language. This page
says what data it reads, what leaves the system, where it goes, and which controls apply. Owner: `<security lead>`.
Last reviewed: `<date>`.

## Summary

- **Off by default.** Each organization (tenant) is off until an administrator turns it on in **Admin › Assistant**,
  which requires accepting the data-handling notice (`AssistantNotice`, versioned: a new version needs a new acceptance).
- **Reads only what the person asking can see.** The model can't query anything directly: it can only call a fixed list of
  read-only tools, and every tool calls the same permission-checked service the pages use, as the signed-in user.
- **Can't change anything.** No tool writes, sends, approves, emails or deletes. Narratives are saved by the PM, in the app.
- **Everything is logged.** Each prompt, each tool call with the records it read, and each answer go to the assistant
  audit log (append-only), visible to administrators.
- **Provider per tenant.** Anthropic API, or Amazon Bedrock in the customer's AWS account — including **AWS GovCloud (US)**
  for controlled unclassified information (CUI).

## Data flow

```
 Person (browser / desktop) ──HTTPS──► Workbench web host
     question + page path                │
                                         │ 1. Permission decorator: signed in; tenant settings: on? notice accepted?
                                         │    usage within limits?  (else: refused and logged, nothing sent)
                                         │ 2. Audit row: who, when, question, page                       ──► tenant DB
                                         ▼
                              Assistant orchestrator (Application/Assistant)
                                 │   ▲
              system prompt +    │   │  tool calls (name + JSON arguments)
              tool list (fixed)  │   │
              + question         ▼   │
                     Model provider (per tenant):  Anthropic API  |  Amazon Bedrock (commercial or GovCloud)
                                 │   ▲
                                 ▼   │  tool results: escaped JSON inside <workbench-data … untrusted="true">
                              AssistantToolbox (read-only)
                                 │  calls IProjectFinancialsService, IEvmService, IFundingService, … as the user
                                 │  (the same decorators as the pages: ProjectAccess, restricted columns, …)
                                 ▼
                              Tenant database (read)              each call + records read ──► audit log
```

The narrative and flag explanations use the same path without tools: Workbench assembles the facts itself (through the
same permission-checked services) and sends them with the request; the facts' records are logged as one audit entry.

## What leaves the system

Only when the assistant is on for the tenant, and only to the configured provider:

| Sent | When | Notes |
| --- | --- | --- |
| The fixed system prompt and tool descriptions | Every request | No customer data. |
| The question, the page path, today's date, the asker's display name and role names | Questions | No email address, no IP, no tokens. |
| Earlier questions and answers of the same conversation (up to 6) | Follow-up questions | Only the asker's own. |
| Tool results: the figures and names the tools return | When the model calls a tool | Only what the asker could open; restricted sheet columns are removed before the tool sees them. |
| Narrative facts (EVM and financial totals, completed task titles, milestone names, ceiling and CLIN status) | "Draft from the data" | Only for Finance, Admin or the project's PM. |
| A timesheet flag's own facts (rule, dates, hours, codes) | "Explain in plain language" | No employee name or other timesheets. |

Never sent: API keys, AWS credentials, session cookies, cost or pay rates beyond what a tool returns to an authorized
asker (labor cost and rates are Finance-only through the existing decorators), attachments or receipts, other tenants'
data (each tenant has its own database; the orchestrator only opens the current tenant's).

Provider data handling: Anthropic's commercial API and Amazon Bedrock don't use API inputs or outputs to train models.
Bedrock keeps requests in the customer's AWS account and region. Retention and zero-data-retention options are
contractual with the provider; record the agreement in the supplier register (`docs/iso/supplier-security-policy.md`).

## Provider options

| Connection | Where data goes | Use for | Credentials |
| --- | --- | --- | --- |
| Anthropic API | Anthropic, United States (commercial cloud) | Data without CUI or export-control restrictions | `Assistant:Connections:<name>:ApiKey` (**secret**) |
| Amazon Bedrock, commercial region | The customer's AWS account, chosen region | Commercial data under AWS terms | IAM role of the host (instance/task/pod role) or `AwsProfile`; no keys in configuration |
| Amazon Bedrock, **GovCloud (US)** (`us-gov-west-1`, `us-gov-east-1`) | The customer's AWS GovCloud account | **CUI** (FedRAMP High / DoD IL4-5 boundary of the customer's AWS authorization) | IAM role; `AWS_REGION` set to the connection's region |
| Fake (development) | Nowhere: canned answers built from the tool results | Development and automated tests | None. Refused outside Development by the startup check. |

The host operator configures connections (server-wide); each tenant's administrator picks one and a model. The data-
handling text shown to the administrator comes from the connection (`AssistantConnections.Describe`) and says whether it
is GovCloud.

## Controls

| Control | How | Where |
| --- | --- | --- |
| Least privilege (800-171 3.1.1, 3.1.2, 3.1.5; ISO A.8.3) | Tools call the permission-checked services as the user; no SQL, no free-form queries; arguments validated (unknown properties refused, lengths bounded) | `AssistantToolbox`, `Identity/*PermissionDecorators` |
| Restricted information (3.1.3; A.8.11) | Restricted sheet columns are dropped by the sheet decorators before a tool sees the rows; financials and labor cost follow `ProjectAccess` | `RestrictedColumnAccess`, `ProjectAccess` |
| No write path (3.1.2) | The tool list is fixed and read-only (test: `The_tool_set_is_fixed_and_read_only`); unknown tool names are refused and logged | `AssistantToolbox.Specs` |
| Prompt-injection hardening (3.14.2; A.8.28) | Tool results are delimited and JSON-escaped so data can't close the delimiter; the system prompt says data is untrusted; the tool list never changes with data; links come only from the tools (app-relative), never from model text; answers render as plain text | `AssistantPrompts.WrapData`, `AssistantPanel` |
| Off by default, admin enablement with notice (3.1.22, 3.13.1; A.5.10) | `AssistantSettings.IsEnabled` false until an Admin saves with the notice accepted; changes recorded as `settings.assistant` security events | `AssistantAdminService` |
| Audit and accountability (3.3.1, 3.3.2; A.8.15) | `AssistantInteraction` (who, when, page, question, answer, provider, model, tokens, outcome) and `AssistantToolCall` (tool, arguments, outcome, records read); refusals are logged too | Admin › Assistant › Audit log |
| Usage limits (A.8.6) | Organization tokens/month, per-person tokens/month, per-person questions/day; metered from the audit log | `AssistantGate` |
| Secrets (3.13.10; A.8.24) | API keys only from secret stores; Bedrock uses IAM roles; keys are never logged or shown (errors are translated to safe messages) | `AnthropicModelClient.Translate`, `ProductionConfigurationCheck` |
| Configuration hardening (3.4.2; A.8.9) | Startup refuses the fake provider outside Development and any incomplete connection | `ProductionConfigurationCheck.AssistantProblems` |
| Transport (3.13.8; A.8.24) | HTTPS to the provider (SDK); `BaseUrl` must be https | `AssistantConnections.Validate` |
| Human in the loop | Narratives are drafts until the PM saves them; VAR fields change only when the PM ticks "copy into the VAR" and saves (through `IEvmService`, as the PM); timesheet flags never block submitting or approving | `ProjectNarrativeTab`, `TimesheetFlagService` |
| Monitoring | Metrics `workbench.assistant.tokens` and `workbench.assistant.requests` (by provider/kind/outcome, no people or amounts) | `AssistantRunner` |

## Timesheet flags

Detection is deterministic: fixed rules (weekend and holiday hours, late entry against `Timekeeping:LateEntryBusinessDays`,
revisions after submission, charges to closed or ceiling-limited codes) plus a statistical baseline of the person's own
last 12 submitted timesheets (mean and standard deviation of project shares, indirect share and whole-hour precision) and
their resourcing allocations. The model is used only to word an explanation of a flag that was already found, from that
flag's facts. Flags are visible to the person's supervisor and to Timekeepers, Finance and Admins — never to the
employee themself, and never used to block anything.

## Residual risks

- A model can misread or mis-summarize correct data. Answers cite the pages their figures came from, and narratives are
  drafts the PM edits; train users to check the sources before relying on an answer.
- Text in records (task names, comments, vendor names) can try to steer the model. Hardening limits the effect to the
  wording of the answer for that user: the model can't reach data the user couldn't, can't change data, and can't send
  anything anywhere. Review the audit log when an answer looks wrong.
- Data sent to the provider is outside Workbench's control once sent; choose the connection by data classification
  (GovCloud for CUI), and keep the provider in the supplier register.

## Operations

- Configuration keys: `docs/operations/configuration.md` (section "AI assistant").
- Turning it off: Admin › Assistant › untick "Turn on", Save. Effective immediately for every user.
- Incident: if a provider key is exposed, rotate it in the secret store; the audit log shows which requests used the
  connection (Provider, Model, time).
