using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

/// <summary>
/// Ensures the shared DB schema used by both apps exists.
/// Run this with ADMIN connection (bilvask_admin_app).
/// Safe to run multiple times.
/// </summary>
public static class DbSchemaMigrator
{
    public static async Task EnsureAsync(string adminConnectionString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adminConnectionString)) return;

        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct);

        // 1) wash_events (camera / management inserts here)
        // Create minimal table if missing, then add columns (idempotent).
        const string createWashEvents = @"
CREATE TABLE IF NOT EXISTS public.wash_events (
  id BIGSERIAL PRIMARY KEY,
  occurred_at TIMESTAMPTZ NOT NULL,
  plate TEXT NOT NULL
);
";

        const string alterWashEvents = @"
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS internnr TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS selskap TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS vehicle_type TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS season TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS status TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS cost NUMERIC(12,2) NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS note TEXT NULL;
ALTER TABLE public.wash_events ADD COLUMN IF NOT EXISTS source TEXT NULL;
";

        // 2) employees + confirmations + worker view
        const string createEmployeesAndConfirmations = @"
CREATE TABLE IF NOT EXISTS public.ansatter (
  id BIGSERIAL PRIMARY KEY,
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

-- In case the table existed before these columns:
ALTER TABLE public.wash_confirmations ADD COLUMN IF NOT EXISTS type_vask TEXT;
ALTER TABLE public.wash_confirmations ADD COLUMN IF NOT EXISTS uregistrert_skade TEXT;
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'wash_confirmations' AND column_name = 'kommentar'
  ) THEN
    EXECUTE 'UPDATE public.wash_confirmations SET uregistrert_skade = kommentar WHERE uregistrert_skade IS NULL AND kommentar IS NOT NULL';
  END IF;
END $$;
UPDATE public.wash_confirmations SET type_vask = 'Innvendig/uttvendig vask' WHERE type_vask IS NULL;
ALTER TABLE public.wash_confirmations ALTER COLUMN type_vask SET NOT NULL;



-- Vehicle lookup table (synced from EgenFlate.csv by worker/admin)
CREATE TABLE IF NOT EXISTS public.egen_flate (
  id BIGSERIAL PRIMARY KEY,
  internnr TEXT NULL,
  registreringsnummer TEXT NOT NULL,
  selskap TEXT NULL,
  vehicle_type TEXT NULL,
  unntak BOOLEAN NOT NULL DEFAULT FALSE
);

-- Ensure required columns exist (in case table existed before)
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS internnr TEXT NULL;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS registreringsnummer TEXT;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS selskap TEXT NULL;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS vehicle_type TEXT NULL;
ALTER TABLE public.egen_flate ADD COLUMN IF NOT EXISTS unntak BOOLEAN;

UPDATE public.egen_flate SET unntak = FALSE WHERE unntak IS NULL;
ALTER TABLE public.egen_flate ALTER COLUMN unntak SET DEFAULT FALSE;
ALTER TABLE public.egen_flate ALTER COLUMN unntak SET NOT NULL;

-- Fill missing fields from egen_flate + defaults (idempotent).
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
  -- Reject obvious OCR noise / truncated plates (e.g. SKYSS logo, EB7571)
  IF v_plate_norm !~ '^[A-Z]{2}[0-9]{5}$' THEN
    RETURN NULL; -- suppress insert/update
  END IF;

  SELECT ef.internnr, ef.selskap, ef.vehicle_type, ef.unntak
  INTO v_internnr, v_selskap, v_vehicle_type, v_unntak
  FROM public.egen_flate ef
  WHERE replace(upper(coalesce(ef.registreringsnummer,'')), ' ', '') = v_plate_norm
  LIMIT 1;

  -- If vehicle list exists, reject unknown plates to avoid OCR garbage polluting wash_events
  -- If vehicle list exists but plate is not found, keep the event but mark it for review.
  -- This ensures unknown vehicles (e.g., ambulances/visitors) are still registered,
  -- while keeping them separated from normal billing until verified in Admin.
  IF NOT FOUND AND (SELECT COUNT(*) FROM public.egen_flate) > 0 THEN
    IF NEW.status IS NULL OR NEW.status = '' THEN
      NEW.status := 'UregistrertKjøretøy';
    END IF;
    IF NEW.cost IS NULL THEN
      NEW.cost := 0;
    END IF;
  END IF;


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
    -- Default rule (can be overridden by the app by sending season explicitly):
    -- Sommer: 01.04 -> 30.09, Vinter: 01.10 -> 31.03
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
  wc.uregistrert_skade AS uregistrert_skade,
  a.navn AS ansatt,
  wc.confirmed_at
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
LEFT JOIN public.ansatter a ON a.id = wc.ansatt_id
-- IMPORTANT: Arbeidsliste must include *all* sources (kamera, worker, csv_sync, csv_import, etc.).
-- We only exclude clearly invalid regnr formats.
WHERE regexp_replace(we.plate, '\s', '', 'g') ~ '^[A-Z]{2}[0-9]{5}$'
ORDER BY we.occurred_at DESC;


