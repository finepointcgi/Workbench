# Data Protection keys

ASP.NET Core Data Protection keys encrypt the auth cookie, antiforgery and Blazor tokens, **and the secrets
Workbench stores encrypted: vendor TINs, bank account and routing numbers, and (MultiTenant) each tenant's connection
string**. The keys live in the database (`DataProtectionKeys` in the tenant database, or in the catalog in
MultiTenant mode, where tenants are isolated by purpose; see `docs/security/configuration.md`). Each key's secret is
encrypted at rest with a **key-encryption key** you hold elsewhere, so a copy of the database or of a backup is not
enough to read the protected data (ISO 27001 A.8.24).

Production refuses to start without key encryption. Before this was enforced, the log said *"No XML encryptor
configured. Key may be persisted to storage in unencrypted form"*; existing unencrypted keys are encrypted by
`--rewrap-data-protection-keys` (below).

> **Losing the key-encryption key loses data.** Without it the key ring can't be read: everyone signs in again
> (harmless), but encrypted TINs and bank accounts are gone for good. Escrow the certificate (and its password) or
> protect the vault key against deletion (soft delete + purge protection, or a KMS deletion window) before go-live.

## Choosing a key-encryption method

| `DataProtection:KeyEncryption` | Use when | Needs |
| --- | --- | --- |
| `Certificate` | Self-hosted, any cloud, air-gapped | A PFX (RSA 2048+) with its private key, mounted as a secret file, or a certificate in the OS `My` store (`CertificateThumbprint`) |
| `AzureKeyVault` | Running in Azure / Azure Government | An RSA key in Key Vault (or Managed HSM); the app's identity may *get*, *wrapKey* and *unwrapKey* |
| `AwsKms` | Running in AWS / GovCloud | A symmetric KMS key; the app's role may `kms:Encrypt` and `kms:Decrypt` with the Workbench encryption context |

Keys stay in the database in every case; only their secrets are wrapped.

### Certificate

```bash
openssl req -x509 -newkey rsa:3072 -sha256 -days 1825 -nodes -subj "/CN=Workbench Data Protection" \
  -addext "keyUsage=keyEncipherment,dataEncipherment" -keyout dp.key -out dp.crt
openssl pkcs12 -export -inkey dp.key -in dp.crt -out dataprotection.pfx -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256
shred -u dp.key
```

`scripts/prod/init-secrets.sh` does the same. Configure:

```
DataProtection__KeyEncryption=Certificate
DataProtection__CertificatePath=/run/secrets/dataprotection.pfx
DataProtection__CertificatePassword=<from the vault, or the secret file DataProtection__CertificatePassword>
```

The certificate's expiry date doesn't stop it decrypting; rotate it on your schedule (yearly or every five years)
anyway, and whenever it may have been exposed.

### Azure Key Vault

Create an RSA key (`az keyvault key create --vault-name <vault> -n workbench-dp --kty RSA --size 3072`), enable soft
delete and purge protection on the vault, and grant the app's managed identity the **Key Vault Crypto Service
Encryption User** role on the key (get, wrapKey, unwrapKey). Configure:

```
DataProtection__KeyEncryption=AzureKeyVault
DataProtection__AzureKeyVault__KeyId=https://<vault>.vault.azure.net/keys/workbench-dp
DataProtection__AzureKeyVault__ClientId=<user-assigned identity client id, optional>
```

Without a version in `KeyId`, new keys are wrapped with the current key version; each stored key records the exact
version that wrapped it, so older versions must stay enabled.

### AWS KMS

Create a symmetric key with automatic rotation on, and give the task/instance role:

```json
{
  "Effect": "Allow",
  "Action": ["kms:Encrypt", "kms:Decrypt"],
  "Resource": "arn:aws-us-gov:kms:us-gov-west-1:111122223333:key/<key-id>",
  "Condition": { "StringEquals": { "kms:EncryptionContext:workbench:purpose": "data-protection-key-ring" } }
}
```

```
DataProtection__KeyEncryption=AwsKms
DataProtection__AwsKms__KeyId=arn:aws-us-gov:kms:us-gov-west-1:111122223333:alias/workbench-dp
DataProtection__AwsKms__Region=us-gov-west-1
```

Every key secret is encrypted with `kms:Encrypt` under that encryption context, so CloudTrail shows each use and the
ciphertext can't be decrypted for any other purpose. KMS's automatic yearly rotation needs nothing from you: old
key material stays available for decryption.

