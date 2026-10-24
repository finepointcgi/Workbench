# Object storage for files

Attachments, receipts, support screenshots, import files and connector exports are kept in a blob store chosen by
`Storage:Provider`:

| Provider | When | Notes |
| --- | --- | --- |
| `Local` (default) | Development, one server | A folder (`Storage:Root`, `/data/blobs` in the image). Files are lost with the container unless the folder is a volume, and other servers can't see them. |
| `Gcs` | Google Cloud (Cloud Run, GKE) | A Google Cloud Storage bucket. |
| `S3` | AWS, or self-hosted with MinIO (or another S3-compatible store) | An S3 bucket. |

**Run more than one web server only with `Gcs` or `S3`.** With `Local`, a file uploaded on one server is missing on
the others.

What every bucket provider does:

- **Private objects only.** Workbench never sets ACLs or makes public links. Every download goes through the app,
  which checks the person's permission first, then streams the file. Keep the bucket's public access blocked (below).
- **Encrypted at rest.** GCS encrypts every object (Google-managed keys, or your Cloud KMS key with
  `Storage:Gcs:KmsKeyName`). S3 uploads ask for SSE-S3 (`AES256`) or SSE-KMS (`aws:kms`); Production refuses
  `Storage:S3:ServerSideEncryption=None`.
- **Integrity.** S3 uploads carry a Content-MD5 that S3 checks; GCS downloads are checked against the object's hash.
- **No keys in config where the platform has an identity.** GCS uses Application Default Credentials (on Cloud Run,
  the service's own service account). S3 uses the AWS default chain (instance profile, ECS task role, EKS IRSA). Static
  keys are a fallback for hosts without one (MinIO, a VM outside the cloud).
- **Tenants.** In MultiTenant mode each tenant's files are under `tenants/{slug}/`, as in the local folder.

## Settings

| Key | Default | Notes |
| --- | --- | --- |
| `Storage:Provider` | `Local` | `Local`, `Gcs` or `S3`. |
| `Storage:Root` | `App_Data/blobs` (`/data/blobs` in the image) | `Local`: the folder. Also where `--copy-blobs` reads from. |
| `Storage:Bucket` | — | `Gcs`/`S3`: the bucket name. Required. |
| `Storage:Prefix` | — | Optional key prefix inside the bucket, ending in `/` (e.g. `workbench/`), to share a bucket. |
| `Storage:Gcs:CredentialsFile` | — | **Secret** (a key file path). Leave empty to use the service account the platform provides. |
| `Storage:Gcs:KmsKeyName` | — | Optional customer-managed key: `projects/P/locations/L/keyRings/R/cryptoKeys/K`. The service account needs `roles/cloudkms.cryptoKeyEncrypterDecrypter` on it; Cloud Storage's service agent does too. |
| `Storage:S3:Region` | SDK default (`AWS_REGION`) | e.g. `us-gov-west-1`. |
| `Storage:S3:ServiceUrl` | — | An S3-compatible endpoint (`https://minio.internal:9000`). Empty for Amazon S3. |
| `Storage:S3:ForcePathStyle` | `true` with a `ServiceUrl`, else `false` | MinIO needs path-style. |
| `Storage:S3:AccessKeyId` / `:SecretAccessKey` | — | **Secret.** Only without an IAM role. Set both or neither. |
| `Storage:S3:ServerSideEncryption` | `AES256` | `AES256`, `aws:kms` or `None` (refused in Production). |
| `Storage:S3:KmsKeyId` | — | SSE-KMS key ARN; the AWS managed key when empty. The role needs `kms:GenerateDataKey` and `kms:Decrypt` on it. |

## Google Cloud Storage

1. Create a bucket in the service's region, with uniform bucket-level access and public access prevention:

   ```bash
   gcloud storage buckets create gs://BUCKET --project=PROJECT --location=REGION --uniform-bucket-level-access --public-access-prevention
   ```

   Optional: object versioning (`gcloud storage buckets update gs://BUCKET --versioning`) as a second line of defense
   against deletes; the default 7-day soft delete already covers mistakes.
2. Give the web host's service account `roles/storage.objectUser` on that bucket only (read, write, delete and list
   objects; no bucket settings):

   ```bash
   gcloud storage buckets add-iam-policy-binding gs://BUCKET --member=serviceAccount:SA_EMAIL --role=roles/storage.objectUser
   ```

3. Set `Storage__Provider=Gcs` and `Storage__Bucket=BUCKET` on the service. Nothing else: the client library finds the
   service account's credentials on its own.

### Cloud Run: the demo service `workbench-git`

The demo runs as Cloud Run service `workbench-git` in project `gen-lang-client-0706696021`, region
`northamerica-northeast2`. These steps are for whoever owns that project; nothing here has been run yet.

```bash
PROJECT=gen-lang-client-0706696021
REGION=northamerica-northeast2
SERVICE=workbench-git
BUCKET=workbench-git-files-$PROJECT     # bucket names are global; pick any unused name
```

