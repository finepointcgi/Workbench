# Software license and subscription agreement (draft)

!!! danger "DRAFT: have a lawyer review before use"
    This is a starting point written by a software team, **not legal advice** and not a contract anyone should sign. Text in
    `[square brackets]` is a placeholder the owner must decide. Have a lawyer licensed where you will operate review every
    clause, especially limitation of liability, governing law, data protection, export control and any sale to a government
    customer (which may need different terms entirely).

This agreement is between **`[Vendor legal name]`** ("**Vendor**") and the organization that accepts it or signs an order form
("**Customer**"). It covers Workbench in two forms: **Self-Hosted** (Customer runs it) and **Hosted** (Vendor runs it for
Customer). The order form says which.

## 1. Definitions

- **Software**: Workbench, the code and documentation, in the version Customer runs.
- **Source License**: the Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2) in `LICENSE.md` of the
  source repository.
- **License Key**: a signed key, or a hosted plan setting, that unlocks the **Modules** and **Seats** on the order form.
- **Module**: a licensed part of the Software (for example Finance, Capture, Workforce, Controls, Government contracting
  pack, Enterprise). Core is free.
- **Seat**: an active person who can sign in. **Full** and **Light** Seats are defined in the order form.
- **Customer Data**: everything Customer or its users put into the Software or the Hosted service.
- **Order Form**: the document naming the Modules, Seats, Term, Fees and any trial.

## 2. License and how it relates to the Source License

2.1 **Source License.** The Software's source code is available to everyone under the Source License. This agreement does not
reduce any right the Source License gives. In short, the Source License allows use, copying, modification and self-hosting for
any purpose except a **Competing Use** (offering a product or service that competes with the Software), and each version
becomes Apache License 2.0 after two years. If this agreement and the Source License conflict about what Customer may do with
the **source code**, the Source License controls. `[Lawyer: confirm this precedence and check it against the actual LICENSE.md text.]`

2.2 **Commercial rights.** In return for the Fees, Vendor grants Customer, for the Term, the right to **unlock and use the
Modules and Seats** named in the Order Form, for Customer's and its affiliates' internal business purposes (`[affiliates
included? Y/N]`), and to receive the support and Hosted service described here.

2.3 **What the key is.** A License Key is a technical control, not a transfer of ownership. Customer must not copy,
alter, share or circumvent a License Key, or remove or bypass the Module and Seat checks, to use more than it paid for. The
Source License does not give Customer the right to do that, and doing it is a breach of this agreement. `[Lawyer: confirm this is
enforceable alongside the Source License, since the source is open to read and change.]`

2.4 **Restrictions.** No Competing Use; no sublicensing or resale of Modules or Seats unless the Order Form allows it; no
use of the Software to build a product that competes with it.

2.5 **Reserved rights.** Vendor keeps all rights not granted. Trademarks (Workbench, the logo) are not licensed by this
agreement or the Source License.

## 3. Seats, Modules and use

3.1 Customer may have no more active Seats than it has paid for, by type. A pending invitation holds a Seat until accepted,
revoked or expired; deactivating a person frees the Seat.
3.2 API keys and integrations are not people and take no Seat.
3.3 Customer may add Seats and Modules at any time on the Order Form's rates; additions run to the end of the current Term
and are billed pro rata `[or: from the next billing date]`.
3.4 Customer may not reduce Seats or Modules during a Term `[or: may reduce at renewal only]`.
3.5 Where the Software cannot enforce a limit (for example open sign-up in a development setup), Customer is still bound by it
and must report and pay for any excess on request.
3.6 Vendor may ask for a usage report once a year, and Customer will give a truthful one `[audit right: scope, notice, cost]`.

## 4. Trials

4.1 A trial gives access to the Modules and Seats on the Order Form for `[30]` days, free, **as is**, with no support
commitment and no warranty beyond Section 12.1.
4.2 Customer Data entered during a trial belongs to Customer (Section 9) and is kept for `[30]` days after the trial ends
unless Customer converts, in which case it carries over untouched.
4.3 At the end of a trial the paid features stop working for changes after the grace period in 5.5; Core and read access to
Customer's records continue. Vendor will not delete Customer Data because a trial ended, except as Section 9.5 provides.

## 5. Fees, renewal and termination

