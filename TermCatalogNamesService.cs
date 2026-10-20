using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;


namespace AIT.TermCatalog;

/// <summary>
/// Passt die Namen des Benennungskatalogs (Sachgebiet, Hauptgruppe, Gruppe, Untergruppe, Begriff, Begriff DE/EN/FR/IT)
/// im DataStandard an die Namen in Vault an. Der DataStandard findet die Custom Objects über den ANZEIGENAMEN
/// (Einzahl) – er steht als fester Text in MenuDefinitions.xml, den Konfigurationsordnern, im psm1, im Vault-Skript
/// und in den Datenblättern. Zusätzlich wird die Begriffssuche im psm1 repariert: sie schlug die Eigenschaft
/// "Category Name" über den englischen Anzeigenamen nach und fand in einem deutschen Vault nichts
/// (dort heißt sie "Kategoriename") – jetzt über den sprachunabhängigen Systemnamen CategoryName.
///
/// Idempotent und wiederholbar: ersetzt werden die englischen Originalnamen UND die zuletzt angewendeten Namen
/// (Datei Vault.Custom\AIT.TermNames.json) – so lässt sich auch von Deutsch auf Eigene Namen umstellen.
/// </summary>
public static class TermCatalogNamesService
{
    public const string StateFileName = "AIT.TermNames.json";

    private const string ConfigDir   = @"Vault.Custom\Configuration";
    private const string MenuRel     = @"Vault.Custom\MenuDefinitions.xml";
    private const string Psm1Rel     = @"Vault.Custom\addinVault\ADSK.QS.CustomObjectsClassified.psm1";
    private const string VaultPs1Rel = @"Vault.Custom\addinVault\AIT.PDMC-Vault.ps1";

    // Systemnamen der Ebenen-Eigenschaften im Köchl-Sample (Datenblätter, Vault-Skript, Bearbeiten-Modus).
    // Per API angelegte Eigenschaften haben GUID-Systemnamen – dort werden diese Schlüssel durch den Anzeigenamen ersetzt.
    private static readonly string[] LegacyLevelKeys = { "_XLTN_SEGMENT", "_XLTN_MAINGROUP", "_XLTN_GROUP", "_XLTN_SUBGROUP" };

    private sealed class AppliedState
    {
        public TermCatalogNames? Vault { get; set; }   // Menüs, Ordner, Vault-Skript, Datenblätter
        public TermCatalogNames? Psm1  { get; set; }   // psm1 (gemeinsam für Vault und CAD)
    }

    // ── Zustand ──────────────────────────────────────────────────────────────

    private static string StatePath(string ds) => Path.Combine(ds, "Vault.Custom", StateFileName);

    private static AppliedState LoadState(string ds)
    {
        try
        {
            var p = StatePath(ds);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<AppliedState>(File.ReadAllText(p, Encoding.UTF8)) ?? new AppliedState();
        }
        catch { /* beschädigt → wie "noch nichts angewendet" */ }
        return new AppliedState();
    }