1. **The service account.** See which one the service runs as:

   ```bash
   gcloud run services describe $SERVICE --project=$PROJECT --region=$REGION --format='value(spec.template.spec.serviceAccountName)'
   ```

   Empty means the Compute Engine default service account (`PROJECT_NUMBER-compute@developer.gserviceaccount.com`),
   which has broad Editor rights. Better to give the service its own account with no project roles:

   ```bash
   gcloud iam service-accounts create workbench-git-run --project=$PROJECT --display-name="Workbench demo (Cloud Run)"
   SA=workbench-git-run@$PROJECT.iam.gserviceaccount.com
   ```

   If the service reads secrets from Secret Manager or connects to Cloud SQL, grant this account the same roles the
   current one uses for those (`roles/secretmanager.secretAccessor` on each secret, `roles/cloudsql.client`) before
   switching.

2. **The bucket and the role** (private, uniform access, public access blocked):

   ```bash
   gcloud storage buckets create gs://$BUCKET --project=$PROJECT --location=$REGION --uniform-bucket-level-access --public-access-prevention
   gcloud storage buckets add-iam-policy-binding gs://$BUCKET --member=serviceAccount:$SA --role=roles/storage.objectUser
   ```

3. **Point the service at the bucket** (this deploys a new revision):

   ```bash
   gcloud run services update $SERVICE --project=$PROJECT --region=$REGION --service-account=$SA \
     --update-env-vars=Storage__Provider=Gcs,Storage__Bucket=$BUCKET
   ```

   If the service is deployed from a pipeline or a `service.yaml`, add the same two variables and the service account
   there, or the next deploy removes them.

4. **Existing files.** The demo's files are sample data that the nightly reset deletes and re-seeds, and without a
   mounted volume `/data/blobs` disappears with each container anyway, so there is normally nothing to copy: the next
   reset seeds the bucket. To keep files that matter, copy them first (next section) from wherever `/data` is mounted.

5. **Check.** Open the demo, attach a file to a sheet row, open it again, then look in the bucket:

   ```bash
   gcloud storage ls -r gs://$BUCKET | head
   ```

   The readiness check doesn't probe the bucket; a missing role shows as an error when uploading.

6. **Then more than one instance.** Files are now shared, but the demo's database is SQLite on the container's disk
   (`/data/workbench.db`), which each instance would have its own copy of. Keep `--max-instances=1` (and session
   affinity) until the demo moves to PostgreSQL (Cloud SQL), then raise it.

## Amazon S3

1. Create the bucket with **Block Public Access** on (all four settings), **Object Ownership: Bucket owner enforced**
   (ACLs disabled), and default encryption SSE-S3 or SSE-KMS. Versioning is recommended.
2. Give the web host's role this policy (and `kms:GenerateDataKey`/`kms:Decrypt` on the key for SSE-KMS):

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       { "Effect": "Allow", "Action": ["s3:PutObject", "s3:GetObject", "s3:DeleteObject"], "Resource": "arn:aws:s3:::BUCKET/*" },
       { "Effect": "Allow", "Action": "s3:ListBucket", "Resource": "arn:aws:s3:::BUCKET" }
     ]
   }
   ```

3. Set `Storage__Provider=S3`, `Storage__Bucket=BUCKET`, `Storage__S3__Region=REGION`. On ECS, EKS or EC2 the role
   supplies credentials; don't set keys.

## MinIO (self-hosted)

```
Storage__Provider=S3
Storage__Bucket=workbench
Storage__S3__ServiceUrl=https://minio.internal:9000
Storage__S3__AccessKeyId=<secret>          # a MinIO user limited to this bucket
Storage__S3__SecretAccessKey=<secret>
```

Server-side encryption needs MinIO's KMS (for a single server, `MINIO_KMS_SECRET_KEY`; otherwise KES). Without it,
MinIO rejects the encryption header: configure the KMS rather than setting `ServerSideEncryption=None`, which
Production refuses. Leave the bucket's anonymous policy at `private`.

## Moving existing files into a bucket (`--copy-blobs`)

Once, when switching an installation from `Local` to a bucket. Run it with the new `Storage:*` settings, with the old
folder mounted at `Storage:Root`:

```bash
dotnet Workbench.Web.dll --copy-blobs           # dry run: lists what it would copy, writes nothing
dotnet Workbench.Web.dll --copy-blobs --apply   # copies, reads each file back and compares SHA-256
```

- Every file keeps its key (`tenants/acme/...` included), so database rows find them unchanged.
- The dry run hashes each local file and compares it with the bucket's copy, if any. With `--apply`, each missing file is
  uploaded and read back; it counts as copied only if the SHA-256 matches.
- A key already in the bucket with different content is a **conflict** and left alone (after the switch the bucket
  has the newer file). Files whose names aren't keys (leftover `.partial` uploads) are listed and skipped.
- It's safe to run again: it only copies what is still missing. It never deletes local files; remove the old volume
  once the app has run on the bucket for a while.
- Exit code 0 when everything is copied (or, for a dry run, when there are no conflicts), 1 otherwise.

Order for a running installation: deploy with the bucket settings while stopped (or accept that uploads during the
copy land in the bucket directly, which is fine), run `--copy-blobs`, then `--copy-blobs --apply`, then start the web
hosts. In containers, run it as a one-off job with the same image, settings and the old volume, like `--migrate`.

## Backups

Bucket versioning (or GCS soft delete) and, for disaster recovery, replication to a second region. See
[backup-restore.md](backup-restore.md).
