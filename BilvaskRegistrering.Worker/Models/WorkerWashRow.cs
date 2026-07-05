using System;

namespace BilvaskRegistrering.Worker.Models;

public sealed class WorkerWashRow
{
    public long Id { get; set; }
    public DateTime Dato { get; set; }
    // Turnus / shift info (computed client-side)
    public int SkiftNr { get; set; }
    public string Skift { get; set; } = "";
    public string? Internnr { get; set; }
    public string RegNr { get; set; } = "";
    public string? TypeKjoretoy { get; set; }
    public string? TypeVask { get; set; }
    public string? Sesong { get; set; }
    public string? Status { get; set; }
    public string? Ansatt { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    // Keep the legacy property name for existing WorkerForm/WorkerDb code.
    public string? Kommentar { get; set; }

    // Compatibility alias for the newer UI label / SQL alias.
    public string? UregistrertSkade
    {
        get => Kommentar;
        set => Kommentar = value;
    }
}