5.1 **Fees.** Customer pays the Fees on the Order Form: `[per Seat per month or year, by type and Module]`. Fees exclude taxes;
Customer pays sales, use and VAT where they apply `[withholding clause]`.
5.2 **Invoicing.** `[annually in advance]`, net `[30]` days. Late amounts bear interest of `[?]%` per month or the legal
maximum, if less. Vendor may suspend paid features after `[15]` days' written notice of non-payment.
5.3 **Price changes.** Fees are fixed for the Term. At renewal Vendor may raise them by up to `[?]%` on `[60]` days' notice.
5.4 **Term and renewal.** The Term starts on the Order Form date and runs `[12]` months. It renews for equal periods unless
either party gives `[60]` days' notice before the end. `[auto-renew: check state law on consumer and business notice rules.]`
5.5 **Lapse.** If a License Key expires or fees go unpaid, Modules keep working for a grace period of `[14]` days with a warning,
then the Software returns to Core. **Nothing is deleted.** Reads of all Customer Data stay available, and no one is locked out
of their own records.
5.6 **Termination for cause.** Either party may terminate on `[30]` days' written notice of a material breach it has not
cured in that time. Vendor may terminate immediately for breach of 2.3, 2.4 or 8.
5.7 **Refunds.** `[None for the current Term, except: pro-rata refund if Customer terminates for Vendor's uncured material
breach, or if Vendor terminates other than for cause.]` Decide the policy; this is the usual one.
5.8 **After termination.** Sections 7, 9, 10, 11, 12.2, 13 and 14 survive. The Source License continues to apply to the
source code independently of this agreement.

## 6. Support

6.1 Support is as described in the Order Form and in the [service levels](service-levels-draft.md) `[attached as Schedule A]`:
hours `[?]`, channels `[?]`, response targets by severity.
6.2 Support covers the current release and the previous `[one]`. Self-Hosted Customer is responsible for its own servers,
backups, TLS, identity provider, upgrades and the security of its network.
6.3 Updates and patches are included for the Term. Vendor tells Customer about security fixes under its `SECURITY.md` process.
6.4 Professional services (import, configuration, training) are separate `[statement of work]`.

## 7. Hosted service

7.1 Vendor runs the Software for Customer in `[region(s)]`, with one database per customer, backed up as described in the
[data processing addendum](data-processing-addendum-draft.md).
7.2 Vendor meets the [service levels](service-levels-draft.md) and gives the credits stated there as Customer's **sole**
remedy for downtime, unless Section 5.6 applies.
7.3 Vendor may change infrastructure or sub-processors on notice under the data processing addendum.
7.4 Customer is responsible for its users, their credentials and what they put into the service.

## 8. Acceptable use

Customer will not, and will not let others: (a) break the law using the Software; (b) upload malware, or probe, scan or
test the Hosted service for vulnerabilities without written permission (report issues under `SECURITY.md` instead);
(c) overload the Hosted service or evade limits; (d) store data it has no right to hold; (e) use the Software for
Competing Use; (f) use the Hosted service to hold classified information or data subject to controls the service is not
built for (**`[decide: CUI, ITAR/export-controlled data, HIPAA, payment card data]`**: say plainly what the Hosted service
can and cannot host).

## 9. Customer Data

9.1 **Ownership.** Customer owns Customer Data. Vendor gets only the right to host, process and back it up to provide the
service.
9.2 **Use limits.** Vendor will not sell Customer Data, use it to train AI models, or look at it except to give support
Customer asked for, keep the service secure, or comply with law. Aggregated, de-identified service metrics (not content) may be
used to run and improve the service. `[Lawyer: confirm the metrics carve-out matches the privacy notice.]`
9.3 **Export.** Customer can export its data at any time with the Software's own exports (CSV, reports, API). Importers and the
audit log are free in every plan for exactly this reason.
9.4 **On termination (Hosted).** For `[60]` days after the Term ends Customer may export. Vendor will on request provide a full
database export in a documented format `[PostgreSQL dump]` at no charge, once.
9.5 **Deletion.** After that period Vendor deletes Customer Data from live systems within `[30]` days and from backups within
`[90]` days, and confirms in writing on request.
9.6 **Self-Hosted.** Customer Data never leaves Customer's systems; the offline key flow sends nothing. If Customer enables the
connected license flow, only the license identifier, edition, seat count and version are sent `[confirm against the shipped
implementation]`.
9.7 Personal data is handled under the [data processing addendum](data-processing-addendum-draft.md).

