-- BilvaskRegistrering – schema (idempotent)

CREATE TABLE IF NOT EXISTS public.wash_events (
  id BIGSERIAL PRIMARY KEY,
  occurred_at TIMESTAMPTZ NOT NULL,
  plate TEXT NOT NULL
);

ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS internnr TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS selskap TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS vehicle_type TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS season TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS status TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS cost NUMERIC(12,2) NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS note TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS source TEXT NULL;

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
  kommentar TEXT NULL,
  confirmed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT uq_wash_confirmations UNIQUE (wash_event_id)
);

ALTER TABLE public.wash_confirmations ADD COLUMN IF NOT EXISTS type_vask TEXT;
ALTER TABLE public.wash_confirmations ADD COLUMN IF NOT EXISTS kommentar TEXT;
UPDATE public.wash_confirmations SET type_vask = 'Innvendig/uttvendig vask' WHERE type_vask IS NULL;
ALTER TABLE public.wash_confirmations ALTER COLUMN type_vask SET NOT NULL;

CREATE TABLE IF NOT EXISTS public.egen_flate (
  id BIGSERIAL PRIMARY KEY,
  internnr TEXT NULL,
  registreringsnummer TEXT NOT NULL,
  selskap TEXT NULL,
  vehicle_type TEXT NULL,
  unntak BOOLEAN NOT NULL DEFAULT FALSE
);

ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS internnr TEXT NULL;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS registreringsnummer TEXT;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS selskap TEXT NULL;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS vehicle_type TEXT NULL;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS unntak BOOLEAN;
UPDATE public.egen_flate SET unntak = FALSE WHERE unntak IS NULL;
ALTER TABLE public.egen_flate ALTER COLUMN unntak SET DEFAULT FALSE;
ALTER TABLE public.egen_flate ALTER COLUMN unntak SET NOT NULL;

CREATE OR REPLACE FUNCTION public.fn_fill_wash_event_fields()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
  v_internnr text;
  v_selskap  text;
  v_vehicle_type text;
  v_unntak   boolean;
  v_plate_norm text;
BEGIN
  v_plate_norm := replace(upper(coalesce(NEW.plate,'')), ' ', '');
  SELECT ef.internnr, ef.selskap, ef.vehicle_type, ef.unntak
  INTO v_internnr, v_selskap, v_vehicle_type, v_unntak
  FROM public.egen_flate ef
  WHERE replace(upper(coalesce(ef.registreringsnummer,'')), ' ', '') = v_plate_norm
  LIMIT 1;

  IF NEW.internnr IS NULL OR NEW.internnr = '' THEN
    NEW.internnr := v_internnr;
  END IF;

  IF NEW.selskap IS NULL OR NEW.selskap = '' THEN
    NEW.selskap := v_selskap;
  END IF;

  IF v_unntak IS TRUE THEN
    NEW.status := 'Unntak';
  END IF;

  IF NEW.vehicle_type IS NULL OR NEW.vehicle_type = '' THEN
    IF v_vehicle_type IS NOT NULL AND v_vehicle_type <> '' THEN
      NEW.vehicle_type := v_vehicle_type;
    ELSE
      -- safer default: assume 'Liten' unless explicitly provided
      NEW.vehicle_type := 'Liten';
    END IF;
  END IF;

  IF NEW.season IS NULL OR NEW.season = '' THEN
    -- Default rule: Sommer 01.04->30.09, Vinter 01.10->31.03
    IF EXTRACT(MONTH FROM NEW.occurred_at) >= 10 OR EXTRACT(MONTH FROM NEW.occurred_at) <= 3 THEN
      NEW.season := 'Vinter';
    ELSE
      NEW.season := 'Sommer';
    END IF;
  END IF;

  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_fill_wash_event_fields ON public.wash_events;
CREATE TRIGGER trg_fill_wash_event_fields
BEFORE INSERT OR UPDATE OF plate ON public.wash_events
FOR EACH ROW
EXECUTE FUNCTION public.fn_fill_wash_event_fields();

CREATE OR REPLACE VIEW public.worker_active_washes AS
SELECT
  we.id,
  we.occurred_at AS dato,
  we.internnr,
  we.plate AS reg_nr,
  we.vehicle_type AS type_kjoretoy,
  wc.type_vask,
  we.season AS sesong,
  we.status,
  wc.kommentar,
  a.navn AS ansatt,
  wc.confirmed_at
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
LEFT JOIN public.ansatter a ON a.id = wc.ansatt_id
WHERE (we.source IS NULL OR LOWER(TRIM(we.source)) IN ('camera', 'kamera', 'its', 'worker', 'worker_offline'))
ORDER BY we.occurred_at DESC;

ALTER TABLE public.ansatter ADD COLUMN IF NOT EXISTS ansattnummer TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS ux_ansatter_ansattnummer ON public.ansatter(ansattnummer);
