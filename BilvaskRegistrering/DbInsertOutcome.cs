/// <summary>
/// Wynik próby zapisu do DB.
/// Używane w Admin (kamera) żeby rozróżnić: zapisano / zduplikowano / błąd / wrzucono do kolejki.
/// Plik bez namespace celowo – część kodu w projekcie jest w global namespace.
/// </summary>
public enum DbInsertOutcome
{
    Inserted = 0,
    Deduped = 1,
    Failed = 2,
    Queued = 3
}
