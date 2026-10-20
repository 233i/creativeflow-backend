-- 画布每次生成单独记录；工作流继续使用已有的运行及节点快照。
CREATE TABLE IF NOT EXISTS canvas_generation_request (
    request_id uuid PRIMARY KEY,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    account_id uuid NULL REFERENCES sol_account(account_id) ON DELETE SET NULL,
    canvas_id uuid NULL REFERENCES canvas(canvas_id) ON DELETE SET NULL,
    canvas_name text NULL,
    node_id text NULL,
    kind text NOT NULL CHECK (kind IN ('text', 'image', 'video')),
    provider_id uuid NULL,
    model_key text NOT NULL,
    input jsonb NOT NULL,
    output jsonb NULL,
    status text NOT NULL CHECK (status IN ('running', 'succeeded', 'failed', 'cancelled')),
    error text NULL,
    video_job_id uuid NULL REFERENCES video_job(job_id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL,
    finished_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_canvas_generation_request_account_created
    ON canvas_generation_request (account_id, created_at DESC, request_id DESC);
CREATE INDEX IF NOT EXISTS ix_canvas_generation_request_created
    ON canvas_generation_request (created_at DESC, request_id DESC);
CREATE UNIQUE INDEX IF NOT EXISTS uq_canvas_generation_request_video
    ON canvas_generation_request (video_job_id) WHERE video_job_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_workflow_run_device_created
    ON workflow_run (device_id, created_at DESC, run_id DESC);

-- 视频状态取自现有轮询器的任务行，不维护第二套异步状态机。
CREATE OR REPLACE VIEW admin_canvas_request_history AS
SELECT r.request_id, r.device_id, r.account_id, r.canvas_id, r.canvas_name, r.node_id,
       r.kind, r.provider_id, r.model_key, r.input,
       CASE WHEN j.asset_id IS NOT NULL
            THEN jsonb_build_object('assetId', j.asset_id)
            ELSE r.output END AS output,
       COALESCE(j.status, r.status) AS status, COALESCE(j.error, r.error) AS error,
       r.created_at,
       CASE WHEN j.status IN ('succeeded', 'failed', 'cancelled') THEN j.updated_at
            ELSE r.finished_at END AS finished_at,
       false AS historical
FROM canvas_generation_request r
LEFT JOIN video_job j ON j.job_id = r.video_job_id
UNION ALL
-- 已有视频任务可准确恢复为一次请求；工作流内的视频不重复计入画布。
SELECT j.job_id, j.device_id, ad.account_id, NULL::uuid, NULL::text, NULL::text,
       'video', j.provider_id, j.model_key, j.request,
       CASE WHEN j.asset_id IS NOT NULL THEN jsonb_build_object('assetId', j.asset_id) END,
       j.status, j.error, j.created_at,
       CASE WHEN j.status IN ('succeeded', 'failed', 'cancelled') THEN j.updated_at END,
       true
FROM video_job j
LEFT JOIN sol_account_device ad ON ad.device_id = j.device_id
WHERE NOT (j.request ? 'workflowRunId')
  AND NOT EXISTS (SELECT 1 FROM canvas_generation_request r WHERE r.video_job_id = j.job_id);