## Rotation

Two different things rotate:

- **Data Protection keys** rotate by themselves: a new key every `DataProtection:KeyLifetimeDays` (90). Old keys are
  kept (never delete rows from `DataProtectionKeys`): they are still needed to read cookies issued earlier and every
  TIN or bank account encrypted with them.
- **The key-encryption key** (certificate, vault key, KMS key) is rotated by you, with this procedure. It is covered
  by the automated test `Rotating_the_certificate_keeps_protected_data_readable_and_rewrap_retires_the_old_one`
  (`tests/Workbench.Tests/Operations/DataProtectionKeyEncryptionTests.cs`), which also proves the old certificate is
  needed until the rewrap has run.

### Rotating the certificate

1. **Create** the new certificate (above) and store it and its password in the vault. Keep the old one.
2. **Configure both**, new as current and old as previous, and roll out (all web hosts restart):

   ```
   DataProtection__CertificatePath=/run/secrets/dataprotection-2027.pfx
   DataProtection__CertificatePassword=<new password>
   DataProtection__PreviousCertificates__0__Path=/run/secrets/dataprotection-2026.pfx
   DataProtection__PreviousCertificates__0__Password=<old password>
   ```

   New keys are now encrypted with the new certificate; existing ones are still readable with the old.
3. **Re-encrypt the stored keys** with the new certificate, once, using the same configuration (like a migration job):

   ```bash
   docker compose -f docker-compose.prod.yml run --rm migrate --rewrap-data-protection-keys
   # Kubernetes: a Job with args ["--rewrap-data-protection-keys"]
   ```

   It decrypts every key with whatever encrypted it, encrypts it with the current certificate, checks each result
   decrypts back to the original, and writes nothing unless every key succeeded. It logs
   `Re-encrypted N Data Protection keys`. MultiTenant: it rewrites the one key ring in the catalog.
4. **Restart** the web hosts (they cache the key ring for up to a day).
5. **Verify on staging first**, then production: remove `PreviousCertificates`, restart, sign in, and open a vendor
   with a TIN on file (*Reveal* works). Then remove `PreviousCertificates` in production.
6. **Keep the old certificate escrowed** as long as backups taken before step 3 exist (35 days with the default
   backup retention, longer for monthly/yearly copies): those backups hold keys encrypted with it.
7. Record the rotation (date, who, old/new thumbprints) in the change log.

### Switching method (e.g. certificate → Key Vault), or encrypting an unencrypted key ring

Same procedure: configure the new method (keep the old certificate under `PreviousCertificates` while switching away
from a certificate), run `--rewrap-data-protection-keys`, restart, verify, remove the old configuration. The test
`Rewrap_encrypts_an_existing_unencrypted_key_ring_in_the_database` covers encrypting a ring that was stored in plain.

### Rotating a vault or KMS key

- **Azure Key Vault**: create a new key version (`az keyvault key rotate`, or a rotation policy). New Data Protection
  keys use it from the next restart; existing ones keep naming the version that wrapped them. To re-wrap everything
  with the new version, run `--rewrap-data-protection-keys`. Never disable old versions before that.
- **AWS KMS**: automatic rotation keeps old material available; nothing to do. To move to a different KMS key, change
  `KeyId`, run `--rewrap-data-protection-keys` (the old key must still allow `kms:Decrypt`), then schedule the old
  key's deletion with the maximum waiting period once old backups have expired.

## If the key-encryption key is compromised

1. Treat it as a security incident ([Incident response: technical steps](incident-response.md)).
2. Rotate it at once with the procedure above, including the rewrap, then revoke/delete the compromised certificate
   or disable the vault key version.
3. An attacker who had both the key-encryption key **and** a copy of the database could read the protected values:
   assess which TINs and bank accounts were exposed (vendor records), and notify as the incident plan requires.
   Changing bank details with vendors is the real remediation; re-encrypting with new Data Protection keys does not
   undo an exposure.

## Recovery

- **Restore** brings the key ring back with the database (the catalog in MultiTenant). The web hosts need the same
  key-encryption configuration as when the backup was taken (the certificate that wrapped those keys).
- **A new environment from backups** needs: the database backup, the key-encryption certificate (or access to the
  vault/KMS key) and the same `DataProtection:ApplicationName`.