    private static void SaveState(string ds, AppliedState st)
        => FileIo.WriteTextRobust(StatePath(ds),
            JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));

    /// <summary>Die zuletzt angewendeten Namen (Vault-Teil bzw. psm1), null = noch nicht angewendet.</summary>
    public static TermCatalogNames? LoadApplied(string ds, bool vaultPart)
    {
        var st = LoadState(ds);
        return vaultPart ? st.Vault : st.Psm1;
    }

    // ── Hilfen ───────────────────────────────────────────────────────────────

    /// <summary>Alte Schreibweisen → neuer Name, für die Schlüssel mit den angegebenen Indizes
    /// (0 Sachgebiet, 1 Hauptgruppe, 2 Gruppe, 3 Untergruppe, 4 Begriff, 5–8 Begriff DE/EN/FR/IT).
    /// Alt = englisches Original und die zuletzt angewendeten Namen.</summary>
    private static Dictionary<string, string> BuildMap(int[] idx, TermCatalogNames target, TermCatalogNames? prev, bool withLegacyKeys = false)
    {
        var eng = TermCatalogNames.English.All.ToArray();
        var tgt = target.All.ToArray();
        var old = prev?.All.ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var i in idx)
        {
            foreach (var o in new[] { eng[i], old?[i] })
                if (!string.IsNullOrEmpty(o) && o != tgt[i] && !map.ContainsKey(o)) map[o] = tgt[i];
            if (withLegacyKeys && i < LegacyLevelKeys.Length && !map.ContainsKey(LegacyLevelKeys[i]))
                map[LegacyLevelKeys[i]] = tgt[i];
        }
        return map;
    }

    /// <summary>Ersetzt "alt" durch "neu" (in Anführungszeichen), optional nur hinter einem Vorspann / vor einem Nachspann.</summary>
    private static string ReplaceQuoted(string text, Dictionary<string, string> map, string prefixPattern = "", string suffixPattern = "")
    {
        if (map.Count == 0) return text;
        var alt = string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        var rx = new Regex($"({prefixPattern})\"({alt})\"({suffixPattern})");
        return rx.Replace(text, m => m.Groups[1].Value + "\"" + map[m.Groups[2].Value] + "\"" + m.Groups[3].Value);
    }

    private static string Backup(string file)
    {
        var zip = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileName(file)}_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.zip");
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        z.CreateEntryFromFile(file, Path.GetFileName(file));
        return zip;
    }

    internal static ChangeResult PatchFile(string ds, string rel, string what, Func<string, string> transform, bool dryRun)
    {
        var path = Path.Combine(ds, rel);
        if (!File.Exists(path)) return new ChangeResult(rel, $"{what}: Datei nicht vorhanden (übersprungen)", true, null);
        try
        {
            var raw = File.ReadAllBytes(path);
            var hadBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var text = File.ReadAllText(path, Encoding.UTF8);
            var nt = transform(text);
            if (nt == text) return new ChangeResult(rel, $"{what}: bereits aktuell", true, null);
            if (dryRun) return new ChangeResult(rel, $"{what}: wird angepasst (Vorschau)", true, "Vorschau – würde ausgeführt");
            var zip = Backup(path);
            // BOM nur behalten bzw. setzen, wenn nötig (Umlaute in eigenen Namen brauchen es für PowerShell 5.1)
            FileIo.WriteTextRobust(path, nt, new UTF8Encoding(hadBom || nt.Any(c => c > 127)));
            return new ChangeResult(rel, $"{what}: angepasst (ZIP-Backup erstellt)", true, zip);
        }
        catch (Exception ex) { return new ChangeResult(rel, $"{what}: Anpassen fehlgeschlagen", false, ex.Message); }
    }

    // ── Konfigurationsordner ─────────────────────────────────────────────────

    private static readonly string[] FolderIndexNames = { "Segment", "Main Group", "Group", "Sub Group", "Term" };

    /// <summary>Ordnername (Konfigurationsordner der Definition) für einen Namen – leer, wenn es kein Ebenen-/Objektname ist.</summary>
    public static string FolderFor(TermCatalogNames names, string englishFolder)
    {
        var i = Array.IndexOf(FolderIndexNames, englishFolder);
        return i < 0 ? englishFolder : names.All.ElementAt(i);
    }

    /// <summary>Benennt die Konfigurationsordner (Segment, Main Group, …) auf die gewünschten Namen um. Vorhandene
    /// Zielordner bleiben unangetastet; ein Ordner wird nur verschoben, wenn der Zielname noch nicht existiert.</summary>
    public static List<ChangeResult> RenameFolders(string ds, TermCatalogNames target, bool dryRun)
    {
        var results = new List<ChangeResult>();
        var cfg = Path.Combine(ds, ConfigDir);
        if (!Directory.Exists(cfg)) return results;
        var prev = LoadState(ds).Vault;
        var tgt = target.All.ToArray();

        for (var i = 0; i < FolderIndexNames.Length; i++)
        {
            var targetDir = Path.Combine(cfg, tgt[i]);
            var candidates = new[] { FolderIndexNames[i], prev?.All.ElementAt(i) }
                .Where(c => !string.IsNullOrEmpty(c) && !string.Equals(c, tgt[i], StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
            var rel = Path.Combine(ConfigDir, tgt[i]);
            var existingOld = candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(cfg, c!)));

            if (Directory.Exists(targetDir))
            {
                if (existingOld != null)
                    results.Add(new ChangeResult(rel, $"Ordner „{existingOld}“ und „{tgt[i]}“ sind beide vorhanden – bitte prüfen, welcher gilt", false,
                        "WARNUNG: nicht automatisch zusammengeführt"));
                continue;
            }
            if (existingOld == null) continue;   // nichts da (z. B. Katalog noch nicht bereitgestellt)
            if (dryRun)
            { results.Add(new ChangeResult(rel, $"Ordner „{existingOld}“ → „{tgt[i]}“ wird umbenannt (Vorschau)", true, "Vorschau – würde ausgeführt")); continue; }
            try
            {
                Directory.Move(Path.Combine(cfg, existingOld!), targetDir);
                results.Add(new ChangeResult(rel, $"Ordner „{existingOld}“ → „{tgt[i]}“ umbenannt", true, null));
            }
            catch (Exception ex) { results.Add(new ChangeResult(rel, $"Ordner „{existingOld}“ umbenennen fehlgeschlagen", false, ex.Message)); }
        }
        return results;
    }

    // ── Vault-Teil ───────────────────────────────────────────────────────────

    /// <summary>Menüs, Vault-Skript, psm1 und Datenblätter auf die Namen umstellen (Ordner: <see cref="RenameFolders"/>).</summary>
    public static List<ChangeResult> ApplyVault(string ds, TermCatalogNames target, bool dryRun, TermCopyConfig copy)
    {
        var results = new List<ChangeResult>();
        var st = LoadState(ds);
        var levels = new[] { 0, 1, 2, 3, 4 };

        // MenuDefinitions.xml: NavigationTypes="X" / Location="X"
        var menuMap = BuildMap(levels, target, st.Vault);
        results.Add(PatchFile(ds, MenuRel, "Katalognamen in Menüs", t =>
            ReplaceQuoted(t, menuMap, @"(?:NavigationTypes|Location)\s*=\s*", "")  , dryRun));

        // AIT.PDMC-Vault.ps1: gezielt (Kategorie "Term", Eigenschaft Begriff EN, Ebenen im Bearbeiten-Modus)
        var termMap  = BuildMap(new[] { 4 }, target, st.Vault);
        var propMap  = BuildMap(new[] { 0, 1, 2, 3 }, target, st.Vault, withLegacyKeys: true);
        // Objektname ↔ Begriffs-Eigenschaft: das Skript hält den Namen eines neuen Begriffs mit EINER Sprach-Eigenschaft synchron
        // (Original: Term EN). Alle Sprach-Namen (englisches Original + zuletzt angewendete) werden auf die gewählte Namenssprache gesetzt.
        var nameProp = target.NameProperty;
        foreach (var o in TermCatalogNames.English.All.Skip(5).Concat(st.Vault?.All.Skip(5) ?? Enumerable.Empty<string>()))
            if (!string.IsNullOrEmpty(o) && o != nameProp && !propMap.ContainsKey(o)) propMap[o] = nameProp;
        var segMap   = BuildMap(new[] { 0 }, target, st.Vault);
        results.Add(PatchFile(ds, VaultPs1Rel, "Katalognamen im Vault-Skript", t =>
        {
            t = ReplaceQuoted(t, termMap, @"_Category""\]\.Value\s+-eq\s+");
            t = ReplaceQuoted(t, propMap, @"\$Prop\[", @"\]");
            t = ReplaceQuoted(t, segMap, @"-_CoName\s+");
            return t;
        }, dryRun));

        // psm1 (gemeinsam mit CAD)
        results.AddRange(ApplyPsm1(ds, target, dryRun, st, copy));
        results.Add(PatchFile(ds, @"Vault.Custom\Configuration\AIT.File.xaml", "Terms-Maske: Titel-Sprache (Radio) und Vorschlagsliste", MaskTransform, dryRun));

        // Datenblätter der Definitionen (Prop[_XLTN_SEGMENT] → Prop[<Anzeigename>])
        results.AddRange(PatchDatasheets(ds, target, st.Vault, dryRun));

        if (!dryRun && results.All(r => r.Applied))
        {
            st = LoadState(ds);
            st.Vault = target.Clone(); st.Psm1 = target.Clone();
            try { SaveState(ds, st); } catch (Exception ex) { results.Add(new ChangeResult(Path.Combine("Vault.Custom", StateFileName), "Namensstand speichern fehlgeschlagen", false, ex.Message)); }
        }
        return results;
    }

    /// <summary>CAD-Teil: nur das gemeinsame psm1 (Begriffssuche in Inventor/AutoCAD/Vault).</summary>
    public static List<ChangeResult> ApplyCad(string ds, TermCatalogNames target, bool dryRun, TermCopyConfig copy)
    {
        var st = LoadState(ds);
        var results = ApplyPsm1(ds, target, dryRun, st, copy);
        results.Add(PatchFile(ds, @"CAD.Custom\Configuration\Inventor.xaml", "Terms-Maske: Titel-Sprache (Radio) und Vorschlagsliste", MaskTransform, dryRun));
        if (!dryRun && results.All(r => r.Applied))
        {
            st = LoadState(ds); st.Psm1 = target.Clone();
            try { SaveState(ds, st); } catch (Exception ex) { results.Add(new ChangeResult(Path.Combine("Vault.Custom", StateFileName), "Namensstand speichern fehlgeschlagen", false, ex.Message)); }
        }
        return results;
    }

    /// <summary>Änderungen an der Terms-Maske (Inventor.xaml, AIT.File.xaml): Radio-Buttons "Titel in:" und Vorschlagsliste.</summary>
    private static string MaskTransform(string xaml) => TermSuggestService.TransformMask(TermCopyService.TransformMask(xaml));

    private const string CatNameOld  = "mCreateClsSearchCond \"Category Name\"";
    private const string CatNameNew  = "mCreateClsSearchCond \"CategoryName\"";
    private const string LookupOld   = "$propDef = $propDefs | Where-Object { $_.dispName -eq $name }";
    private const string LookupNew   = "$propDef = $propDefs | Where-Object { $_.dispName -eq $name -or $_.SysName -eq $name } # AIT: auch per Systemname (z. B. CategoryName) – sprachunabhängig";

    private static List<ChangeResult> ApplyPsm1(string ds, TermCatalogNames target, bool dryRun, AppliedState st, TermCopyConfig copy)
    {
        var map = BuildMap(Enumerable.Range(0, 9).ToArray(), target, st.Psm1);
        var r = PatchFile(ds, Psm1Rel, "Begriffssuche und Übernehmen (Namen, Kategoriename über Systemname, Kopier-Zuordnung)", t =>
        {
            t = ReplaceQuoted(t, map);
            t = t.Replace(CatNameOld, CatNameNew);
            if (!t.Contains("-or $_.SysName -eq $name", StringComparison.Ordinal)) t = t.Replace(LookupOld, LookupNew);
            return TermSuggestService.TransformPsm1(TermCopyService.TransformPsm1(t, copy), target, copy.Autocomplete);
        }, dryRun);
        return new List<ChangeResult> { r };
    }

    private static List<ChangeResult> PatchDatasheets(string ds, TermCatalogNames target, TermCatalogNames? prev, bool dryRun)
    {
        var results = new List<ChangeResult>();
        var sawUnchanged = false;
        var map = BuildMap(new[] { 0, 1, 2, 3 }, target, prev, withLegacyKeys: true);
        if (map.Count == 0) return results;
        var alt = string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        var rx = new Regex($@"Prop\[({alt})\]");
        foreach (var name in target.Levels)
        {
            var dir = Path.Combine(ds, ConfigDir, name);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.xaml"))
            {
                var rel = Path.Combine(ConfigDir, name, Path.GetFileName(file));
                var r = PatchFile(ds, rel, "Datenblatt-Bindungen", t => rx.Replace(t, m => $"Prop[{map[m.Groups[1].Value]}]"), dryRun);
                if (!r.Applied || r.Detail != null) results.Add(r);   // unveränderte Datenblätter nicht einzeln aufführen
                else sawUnchanged = true;
            }
        }
        if (results.Count == 0 && sawUnchanged)
            results.Add(new ChangeResult(ConfigDir, "Datenblatt-Bindungen: bereits aktuell", true, null));
        return results;
    }
}
