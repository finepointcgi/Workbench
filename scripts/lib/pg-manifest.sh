# shellcheck shell=bash
# Shared by backup-all.sh and restore-test.sh (source it). A manifest is what a restore must reproduce exactly:
# the row count of every table and a fingerprint of the trial balance (SHA-256 of each GL account's posted debits and
# credits) plus whether it balances. It holds no business data, only counts and a hash.

# Prints the manifest as tab-separated lines: "table <name> <rows>", "trial_balance sha256 <hex|none|empty>" and
# "trial_balance balanced <true|false|n/a>". Run it inside the transaction or on the database to describe.
manifest_sql() {
  cat <<'SQL'
SELECT kind, name, value FROM (
  SELECT 'table' AS kind, table_name::text AS name,
         (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text AS value
  FROM information_schema.tables
  WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
  UNION ALL
  SELECT 'trial_balance', k.name,
         CASE WHEN to_regclass('public."GlJournalLines"') IS NULL OR to_regclass('public."GlJournalEntries"') IS NULL
              THEN CASE k.name WHEN 'sha256' THEN 'none' ELSE 'n/a' END
              ELSE (xpath('/row/' || k.col || '/text()', query_to_xml($q$
                SELECT coalesce(encode(sha256(convert_to(string_agg(a || ':' || d || ':' || c, ',' ORDER BY a), 'UTF8')), 'hex'), 'empty') AS h,
                       coalesce(sum(d) = sum(c), true) AS b
                FROM (SELECT l."AccountId"::text AS a, sum(l."Debit") AS d, sum(l."Credit") AS c
                      FROM "GlJournalLines" l JOIN "GlJournalEntries" e ON e."Id" = l."JournalEntryId"
                      WHERE e."Status" <> 'Draft'
                      GROUP BY l."AccountId") t
              $q$, false, true, '')))[1]::text
         END
  FROM (VALUES ('sha256', 'h'), ('balanced', 'b')) AS k(name, col)
) m
ORDER BY kind, name;
SQL
}

# Tenant database names from the catalog (MultiTenant), Active and Suspended tenants, using the same naming as
# Tenancy:TenantConnectionString ("{tenant}" replaced by the slug with '-' as '_').
tenant_databases() {
  local catalog="$1" template="$2" slug
  psql -X -At -v ON_ERROR_STOP=1 -d "$catalog" -c "SELECT \"Slug\" FROM \"Tenants\" WHERE \"Status\" IN ('Active', 'Suspended') ORDER BY 1" |
    while IFS= read -r slug; do [[ -n "$slug" ]] && printf '%s\n' "${template//\{tenant\}/${slug//-/_}}"; done
}

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }
