CREATE TABLE accounts_v2 (
    id TEXT PRIMARY KEY CHECK(length(id) = 32),
    provider_id TEXT NOT NULL,
    profile_uuid TEXT NOT NULL,
    account_type INTEGER NOT NULL CHECK(account_type IN (0, 1)),
    player_name TEXT NOT NULL,
    avatar_cache_key TEXT NULL,
    secret_ref TEXT NULL,
    status INTEGER NOT NULL CHECK(status IN (0, 1, 2)),
    last_successful_login_utc TEXT NULL,
    UNIQUE (provider_id, profile_uuid)
);

INSERT INTO accounts_v2(
    id, provider_id, profile_uuid, account_type, player_name, avatar_cache_key,
    secret_ref, status, last_successful_login_utc)
SELECT
    id, provider_id, profile_uuid, account_type, player_name, avatar_cache_key,
    secret_ref, status, last_successful_login_utc
FROM accounts;

DROP TABLE accounts;
ALTER TABLE accounts_v2 RENAME TO accounts;
CREATE INDEX ix_accounts_status ON accounts(status);
