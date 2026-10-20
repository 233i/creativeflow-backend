-- First-party email/password credentials.
--
-- Password hashes use ASP.NET Core Identity's versioned format. The normalized email is the
-- authentication key; profile email remains on sol_account so OAuth-only accounts keep the same
-- provider-neutral shape.

CREATE TABLE IF NOT EXISTS sol_password_credential (
    account_id       uuid PRIMARY KEY REFERENCES sol_account(account_id) ON DELETE CASCADE,
    normalized_email text NOT NULL,
    password_hash    text NOT NULL,
    failed_attempts  int NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until     timestamptz NULL,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL,

    CONSTRAINT uq_password_credential_email UNIQUE (normalized_email),
    CONSTRAINT ck_password_credential_email_length
        CHECK (char_length(normalized_email) BETWEEN 3 AND 254),
    CONSTRAINT ck_password_credential_hash_length
        CHECK (char_length(password_hash) BETWEEN 20 AND 2048)
);