-- Permissions:
GRANT USAGE ON SCHEMA public TO bilvask_worker_app;
GRANT USAGE ON SCHEMA public TO bilvask_admin_app;

GRANT SELECT ON public.worker_active_washes TO bilvask_worker_app;
GRANT SELECT ON public.worker_active_washes TO bilvask_admin_app;

GRANT SELECT ON public.ansatter TO bilvask_worker_app;
GRANT SELECT ON public.ansatter TO bilvask_admin_app;

-- Worker must be able to register wash events independently when Admin is offline.
GRANT INSERT ON public.wash_events TO bilvask_worker_app;
-- Admin can also insert (e.g. manual corrections / tools).
GRANT INSERT ON public.wash_events TO bilvask_admin_app;

GRANT INSERT, UPDATE, DELETE ON public.wash_confirmations TO bilvask_worker_app;
GRANT INSERT, UPDATE, DELETE ON public.wash_confirmations TO bilvask_admin_app;

ALTER VIEW public.worker_active_washes OWNER TO bilvask_admin_app;


-- Review queue (unknown/unverified vehicles)
CREATE TABLE IF NOT EXISTS public.wash_events_review (
  wash_event_id BIGINT PRIMARY KEY REFERENCES public.wash_events(id) ON DELETE CASCADE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  reason TEXT NOT NULL,
  hit_count INTEGER NOT NULL DEFAULT 1,
  last_seen TIMESTAMPTZ NOT NULL DEFAULT now(),
  handled_by TEXT NULL,
  handled_at TIMESTAMPTZ NULL,
  handled_action TEXT NULL,
  handled_note TEXT NULL
);

-- Enqueue events for review when status indicates 'unregistered vehicle'
CREATE OR REPLACE FUNCTION public.fn_queue_wash_event_review()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
  -- Some parts of the system historically used ASCII-only spelling.
  IF NEW.status IN ('UregistrertKjøretøy', 'UregistrertKjoretoy') THEN
    INSERT INTO public.wash_events_review (wash_event_id, reason, hit_count, last_seen)
    VALUES (NEW.id, 'UregistrertKjøretøy', 1, now())
    ON CONFLICT (wash_event_id)
    DO UPDATE SET
      reason = EXCLUDED.reason,
      hit_count = public.wash_events_review.hit_count + 1,
      last_seen = now();
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_queue_wash_event_review ON public.wash_events;
CREATE TRIGGER trg_queue_wash_event_review
AFTER INSERT OR UPDATE OF status ON public.wash_events
FOR EACH ROW
EXECUTE FUNCTION public.fn_queue_wash_event_review();

-- Cleanup legacy/experimental verification objects that may break inserts/updates.
-- (We keep the stable review queue above; this only removes older broken triggers/functions/tables.)
DROP TRIGGER IF EXISTS trg_set_wash_events_norm_fields ON public.wash_events;
DROP TRIGGER IF EXISTS trg_route_unverified_or_duplicate ON public.wash_events;
DROP FUNCTION IF EXISTS public.set_wash_events_norm_fields() CASCADE;
DROP FUNCTION IF EXISTS public.route_unverified_or_duplicate() CASCADE;
DROP TABLE IF EXISTS public.wash_events_verification CASCADE;


-- View for Admin/Server-Admin verification UI
DROP VIEW IF EXISTS public.v_wash_events_to_review;
CREATE VIEW public.v_wash_events_to_review AS
SELECT
  r.wash_event_id,
  r.created_at,
  r.reason,
  r.hit_count,
  r.last_seen,
  we.occurred_at,
  we.selskap,
  we.internnr,
  we.plate,
  we.vehicle_type,
  we.season,
  we.status,
  we.cost,
  we.note,
  we.source
FROM public.wash_events_review r
JOIN public.wash_events we ON we.id = r.wash_event_id
WHERE r.handled_at IS NULL;


-- Permissions for review objects
GRANT SELECT ON public.wash_events_review TO bilvask_worker_app;
GRANT SELECT, UPDATE ON public.wash_events_review TO bilvask_admin_app;

GRANT SELECT ON public.v_wash_events_to_review TO bilvask_worker_app;
GRANT SELECT ON public.v_wash_events_to_review TO bilvask_admin_app;

ALTER TABLE public.wash_events_review OWNER TO bilvask_admin_app;
ALTER VIEW public.v_wash_events_to_review OWNER TO bilvask_admin_app;

";

        await using (var cmd = new NpgsqlCommand(createWashEvents, conn))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand(alterWashEvents, conn))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new NpgsqlCommand(createEmployeesAndConfirmations, conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }
}
