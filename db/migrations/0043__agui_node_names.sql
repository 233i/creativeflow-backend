-- AG-UI 节点事件保留节点 ID，同时从本次运行的固定图快照写入安全显示名称。
CREATE OR REPLACE FUNCTION sol_journal_workflow_node() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    node_name text;
BEGIN
    IF TG_OP='UPDATE' AND (NEW.status,NEW.progress,NEW.error,NEW.is_reused,NEW.started_at,NEW.finished_at)
        IS NOT DISTINCT FROM (OLD.status,OLD.progress,OLD.error,OLD.is_reused,OLD.started_at,OLD.finished_at)
        THEN RETURN NEW; END IF;
    SELECT COALESCE(
        NULLIF(node->'data'->>'title', ''),
        NULLIF(node->'data'->>'label', ''),
        NULLIF(node->>'name', ''),
        CASE node->>'type'
            WHEN 'start' THEN '开始' WHEN 'end' THEN '结束' WHEN 'text' THEN '文本输入' WHEN 'image' THEN '图片输入'
            WHEN 'llm' THEN 'LLM' WHEN 'knowledgeRetrieve' THEN '知识检索' WHEN 'knowledgeAnswer' THEN '知识问答'
            WHEN 'parameterExtract' THEN '参数提取' WHEN 'intentClassify' THEN '意图分类' WHEN 'transform' THEN '变量处理'
            WHEN 'condition' THEN '条件分支' WHEN 'merge' THEN '结果汇合' WHEN 'httpRequest' THEN 'HTTP 请求'
            WHEN 'imageGenerate' THEN '图片生成' WHEN 'videoGenerate' THEN '视频生成' WHEN 'videoExtend' THEN '视频延长'
            WHEN 'videoFrame' THEN '提取尾帧' WHEN 'imageOutput' THEN '图片结果' WHEN 'videoOutput' THEN '视频结果'
            WHEN 'output' THEN '输出' ELSE NULLIF(node->>'type', '') END,
        NULLIF(NEW.node_type, ''),
        NEW.node_id)
    INTO node_name
    FROM workflow_run run
    LEFT JOIN LATERAL jsonb_array_elements(
        CASE WHEN jsonb_typeof(run.graph_snapshot->'nodes') = 'array'
             THEN run.graph_snapshot->'nodes' ELSE '[]'::jsonb END) node ON TRUE
    WHERE run.run_id=NEW.run_id AND node->>'id'=NEW.node_id
    LIMIT 1;
    node_name := COALESCE(node_name, NULLIF(NEW.node_type, ''), NEW.node_id);
    PERFORM sol_append_workflow_event(NEW.run_id,'node.state',jsonb_build_object(
        'id',NEW.node_id,'name',node_name,'type',NEW.node_type,'status',NEW.status,'progress',NEW.progress,
        'isReused',NEW.is_reused,'error',NEW.error,'startedAt',NEW.started_at,'finishedAt',NEW.finished_at));
    RETURN NEW;
END;
$$;
