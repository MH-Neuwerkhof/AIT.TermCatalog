namespace AIT.TermCatalog;

/// <summary>Ergebnis eines Änderungsschritts am Data Standard (Datei, Beschreibung, angewendet?, Detail/Backup-Pfad).</summary>
public record ChangeResult(string File, string Description, bool Applied, string? Detail = null);