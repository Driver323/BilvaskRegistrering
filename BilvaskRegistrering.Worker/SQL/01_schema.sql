-- Run as ADMIN (bilvask_admin_app) in database: bilvaskregistrering
-- This adds employee table + wash confirmations + worker view.

CREATE TABLE IF NOT EXISTS public.ansatter (
  id BIGSERIAL PRIMARY KEY,
  ansattnummer TEXT,
  navn TEXT NOT NULL,
  pin TEXT NULL,
  aktiv BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.wash_confirmations (
  id BIGSERIAL PRIMARY KEY,
  wash_event_id BIGINT NOT NULL REFERENCES public.wash_events(id) ON DELETE CASCADE,
  ansatt_id BIGINT NOT NULL REFERENCES public.ansatter(id),
  type_vask TEXT NOT NULL DEFAULT 'Innvendig/uttvendig vask',
  uregistrert_skade TEXT NULL,
  confirmed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT uq_wash_confirmations UNIQUE (wash_event_id)
);

-- If you already have an older table, run:
-- ALTER TABLE public.wash_confirmations ADD COLUMN IF NOT EXISTS type_vask TEXT;
-- ALTER TABLE public.wash_confirmations ADD COLUMN IF NOT EXISTS uregistrert_skade TEXT;
-- UPDATE public.wash_confirmations SET type_vask = 'Innvendig/uttvendig vask' WHERE type_vask IS NULL;
-- ALTER TABLE public.wash_confirmations ALTER COLUMN type_vask SET NOT NULL;

CREATE OR REPLACE VIEW public.worker_active_washes AS
SELECT
  we.id,
  we.occurred_at AS dato,
  we.internnr,
  we.plate AS reg_nr,
  we.vehicle_type AS type_kjoretoy,
  wc.type_vask,
  we.season AS season,
  we.status,
  wc.uregistrert_skade,
  a.navn AS ansatt,
  wc.confirmed_at
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
LEFT JOIN public.ansatter a ON a.id = wc.ansatt_id
WHERE (we.source IS NULL OR we.source IN ('camera','kamera'))
ORDER BY we.occurred_at DESC;

-- Permissions (optional):
-- REVOKE ALL ON SCHEMA public FROM wash_worker;
-- GRANT USAGE ON SCHEMA public TO wash_worker;
-- REVOKE ALL ON ALL TABLES IN SCHEMA public FROM wash_worker;
-- REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM wash_worker;
-- GRANT SELECT ON public.worker_active_washes TO wash_worker;
-- GRANT SELECT ON public.ansatter TO wash_worker;
-- GRANT INSERT ON public.wash_confirmations TO wash_worker;
-- ALTER VIEW public.worker_active_washes OWNER TO bilvask_admin_app;


-- Ensure older DBs get new columns
ALTER TABLE public.ansatter ADD COLUMN IF NOT EXISTS ansattnummer TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS ux_ansatter_ansattnummer ON public.ansatter(ansattnummer);

-- Permissions for worker app (run as admin)
GRANT USAGE ON SCHEMA public TO bilvask_worker_app;
GRANT SELECT ON public.worker_active_washes TO bilvask_worker_app;
GRANT SELECT ON public.ansatter TO bilvask_worker_app;
GRANT INSERT ON public.wash_events TO bilvask_worker_app;
GRANT INSERT, UPDATE ON public.wash_confirmations TO bilvask_worker_app;
