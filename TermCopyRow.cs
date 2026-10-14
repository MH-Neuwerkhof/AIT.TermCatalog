namespace AIT.TermCatalog;

/// <summary>Eine Zeile der Kopier-Zuordnung: beim Übernehmen eines Begriffs wird die Eigenschaft
/// <see cref="Property"/> mit dem Begriff in der Sprache <see cref="Lang"/> gefüllt.</summary>
public class TermCopyRow
{
    /// <summary>Name der Eigenschaft (Schlüssel im DataStandard, z. B. "Titel-EN"); fehlt sie im Fenster, wird die Zeile still übersprungen.</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>"DE", "EN", "FR" oder "IT".</summary>
    public string Lang { get; set; } = "EN";

    /// <summary>True = nur kopieren, wenn die Sprache in der Maske unter „Zusätzliche Sprachen“ angehakt ist.</summary>
    public bool OnlyChecked { get; set; } = true;

    public static readonly string[] Languages = { "DE", "EN", "FR", "IT" };

    public TermCopyRow Clone() => (TermCopyRow)MemberwiseClone();
}
