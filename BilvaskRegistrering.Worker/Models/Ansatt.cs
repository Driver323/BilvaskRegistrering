namespace BilvaskRegistrering.Worker.Models;

public sealed class Ansatt
{
    public long Id { get; set; }
    public string Ansattnummer { get; set; } = "";
    public string Navn { get; set; } = "";
    public override string ToString() => string.IsNullOrWhiteSpace(Ansattnummer) ? Navn : $"{Ansattnummer} - {Navn}";
}
