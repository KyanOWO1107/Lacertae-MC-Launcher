CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_utc TEXT NOT NULL
);

CREATE TABLE game_roots (
    id TEXT PRIMARY KEY,
    normalized_path TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    availability INTEGER NOT NULL,
    last_scanned_utc TEXT NULL
);

CREATE TABLE version_overrides (
    game_root_id TEXT NOT NULL,
    version_folder TEXT NOT NULL,
    display_name TEXT NULL,
    isolation_override INTEGER NOT NULL,
    account_id TEXT NULL,
    java_path TEXT NULL,
    minimum_memory_mb INTEGER NULL,
    maximum_memory_mb INTEGER NULL,
    gc_profile INTEGER NULL,
    jvm_arguments_json TEXT NOT NULL,
    game_arguments_json TEXT NOT NULL,
    PRIMARY KEY (game_root_id, version_folder),
    FOREIGN KEY (game_root_id) REFERENCES game_roots(id) ON DELETE CASCADE
);

CREATE TABLE accounts (
    id TEXT PRIMARY KEY,
    provider_id TEXT NOT NULL,
    profile_uuid TEXT NOT NULL,
    account_type INTEGER NOT NULL,
    player_name TEXT NOT NULL,
    avatar_cache_key TEXT NULL,
    secret_ref TEXT NULL,
    status INTEGER NOT NULL,
    last_successful_login_utc TEXT NULL,
    UNIQUE (provider_id, profile_uuid)
);

CREATE TABLE background_tasks (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    state INTEGER NOT NULL,
    frozen_plan_json TEXT NOT NULL,
    journal_json TEXT NULL,
    problem_code TEXT NULL,
    updated_utc TEXT NOT NULL
);
