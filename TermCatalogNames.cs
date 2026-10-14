using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIT.TermCatalog;

/// <summary>
/// Namen der Custom-Object-Definitionen und -Eigenschaften des Benennungskatalogs (Terms).
/// Der DataStandard findet sie über ihren ANZEIGENAMEN (Einzahl) – er steht in MenuDefinitions.xml, den
/// Konfigurationsordnern, im psm1 und im Vault-Skript. Die Standardwerte sind die Namen des Köchl-Samples;
/// "Deutsch" entspricht den UIStrings (de-DE). Muss mit den Namen in Vault übereinstimmen.
/// </summary>
public class TermCatalogNames
{
    public const string PresetGerman  = "Deutsch";
    public const string PresetEnglish = "Englisch";
    public const string PresetCustom  = "Eigene";
    public static readonly string[] Presets = { PresetGerman, PresetEnglish, PresetCustom };

    public string Segment   { get; set; } = "Segment";
    public string MainGroup { get; set; } = "Main Group";
    public string Group     { get; set; } = "Group";
    public string SubGroup  { get; set; } = "Sub Group";
    public string Term      { get; set; } = "Term";
    public string TermDE    { get; set; } = "Term DE";
    public string TermEN    { get; set; } = "Term EN";
    public string TermFR    { get; set; } = "Term FR";
    public string TermIT    { get; set; } = "Term IT";

    /// <summary>Sprache des Objektnamens eines Begriffs ("DE", "EN", "FR", "IT"): der Name des Custom Objects (Spalte "Name"
    /// in Vault, Suchtreffer "Name") ist der Begriff in dieser Sprache. Köchl-Original: EN.</summary>
    public string NameLang { get; set; } = "EN";

    /// <summary>Name der Eigenschaft, mit der der Objektname eines Begriffs synchron gehalten wird.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string NameProperty => NameLang switch { "DE" => TermDE, "FR" => TermFR, "IT" => TermIT, _ => TermEN };

    public static TermCatalogNames English => new();

    public static TermCatalogNames German => new()
    {
        Segment = "Sachgebiet", MainGroup = "Hauptgruppe", Group = "Gruppe", SubGroup = "Untergruppe", Term = "Begriff",
        TermDE = "Begriff DE", TermEN = "Begriff EN", TermFR = "Begriff FR", TermIT = "Begriff IT",
        NameLang = "DE",
    };

    public TermCatalogNames Clone() => (TermCatalogNames)MemberwiseClone();

    /// <summary>Die Namen der gewählten Vorlage (bei "Eigene" die gespeicherten eigenen Namen, sonst Deutsch).</summary>
    public static TermCatalogNames Resolve(string preset, TermCatalogNames? custom) => preset switch
    {
        PresetEnglish => English,
        PresetCustom  => (custom ?? German).Clone(),
        _             => German,
    };

    public bool SameAs(TermCatalogNames o) => NameLang == o.NameLang && All.SequenceEqual(o.All, StringComparer.Ordinal);

    /// <summary>Die fünf Ebenen-/Objektnamen in Reihenfolge Sachgebiet … Begriff.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> Levels => new[] { Segment, MainGroup, Group, SubGroup, Term };

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> All => new[] { Segment, MainGroup, Group, SubGroup, Term, TermDE, TermEN, TermFR, TermIT };

    /// <summary>Kurzbeschreibung für Statuszeilen.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => string.Join(" · ", Levels) + "  (Objektname in " + NameLang + ")";

    /// <summary>Null = in Ordnung, sonst die Fehlermeldung. Die Namen landen in PowerShell-Texten, XML-Attributen
    /// und Ordnernamen – deshalb sind Anführungszeichen, $, Backtick, XML-Sonderzeichen und Pfadzeichen tabu.</summary>
    public string? Validate()
    {
        var labels = new[] { "Sachgebiet", "Hauptgruppe", "Gruppe", "Untergruppe", "Begriff", "Begriff DE", "Begriff EN", "Begriff FR", "Begriff IT" };
        var values = All.ToArray();
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '"', '\'', '$', '`', '<', '>', '&', '#' }).ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(values[i])) return $"„{labels[i]}“ darf nicht leer sein.";
            if (values[i] != values[i].Trim()) return $"„{labels[i]}“: keine Leerzeichen am Anfang oder Ende.";
            if (values[i].IndexOfAny(bad) >= 0) return $"„{labels[i]}“ enthält ein nicht erlaubtes Zeichen (\" ' $ ` < > & # / \\ : * ? |).";
        }
        if (NameLang is not ("DE" or "EN" or "FR" or "IT")) return "Sprache des Objektnamens: DE, EN, FR oder IT.";
        var levels = Levels.ToArray();
        if (levels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != levels.Length)
            return "Sachgebiet, Hauptgruppe, Gruppe, Untergruppe und Begriff müssen unterschiedlich heißen.";
        var langs = new[] { TermDE, TermEN, TermFR, TermIT };
        if (langs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != langs.Length)
            return "Die vier Begriff-Sprachen müssen unterschiedlich heißen.";
        return null;
    }
}
