-- 后台图片任务与下载检查点。已开始的上游请求绝不因 worker 重启而自动重发。
CREATE TABLE image_job (
    job_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES sol_account(account_id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    provider_id uuid NOT NULL,
    canvas_id uuid NULL,
    node_id text NULL,
    request jsonb NOT NULL,
    request_hash text NOT NULL,
    status text NOT NULL CHECK (status IN ('queued','generating','saving','succeeded','failed')),
    failure_stage text NULL,
    error text NULL,
    has_checkpoint boolean NOT NULL DEFAULT false,
    lease_id uuid NULL,
    lease_expires_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz NULL,
    finished_at timestamptz NULL
);
CREATE INDEX ix_image_job_queue ON image_job (created_at, job_id)
    WHERE status IN ('queued','generating','saving');
CREATE INDEX ix_image_job_canvas ON image_job (account_id, canvas_id, job_id DESC);
CREATE INDEX ix_image_job_dedup ON image_job (account_id, request_hash)
    WHERE status IN ('queued','generating','saving');

-- 重复点击可有不同的 Key，但每个 Key 永久指向已接受的同一个任务。
CREATE TABLE image_job_submission (
    account_id uuid NOT NULL REFERENCES sol_account(account_id) ON DELETE CASCADE,
    submission_key uuid NOT NULL,
    request_hash text NOT NULL,
    job_id uuid NOT NULL REFERENCES image_job(job_id) ON DELETE CASCADE,
    PRIMARY KEY (account_id, submission_key)
);
CREATE INDEX ix_image_job_submission_job ON image_job_submission (job_id);

CREATE TABLE image_job_output (
    job_id uuid NOT NULL REFERENCES image_job(job_id) ON DELETE CASCADE,
    output_index integer NOT NULL,
    asset_id uuid NOT NULL UNIQUE,
    media_type text NOT NULL,
    payload bytea NULL,
    download_url text NULL,
    provider_base_url text NULL,
    source_url text NULL,
    source_url_expires_at timestamptz NULL,
    saved boolean NOT NULL DEFAULT false,
    PRIMARY KEY (job_id, output_index)
);
