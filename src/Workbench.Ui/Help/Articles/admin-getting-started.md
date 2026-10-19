---
title: "Getting started: set up a new organization"
summary: The order to set things up in, for the first administrator of a new organization.
category: Administration
audience: admin
keywords: getting started setup new organization onboarding admin first checklist
order: 50
---
Welcome. This guide walks you through setting up Workbench for your company, in the order that avoids rework. It goes hand in hand with the **Setup** checklist (**Admin › Setup › Getting started**), which ticks each step off as you finish it.

Plan for about a day of work spread over a week, with your controller or Finance lead for steps 4–7.

## 1. Sign-in and your administrators (30 minutes)

- Connect your company sign-in so people use their usual work account: see [Single sign-on](help:single-sign-on).
- Make sure at least two people are administrators, so you never lock yourself out.

## 2. Your organization (15 minutes)

In **Admin › Organization**, set the company name and the timesheet period (weekly, every two weeks, twice a month or monthly). Then set up the **Fiscal calendar** (most government contractors run October to September).

## 3. Bring your data over, or type it in (1–3 hours)

If you're moving from Costpoint, Unanet or Smartsheet, **Import** is the fastest route: org units, people, customers, contracts and CLINs, projects with their WBS, rates and budgets. Every import can be rolled back. See [Import data](help:importing-data).

Otherwise create them in **Setup** in this order: **Org units → Labor categories → People → Customers → Contracts → Projects and WBS**. Each depends on the one before.

## 4. Money setup (with Finance, 2–3 hours)

- **Chart of accounts**: install the default chart and adjust it.
- **Cost rates** for each person, and **bill rates** on each contract.
- **Indirect rates**: your provisional [fringe](term:fringe), [overhead](term:overhead) and [G&A](term:ga) rates.
- **Bank accounts** in Cash, and your **payroll provider** in Payroll.
- **Billing details** (company name, remit-to address, CAGE code) for invoices.

## 5. People and access (1 hour)

Invite users and give them roles: see [Users and roles](help:users-and-roles). Set each person's supervisor in **People**: supervisors approve timesheets.

## 6. Approvals (30 minutes)

The built-in approval rules work out of the box (supervisor approves time, project manager approves project costs). Change them only if you need to: see [Approval workflows](help:approval-workflows).

## 7. Try it, then go live

1. Have two or three people enter a week of time and an expense report.
2. Approve them, post them and run a test invoice.
3. When you're happy, announce the start date and point people to the [help center](help). Workbench shows each person a short welcome tour the first time they sign in.

> [!TIP]
> Running a pilot? The training material in the `docs/training` folder of the Workbench repository has a one-hour agenda and a quick-reference card for each role.
