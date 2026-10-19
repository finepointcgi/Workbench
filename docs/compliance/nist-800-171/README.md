# NIST SP 800-171 / CMMC Level 2 kit

For defense contractors who handle Controlled Unclassified Information (CUI) under DFARS 252.204-7012 and need CMMC
Level 2.

| Document | Use it to |
| --- | --- |
| [control-mapping.md](control-mapping.md) | See, for all 110 requirements, what Workbench does and what is the customer's or the hosting provider's |
| [system-security-plan.md](system-security-plan.md) | Write your SSP (3.12.4) and POA&M (3.12.2) |
| [gaps.md](gaps.md) | Know what isn't covered, especially for the hosted SaaS (FedRAMP Moderate equivalency) |

Start with [gaps.md](gaps.md): **don't put CUI in the hosted Workbench service** until it meets FedRAMP Moderate (or
DoD's equivalency standard). Self-hosting Workbench inside an environment that already meets 800-171 (for example Azure
Government or AWS GovCloud with your own controls, or an on-premises enclave) is the supported path today.

Configure Workbench for CUI:

1. Single sign-on through your IdP with **MFA required for everyone** (**Admin › Users and security › Sign-in**;
   [../../security/sso-entra.md](../../security/sso-entra.md), [../../security/sso-okta.md](../../security/sso-okta.md)).
2. **Only allow sign-in through this provider**; invite-only or allowed-domain provisioning; SCIM for leavers.
3. Re-authentication window ≤ 15 minutes; idle timeout ≤ 15–30 minutes (`Authentication:Session:*`).
4. Security event retention ≥ 3 years (`Retention:SecurityEventDays` or the Retention tab), forwarded to your SIEM.
5. Production configuration per [../../security/configuration.md](../../security/configuration.md), TLS on a
   FIPS-validated terminator, FIPS-enabled host OS.

The ISO 27001 policies in [../../iso/](../../iso/README.md) double as the policies 800-171 expects (access control,
incident response, configuration, logging, media and asset handling). Evidence sources are in
[../../iso/evidence-catalog.md](../../iso/evidence-catalog.md).

*This kit is guidance, not legal advice or an assessment. CMMC requirements (32 CFR part 170, 48 CFR part 204) change;
confirm the current rule and your contract clauses.*
