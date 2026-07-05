using System;

namespace BilvaskRegistrering.Models;

public sealed class VaskeHendelse
{
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string Plate { get; set; } = "";
    public string? Selskap { get; set; }
    public string? VehicleType { get; set; }
    public string? Season { get; set; }
    public string? Status { get; set; }
    public decimal? Cost { get; set; }
}