## 10. Confidentiality

Each party will keep the other's non-public information (pricing, security details, Customer Data, roadmaps) confidential,
use it only for this agreement, share it only with people who need it and are bound to the same duty, and protect it as it
would its own, but with at least reasonable care. Exceptions: public information, independently developed, rightfully
received from someone else, or legally required to be disclosed (with notice where allowed). Duration: `[5]` years after
termination; trade secrets and Customer Data for as long as they stay such.
This does not limit what the Source License allows with the open source.

## 11. Intellectual property and feedback

Each party keeps its own IP. Customer's suggestions may be used by Vendor without obligation. Customer's own customizations
and configurations (custom fields, layouts, automations, reports) belong to Customer. Modifications Customer makes to the
source are governed by the Source License.

## 12. Warranties and disclaimers

12.1 **Mutual.** Each party has authority to enter this agreement.
12.2 **Vendor warranty.** For the Term the Software will materially perform as its documentation describes, and Vendor will
provide the Hosted service with reasonable skill and care. Remedy: Vendor will fix the problem or, if it cannot within
`[30]` days, either party may end the affected Order Form and Vendor refunds pre-paid fees for the unused period.
12.3 **Disclaimer.** Otherwise the Software and trials are provided **as is**, and Vendor disclaims implied warranties
including merchantability, fitness for a particular purpose and non-infringement, to the fullest extent the law allows.
12.4 **No compliance guarantee.** Workbench helps Customer prepare for DCAA, CAS, CMMC, SOC 2, ISO 27001 and similar
requirements. **It does not certify Customer, and Customer remains responsible for its own compliance, filings, rates,
assessments and the accuracy of its records.** Output such as an SPRS score, an incurred cost schedule or a readiness
checklist is a working aid, to be reviewed by Customer's own qualified people.
12.5 Customer is responsible for the correctness of its own books, payroll and tax. The Software is not accounting, tax or legal
advice.

## 13. Limitation of liability

13.1 Neither party is liable for lost profit, lost revenue, lost data (where Customer failed to keep backups or exports), or
indirect, special or consequential damages, however caused.
13.2 Each party's total liability under this agreement is limited to the fees Customer paid in the `[12]` months before the
claim `[or: a stated multiple]`.
13.3 These limits do not apply to: Customer's payment obligations; breach of Section 2.3, 2.4 or 8; a party's confidentiality
breach `[consider a higher cap]`; indemnity (13.4); fraud, willful misconduct, or anything law says cannot be limited.
13.4 **Indemnity.** `[Vendor defends Customer against a claim that the Software infringes IP; Customer defends Vendor
against claims about Customer Data and misuse. Lawyer to write this section; the right wording depends on the cap, insurance
and the Source License.]`
13.5 `[Data breach super-cap: decide.]`

## 14. General

14.1 **Governing law and venue:** `[State/country]` law, courts of `[venue]`; `[arbitration? Y/N]`. **Placeholder: the owner
must choose.**
14.2 **Notices** in writing to the addresses on the Order Form.
14.3 **Assignment:** neither party may assign without consent, except to a successor to substantially all its business.
14.4 **Force majeure** excuses delay, not payment.
14.5 **Export control and sanctions.** Customer will comply with applicable export law and will not use the Software where
prohibited. `[Lawyer: add if Vendor hosts or ships to customers handling controlled data.]`
14.6 **Government customers.** If Customer is a US Government agency, `[commercial item terms and FAR/DFARS flow-downs: a
lawyer must write this. Do not sell directly to a government agency on this draft.]`
14.7 **Publicity:** `[Vendor may name Customer as a customer: Y/N]`.
14.8 **Entire agreement and order of precedence:** Order Form, then this agreement, then the data processing addendum on
personal data matters, then the service levels on uptime and support. Amendments in writing.
14.9 **Severability; no waiver; counterparts; electronic signature.**

!!! info "Choices this draft makes that the owner should confirm"
    - Customer Data is never used to train models, and Vendor does not look at it except for support.
    - Lapse falls back to Core with nothing deleted. This matches the shipped design and is a selling point.
    - Governing law, venue, liability cap, refund, auto-renew, audit right, affiliate use and publicity are all open.
    - Hosting regions and what the Hosted service may hold (CUI, export-controlled data) are open; see the
      [SOC 2 readiness](soc2-readiness.md) page for why CUI is a separate decision.
