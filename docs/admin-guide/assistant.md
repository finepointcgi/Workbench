# AI assistant

<p class="roles">Administrators turn it on · Everyone it's enabled for can use it</p>

An AI assistant that answers questions from your Workbench data, drafts project manager narratives and explains
timesheet flags. It reads only what each person can already see, through read-only tools, and can't change anything.
**It's off until you turn it on.**

<figure markdown="span">
  ![Assistant settings: the data-handling notice, Turn on the assistant for this organization, provider connection and model, organization tokens per month, tokens per person per month, questions per person per day, and options for supervisors and Finance](../assets/screenshots/admin-assistant.png){ .shot }
</figure>

## Before you turn it on

Read the **data-handling notice**. When the assistant is on, the questions people type and the records the assistant
reads to answer them (project financials, budgets, earned value, ledger lines, timesheets, approvals, ceilings, funding,
resourcing and sheet rows) are sent to the AI provider you choose, which processes them to produce the answer. Check
this against your contracts, especially any that involve CUI.

## Settings

1. Tick **Turn on the assistant for this organization**.
2. Pick the **provider connection** and **model**. Connections (for example Anthropic, or Amazon Bedrock in AWS GovCloud)
   are configured on the server; keys stay there.
3. Set limits: **organization tokens per month**, **tokens per person per month**, and **questions per person per day**.
   Empty means no limit.
4. Choose the extras, such as **Let supervisors and Finance ask for plain-language explanations of timesheet flags**.
5. Tick **I have read the data-handling notice and accept it for our organization**, and **Save**.

## Usage and audit log

- **Usage**: tokens and questions by month and by person, against the limits.
- **Audit log**: every question, who asked it, which records the assistant read, and the answer.

Details of what it can read and how it's secured: [AI assistant security](../security/ai-assistant.md).
