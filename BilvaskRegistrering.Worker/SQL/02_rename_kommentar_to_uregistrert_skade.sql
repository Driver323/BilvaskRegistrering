-- Rename wash_confirmations.kommentar -> uregistrert_skade (safe/idempotent)
DO $$
BEGIN
  IF EXISTS (
      SELECT 1 FROM information_schema.columns
      WHERE table_schema = 'public' AND table_name = 'wash_confirmations' AND column_name = 'kommentar'
  ) AND NOT EXISTS (
      SELECT 1 FROM information_schema.columns
      WHERE table_schema = 'public' AND table_name = 'wash_confirmations' AND column_name = 'uregistrert_skade'
  ) THEN
    ALTER TABLE public.wash_confirmations RENAME COLUMN kommentar TO uregistrert_skade;
  END IF;

  IF NOT EXISTS (
      SELECT 1 FROM information_schema.columns
      WHERE table_schema = 'public' AND table_name = 'wash_confirmations' AND column_name = 'uregistrert_skade'
  ) THEN
    ALTER TABLE public.wash_confirmations ADD COLUMN uregistrert_skade TEXT NULL;
  END IF;
END $$;

CREATE OR REPLACE VIEW public.worker_active_washes AS
SELECT
  we.id,
  we.occurred_at,
  we.internnr,
  we.plate AS reg_nr,
  we.vehicle_type AS type_kjoretoy,
  COALESCE(wc.type_vask, 'Innvendig/uttvendig vask') AS type_vask,
  we.season,
  we.status,
  wc.uregistrert_skade,
  a.navn AS ansatt,
  wc.confirmed_at
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
LEFT JOIN public.ansatter a ON a.id = wc.ansatt_id;

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.wash_confirmations TO bilvask_worker_app;
