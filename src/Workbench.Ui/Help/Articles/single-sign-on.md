---
title: Single sign-on
summary: Let people sign in with their company account (Microsoft Entra ID, Okta or any OpenID Connect provider).
category: Administration
audience: admin
keywords: sso single sign-on oidc openid entra azure ad okta google scim provisioning login
order: 52
---
Workbench doesn't keep passwords. People sign in through your company's identity provider using OpenID Connect, so your password rules and multi-factor authentication apply.

## What your IT team needs to do

1. In your identity provider (for example Microsoft Entra ID or Okta), register Workbench as a web application.
2. Set the redirect address to your Workbench address followed by `/signin-oidc`.
3. Give the Workbench server the provider's address (authority), the client id and the client secret. Your hosting administrator puts these in the server's configuration; secrets go in environment variables or a vault, never in a file.
4. Restart Workbench and sign in.

If you use Workbench as a hosted service, send these details to your Workbench support contact instead.

## Who can sign in

By default only invited people can sign in (see [Users and roles](help:users-and-roles)). Your identity provider can also create and deactivate users automatically through SCIM provisioning; ask your IT team to point it at your Workbench address followed by `/scim/v2`.

## Sessions

People are signed out after 30 minutes without activity and after 12 hours in any case. Deactivating someone signs them out everywhere at once.
