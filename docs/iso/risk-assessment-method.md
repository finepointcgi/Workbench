# Risk assessment and treatment method

| | |
| --- | --- |
| Document owner | `<ISMS manager>` |
| Approved by | `<top management>` |
| Version / date | `<1.0>` / `<date>` |

*ISO 27001 clauses 6.1.2, 6.1.3, 8.2 and 8.3. The method must produce "consistent, valid and comparable results" when
repeated. Keep it simple enough that the same people get the same scores next year.*

## 1. When we assess risk

- Yearly, for the whole scope.
- When there is a significant change: new hosting, new major feature or interface (for example SCIM, a new
  integration), a new customer requirement, a major incident, or a merger.
- Results go in `risk-register.md` and feed the Statement of Applicability.

## 2. Identifying risks

We identify risks per asset or process (see `asset-inventory.md`) and per threat scenario, using:

- the threat model (`../security/threat-model.md`);
- incidents and near misses, audit findings, vulnerability reports;
- interested parties' requirements (customers, DCAA, regulations);
- Annex A as a checklist of possible controls (not as a risk list).

Each risk has an **owner**: the person accountable for deciding its treatment and accepting its residual risk.

## 3. Scales

**Likelihood (L)**

| Score | Label | Guide |
| --- | --- | --- |
| 1 | Rare | Not expected in 5 years |
| 2 | Unlikely | Could happen once in 2–5 years |
| 3 | Possible | Could happen about once a year |
| 4 | Likely | Several times a year |
| 5 | Almost certain | Monthly or more |

**Impact (I)**: score each dimension and take the highest.

| Score | Financial | Contract / regulatory | Operations | Data |
| --- | --- | --- | --- | --- |
| 1 Negligible | < `$10k` | None | < 1 hour disruption | No sensitive data |
| 2 Minor | `$10k–50k` | Customer query | < 1 day | Internal data, limited |
| 3 Moderate | `$50k–250k` | Formal finding, cure notice | 1–3 days; missed payroll/billing cycle | PII or financial data of a few people |
| 4 Major | `$250k–1M` | DCAA disallowance, DFARS report, lost contract | > 3 days | CUI or broad PII exposure |
| 5 Severe | > `$1M` | Suspension/debarment, False Claims exposure | Business-threatening | Large CUI breach |

**Risk score = L × I** (1–25).

| Score | Level | Action |
| --- | --- | --- |
| 1–4 | Low | Accept; review yearly |
| 5–9 | Medium | Treat if cost-effective, or owner accepts |
| 10–16 | High | Treat; acceptance only by top management with a dated plan |
| 17–25 | Critical | Treat immediately; not acceptable |

## 4. Acceptance criteria

Residual risk is acceptable when it is **Low**, or **Medium** with the risk owner's documented acceptance. High
residual risk needs top management's acceptance and a review date no more than 6 months out. Critical residual risk
is never accepted.

## 5. Treatment

For each risk above the acceptance criteria choose one or more:

- **Modify**: apply controls (Annex A or others). Record which controls in the register and the SoA.
- **Avoid**: stop the activity.
- **Share**: transfer part of it (insurance, a supplier with the right controls; the risk stays ours to monitor).
- **Retain**: accept, within the criteria above.

The **risk treatment plan** is the set of register rows with treatment actions, owners and due dates. Risk owners
approve the plan and the residual risk (6.1.3 f).

## 6. Recording and reviewing

- The register is reviewed quarterly by the ISMS manager and at each management review.
- Each entry keeps its history (date, previous score) so trends are visible.
- Treatment actions are tracked to completion like corrective actions.
