-- 参考脚本（运行时不自动执行）。
-- 运行时表结构以 SqlSugar CodeFirst 实体为准：
--   Persistence/KnowledgeSchemaEntities.cs
--   Infrastructure/Persistence/IndexSchemaEntities.cs
-- 新增的索引生命周期、Outbox 与摘要表可能滞后于本脚本，请以实体定义为准。
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS kb_knowledge_bases (
	id uuid PRIMARY KEY,
	name text NOT NULL,
	created_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_kb_knowledge_bases_normalized_name
	ON kb_knowledge_bases (lower(name));

INSERT INTO kb_knowledge_bases (id, name)
VALUES ('11111111-1111-1111-1111-111111111111', '默认知识库')
ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS kb_documents (
	id uuid PRIMARY KEY,
	knowledge_base_id uuid NOT NULL REFERENCES kb_knowledge_bases(id),
	file_name text NOT NULL,
	extension text NOT NULL,
	content_type text NOT NULL,
	size_bytes bigint NOT NULL,
	storage_path text NOT NULL,
	status text NOT NULL,
	error text NULL,
	created_at timestamptz NOT NULL DEFAULT now(),
	file_hash text NULL,
	content_hash text NULL,
	content_version integer NOT NULL DEFAULT 0
);

ALTER TABLE kb_documents
	ADD COLUMN IF NOT EXISTS knowledge_base_id uuid REFERENCES kb_knowledge_bases(id);

UPDATE kb_documents
	SET knowledge_base_id = '11111111-1111-1111-1111-111111111111'
	WHERE knowledge_base_id IS NULL;

ALTER TABLE kb_documents ALTER COLUMN knowledge_base_id SET NOT NULL;

CREATE INDEX IF NOT EXISTS ix_kb_documents_knowledge_base_id ON kb_documents(knowledge_base_id);

CREATE TABLE IF NOT EXISTS kb_chunks (
	id bigserial PRIMARY KEY,
	document_id uuid NOT NULL REFERENCES kb_documents(id) ON DELETE CASCADE,
	document_version integer NOT NULL DEFAULT 1,
	chunk_index integer NOT NULL,
	content text NOT NULL,
	content_hash text NULL,
	-- 分块与向量化已解耦；该列仅保留历史兼容，新向量写入 kb_index_vectors。
	embedding vector NULL,
	created_at timestamptz NOT NULL DEFAULT now(),
	UNIQUE (document_id, document_version, chunk_index)
);

CREATE INDEX IF NOT EXISTS ix_kb_chunks_document_id ON kb_chunks(document_id);

CREATE TABLE IF NOT EXISTS knowledge_chat_sessions (
	id uuid PRIMARY KEY,
	knowledge_base_id uuid NOT NULL REFERENCES kb_knowledge_bases(id) ON DELETE CASCADE,
	title text NOT NULL,
	created_at timestamptz NOT NULL DEFAULT now(),
	updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_knowledge_chat_sessions_base_updated
	ON knowledge_chat_sessions(knowledge_base_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS knowledge_chat_messages (
	id bigserial PRIMARY KEY,
	session_id uuid NOT NULL REFERENCES knowledge_chat_sessions(id) ON DELETE CASCADE,
	role text NOT NULL CHECK (role IN ('user', 'assistant')),
	content text NOT NULL,
	created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_knowledge_chat_messages_session
	ON knowledge_chat_messages(session_id, id);

CREATE TABLE IF NOT EXISTS companion_profiles (
	id uuid PRIMARY KEY,
	name text NOT NULL,
	target_sender text NOT NULL,
	platform text NOT NULL,
	persona_prompt text NOT NULL DEFAULT '',
	created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS companion_messages (
	id bigserial PRIMARY KEY,
	profile_id uuid NOT NULL REFERENCES companion_profiles(id) ON DELETE CASCADE,
	sender text NOT NULL,
	content text NOT NULL,
	sent_at timestamptz NULL,
	created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_companion_messages_profile_time
	ON companion_messages(profile_id, sent_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS companion_sessions (
	id uuid PRIMARY KEY,
	profile_id uuid NOT NULL REFERENCES companion_profiles(id) ON DELETE CASCADE,
	title text NOT NULL,
	created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS companion_session_messages (
	id bigserial PRIMARY KEY,
	session_id uuid NOT NULL REFERENCES companion_sessions(id) ON DELETE CASCADE,
	role text NOT NULL CHECK (role IN ('user', 'assistant')),
	content text NOT NULL,
	created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_companion_session_messages_session
	ON companion_session_messages(session_id, id);
-- 分身（Avatar）：单例画像 + 经历 / 行为 / 人际关系 / 轨迹推演
CREATE TABLE IF NOT EXISTS avatar_profile (
        id uuid PRIMARY KEY,
        display_name text NOT NULL DEFAULT '',
        headline text NOT NULL DEFAULT '',
        personality text NOT NULL DEFAULT '',
        values_text text NOT NULL DEFAULT '',
        strengths text NOT NULL DEFAULT '',
        weaknesses text NOT NULL DEFAULT '',
        skills text NOT NULL DEFAULT '',
        resources text NOT NULL DEFAULT '',
        constraints text NOT NULL DEFAULT '',
        long_term_goals text NOT NULL DEFAULT '',
        risk_preference text NOT NULL DEFAULT 'balanced',
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS avatar_experiences (
        id uuid PRIMARY KEY,
        occurred_at timestamptz NOT NULL,
        category text NOT NULL DEFAULT '其它',
        title text NOT NULL,
        content text NOT NULL DEFAULT '',
        outcome text NOT NULL DEFAULT 'ongoing',
        emotion text NOT NULL DEFAULT 'neutral',
        impact integer NOT NULL DEFAULT 3,
        tags text NOT NULL DEFAULT '[]',
        created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_avatar_experiences_occurred
        ON avatar_experiences(occurred_at DESC);

CREATE TABLE IF NOT EXISTS avatar_behaviors (
        id uuid PRIMARY KEY,
        occurred_on timestamptz NOT NULL,
        category text NOT NULL DEFAULT '其它',
        description text NOT NULL,
        duration_minutes integer NOT NULL DEFAULT 0,
        mood integer NULL,
        tags text NOT NULL DEFAULT '[]',
        source text NOT NULL DEFAULT 'manual',
        created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_avatar_behaviors_occurred
        ON avatar_behaviors(occurred_on DESC);

CREATE TABLE IF NOT EXISTS avatar_relationships (
        id uuid PRIMARY KEY,
        name text NOT NULL,
        role text NOT NULL DEFAULT '其它',
        closeness integer NOT NULL DEFAULT 3,
        influence integer NOT NULL DEFAULT 3,
        notes text NOT NULL DEFAULT '',
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS avatar_simulations (
        id uuid PRIMARY KEY,
        scenario text NOT NULL,
        goal text NOT NULL DEFAULT '',
        horizon_months integer NOT NULL DEFAULT 6,
        status text NOT NULL DEFAULT 'running',
        success_probability integer NULL,
        confidence integer NULL,
        summary text NOT NULL DEFAULT '',
        report text NOT NULL DEFAULT '',
        key_factors text NOT NULL DEFAULT '[]',
        milestones text NOT NULL DEFAULT '[]',
        risks text NOT NULL DEFAULT '[]',
        next_actions text NOT NULL DEFAULT '[]',
        model text NOT NULL DEFAULT '',
        error_message text NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        completed_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_avatar_simulations_created
        ON avatar_simulations(created_at DESC);

-- 分身（v2）：碎片素材 / 人格洞察 / 第一人称对话
-- 与知识库隔离：这些表不参与 kb_* 的检索与索引。
CREATE TABLE IF NOT EXISTS avatar_fragments (
        id uuid PRIMARY KEY,
        kind text NOT NULL DEFAULT 'journal',
        title text NOT NULL DEFAULT '',
        content text NOT NULL,
        occurred_at timestamptz NOT NULL,
        source text NOT NULL DEFAULT 'manual',
        tags text NOT NULL DEFAULT '[]',
        is_digested boolean NOT NULL DEFAULT false,
        created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_avatar_fragments_occurred
        ON avatar_fragments(occurred_at DESC);

CREATE INDEX IF NOT EXISTS ix_avatar_fragments_digested
        ON avatar_fragments(is_digested);

CREATE TABLE IF NOT EXISTS avatar_insights (
        id uuid PRIMARY KEY,
        dimension text NOT NULL DEFAULT 'summary',
        title text NOT NULL,
        content text NOT NULL,
        confidence integer NOT NULL DEFAULT 50,
        evidence_count integer NOT NULL DEFAULT 0,
        evidence text NOT NULL DEFAULT '[]',
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_avatar_insights_dimension_title
        ON avatar_insights(dimension, title);

CREATE TABLE IF NOT EXISTS avatar_chat_sessions (
        id uuid PRIMARY KEY,
        title text NOT NULL DEFAULT '新的对话',
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_avatar_chat_sessions_updated
        ON avatar_chat_sessions(updated_at DESC);

CREATE TABLE IF NOT EXISTS avatar_chat_messages (
        id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
        session_id uuid NOT NULL REFERENCES avatar_chat_sessions(id) ON DELETE CASCADE,
        role text NOT NULL CHECK (role IN ('user', 'assistant')),
        content text NOT NULL,
        citations text NOT NULL DEFAULT '[]',
        created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_avatar_chat_messages_session
        ON avatar_chat_messages(session_id, id);