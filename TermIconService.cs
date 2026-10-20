using System.Text.RegularExpressions;

namespace AIT.TermCatalog;

/// <summary>
/// Menü-Symbole der Katalog-Ebenen im Data Standard: kopiert die Symbole nach Vault.Custom und trägt sie in MenuDefinitions.xml
/// bei „Neues/… bearbeiten“ der vier Ebenen ein (statt des gemeinsamen Standard-Symbols). Idempotent.
/// </summary>
public static class TermIconService
{
    private const string MenuRel = @"Vault.Custom\MenuDefinitions.xml";
    private const string IconDir = "Vault.Custom";

    /// <summary>Menüelement → Symboldatei.</summary>
    private static readonly (string Element, string File)[] MenuMap =
    {
        ("newSegmentObject", "Ebene1-add.ico"),   ("editSegmentObject", "Ebene1-edit.ico"),
        ("newMainGroupObject", "Ebene2-add.ico"), ("editMainGroupObject", "Ebene2-edit.ico"),
        ("newGroupObject", "Ebene3-add.ico"),     ("editGroupObject", "Ebene3-edit.ico"),
        ("newSubGroupObject", "Ebene4-add.ico"),  ("editSubGroupObject", "Ebene4-edit.ico"),
    };

    /// <summary>Setzt in MenuDefinitions.xml das Symbol der acht Ebenen-Menüs (Image="…"). Andere Einträge bleiben unberührt.</summary>
    public static string PatchMenuDefinitions(string text)
    {
        foreach (var (element, file) in MenuMap)
            text = Regex.Replace(text, @"(?s)(<" + element + @"\b[^>]*?\bImage\s*=\s*"")[^""]*("")", "${1}" + file + "${2}");
        return text;
    }

    /// <summary>Kopiert die Symbole und passt MenuDefinitions.xml an (Vorschau mit <paramref name="dryRun"/>).</summary>
    public static List<ChangeResult> ApplyMenuIcons(string ds, bool dryRun)
    {
        var results = new List<ChangeResult>();
        var dir = Path.Combine(ds, IconDir);
        if (!Directory.Exists(dir)) return results;   // Data Standard ohne Vault.Custom: nichts zu tun

        // 1) Symboldateien
        var pending = new List<(string Name, byte[] Data)>();
        var total = 0;
        foreach (var (name, data) in TermIcons.All())
        {
            total++;
            var target = Path.Combine(dir, name);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(data)) pending.Add((name, data));
        }
        var rel = Path.Combine(IconDir, "Ebene*.ico");
        if (total == 0) results.Add(new ChangeResult(rel, "Katalog-Symbole: in der Bibliothek nicht enthalten", false, "WARNUNG: Ressourcen fehlen"));
        else if (pending.Count == 0) results.Add(new ChangeResult(rel, "Katalog-Symbole (Menüs): bereits aktuell", true, null));
        else if (dryRun) results.Add(new ChangeResult(rel, $"Katalog-Symbole (Menüs): {pending.Count} von {total} Dateien werden bereitgestellt (Vorschau)", true, "Vorschau – würde ausgeführt"));
        else
        {
            try
            {
                foreach (var (name, data) in pending)
                {
                    var target = Path.Combine(dir, name);
                    try { File.WriteAllBytes(target, data); }
                    catch (UnauthorizedAccessException)
                    {
                        if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                        File.WriteAllBytes(target, data);
                    }
                }
                results.Add(new ChangeResult(rel, $"Katalog-Symbole (Menüs): {pending.Count} von {total} Dateien bereitgestellt", true, null));
            }
            catch (Exception ex) { results.Add(new ChangeResult(rel, "Katalog-Symbole (Menüs): Bereitstellen fehlgeschlagen", false, ex.Message)); }
        }

        // 2) Menüeinträge
        results.Add(TermCatalogNamesService.PatchFile(ds, MenuRel, "Katalog-Symbole in den Menüs", PatchMenuDefinitions, dryRun));
        return results;
    }
}