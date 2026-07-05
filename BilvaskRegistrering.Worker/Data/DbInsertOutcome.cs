namespace BilvaskRegistrering.Worker.Data;

internal enum DbInsertOutcome
{
    Inserted = 0,
    Deduped = 1,
    Failed = 2
}
