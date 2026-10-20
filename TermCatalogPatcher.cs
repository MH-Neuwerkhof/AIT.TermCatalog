using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AIT.TermCatalog;

/// <summary>
/// Benennungskatalog (Terms) im Data Standard beibehalten / wiederherstellen / anpassen: Term- und Katalogebenen-Menüs
/// (MenuDefinitions.xml), "..."-Buttons am Titel (Inventor.xaml, AIT.File.xaml), PS-Module und Masken, Namenstabelle und
/// Kopier-Zuordnung. NICHT dazu gehört die Klassifizierung von Dateien. Alle Schritte sind idempotent, legen vor jeder Änderung
/// ein ZIP-Backup an und kennen eine Vorschau (dryRun).
///
/// "Originale" (exchangeFolder): Ordner mit dem unveränderten PDMC-Stand; daraus werden fehlende Teile wiederhergestellt.
/// Fehlt der Ordner, wird nur gemeldet, was fehlt.
/// </summary>
public static class TermCatalogPatcher
{
    private const string AitPdmcVaultPs1Name = "AIT.PDMC-Vault.ps1";
    private const string OrigFileXamlName    = "ADSK.QS.File.xaml";
    private const string AitFileXamlName     = "AIT.File.xaml";
    private const string DefaultPs1SubPath   = @"Vault.Custom\addinVault";
    private const string MenuDefinitionsSubPath = @"Vault.Custom";
    private const string MenuDefinitionsName    = "MenuDefinitions.xml";
    private const string InventorXamlSubPath   = @"CAD.Custom\Configuration";
    private const string InventorXamlName      = "Inventor.xaml";
    private const string TermButtonCommand     = "mInitializeTermCatalog";

    private static readonly Regex RxTitleBtnTrigger = new(
        @"[ \t]*<DataTrigger Binding=""\{Binding Name\}"" Value=""Title"">[\s\S]*?</DataTrigger>[ \t]*\r?\n?",
        RegexOptions.Compiled);

    // Dateien, die für den Benennungskatalog vorhanden sein müssen (werden vom Tool nie entfernt —
    // bei Bedarf aus dem Austauschordner nachgeliefert).
    private static readonly string[] TermCatalogRequiredFiles =
    {
        @"Vault.Custom\addinVault\ADSK.QS.CustomObjectsClassified.psm1",
        @"Vault.Custom\addinVault\Menus\ADSK.QS.CreateCustomObjectClassified.ps1",
        @"Vault.Custom\addinVault\Menus\ADSK.QS.EditCustomObjectClassified.ps1",
        @"Vault.Custom\Configuration\ADSK.QS.CustomObjectClassified.xaml",
        @"Vault.Custom\Configuration\Term\Adsk.QS.ClassifiedObject.DataSheet.xaml",
    };

    /// <summary>True, wenn die Installation bereits vorbereitet ist (AIT.PDMC-Vault.ps1) UND die Term-Menüs
    /// in MenuDefinitions.xml sowie die Titel-"…"-Buttons in AIT.File.xaml vorhanden sind (Vault-Teil;
    /// der Inventor-Teil wird über <see cref="CadDetectionResult.TermCatalogEnabled"/> erkannt).</summary>
    public static bool IsPresent(string dsPath)
    {
        try
        {
            if (!File.Exists(Path.Combine(dsPath, DefaultPs1SubPath, AitPdmcVaultPs1Name))) return false;

            var menuPath = Path.Combine(dsPath, MenuDefinitionsSubPath, MenuDefinitionsName);
            if (!File.Exists(menuPath) ||
                !Regex.IsMatch(File.ReadAllText(menuPath, Encoding.UTF8), @"<newTermObject\b"))
                return false;

            var fileXaml = Path.Combine(dsPath, VaultFileXamlSubPath, AitFileXamlName);
            return File.Exists(fileXaml) &&
                   File.ReadAllText(fileXaml, Encoding.UTF8).Contains(TermButtonCommand, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Stellt die Bausteine des Benennungskatalogs aus dem (unveränderten) Austauschordner wieder
    /// her, falls sie bei einer früheren Vorbereitung entfernt wurden — jeweils nur, was fehlt, mit
    /// ZIP-Backup der geänderten Dateien. Idempotent. Gedacht für die Option "Benennungskatalog aktivieren".</summary>
    public static List<ChangeResult> RunVault(string dsPath, string exchangeFolder, bool dryRun, TermCatalogNames names, TermCopyConfig copy,
        Func<string, string>? menuRestoreTransform = null)
    {
        var results = RunTermCatalogCore(dsPath, exchangeFolder, dryRun, names, menuRestoreTransform);
        results.AddRange(RestoreVaultTitleButtons(dsPath, exchangeFolder, dryRun));
        results.AddRange(TermCatalogNamesService.ApplyVault(dsPath, names, dryRun, copy));
        results.AddRange(PolishTermSearchMask(dsPath, dryRun, Path.Combine(VaultFileXamlSubPath, AitFileXamlName)));
        results.AddRange(TermIconService.ApplyMenuIcons(dsPath, dryRun));
        return results;
    }

    private const string TermSearchHintDe =
        "Suchtext in der Begriffsdefinition (mit * für Teilbegriffe); unten weitere Sprachen wählen, um die Suche zu erweitern";

    /// <summary>Feinschliff der Terms-Maske in Inventor: deutscher Hinweistext (UIStrings ClassTerms_16 ist im
    /// de-DE-Original noch englisch), Abstand zwischen Gruppenüberschrift und Suchfeld, deutscher Titel.
    /// Idempotent, mit ZIP-Backup.</summary>
    private static List<ChangeResult> PolishTermSearchMask(string dsPath, bool dryRun, string xamlRel)
    {
        var results = new List<ChangeResult>();
        static string ZipFor(string file) =>
            Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileName(file)}_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.zip");
        static void Backup(string file, string zipName)
        {
            using var zip = ZipFile.Open(zipName, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(file, Path.GetFileName(file));
        }

        // a) de-DE\UIStrings.xml – nur ersetzen, solange noch der englische Originaltext drinsteht
        var uiPath = Path.Combine(dsPath, "de-DE", "UIStrings.xml");
        var uiRel  = Path.Combine("de-DE", "UIStrings.xml");
        if (File.Exists(uiPath))
        {
            try
            {
                var ui = File.ReadAllText(uiPath, Encoding.UTF8);
                var rx = new Regex(@"(<UIString ID=""ClassTerms_16"">)Search text to match term definition[^<]*(</UIString>)");
                if (rx.IsMatch(ui))
                {
                    if (dryRun)
                        results.Add(new ChangeResult(uiRel, "Terms-Maske: Hinweistext wird eingedeutscht (Vorschau)", true, "Vorschau – würde ausgeführt"));
                    else
                    {
                        var zip = ZipFor(uiPath);
                        Backup(uiPath, zip);
                        FileIo.WriteTextRobust(uiPath, rx.Replace(ui, "$1" + TermSearchHintDe + "$2"), new UTF8Encoding(false));
                        results.Add(new ChangeResult(uiRel, "Terms-Maske: Hinweistext eingedeutscht (ZIP-Backup erstellt)", true, zip));
                    }
                }
            }
            catch (Exception ex) { results.Add(new ChangeResult(uiRel, "Terms-Maske: UIStrings anpassen fehlgeschlagen", false, ex.Message)); }
        }

        // b) Inventor.xaml und Vault-Datei-Maske (AIT.File.xaml) – Abstand + deutscher Titel
        foreach (var invRel in new[] { xamlRel })
        {
            var invPath = Path.Combine(dsPath, invRel);
            if (!File.Exists(invPath)) continue;
            try
            {
                var xaml = File.ReadAllText(invPath, Encoding.UTF8);
                var text = xaml;
                text = text.Replace(@"<Grid x:Name=""grdTermSearch"" Width=""562"" HorizontalAlignment=""Left"">",
                                    @"<Grid x:Name=""grdTermSearch"" Width=""562"" HorizontalAlignment=""Left"" Margin=""0,6,0,3"">");
                text = text.Replace(@"Text=""{Binding UIString[], FallbackValue='Terms Master'}""",
                                    @"Text=""{Binding UIString[ClassTerms_01], FallbackValue='Benennungskatalog'}""");
                if (text == xaml) continue;
                if (dryRun)
                    results.Add(new ChangeResult(invRel, "Terms-Maske: Abstand Suchfeld und deutscher Titel (Vorschau)", true, "Vorschau – würde ausgeführt"));
                else
                {
                    var zip = ZipFor(invPath);
                    Backup(invPath, zip);
                    FileIo.WriteTextRobust(invPath, text, Encoding.UTF8);
                    results.Add(new ChangeResult(invRel, "Terms-Maske: Abstand beim Suchfeld und deutscher Titel angepasst (ZIP-Backup erstellt)", true, zip));
                }
            }
            catch (Exception ex) { results.Add(new ChangeResult(invRel, "Terms-Maske: XAML anpassen fehlgeschlagen", false, ex.Message)); }
        }
        return results;
    }

    private const string VaultFileXamlSubPath = @"Vault.Custom\Configuration";
    private const string DynBtnCellStyleKey   = "x:Key=\"DynamicDataGridBtnCellStyle\"";
    private static readonly Regex RxTitleTriggers = new(
        @"[ \t]*<DataTrigger Binding=""\{Binding Name\}"" Value=""(?:Title|Titel)"">[\s\S]*?</DataTrigger>[ \t]*\r?\n?",
        RegexOptions.Compiled);

    /// <summary>Vault-Datei-Maske (AIT.File.xaml): "…"-Buttons an Title/Titel (öffnen die Terms-Suche) aus der
    /// Original-ADSK.QS.File.xaml im Austauschordner wieder einsetzen, falls sie fehlen. Idempotent, mit ZIP-Backup.</summary>
    private static List<ChangeResult> RestoreVaultTitleButtons(string dsPath, string exchangeFolder, bool dryRun)
    {
        var results = new List<ChangeResult>();
        var rel    = Path.Combine(VaultFileXamlSubPath, AitFileXamlName);
        var target = Path.Combine(dsPath, rel);
        if (!File.Exists(target)) return results;   // noch nicht vorbereitet
        try
        {
            var text = File.ReadAllText(target, Encoding.UTF8);
            if (text.Contains(TermButtonCommand, StringComparison.Ordinal))
            {
                results.Add(new ChangeResult(rel, $"{AitFileXamlName} – Titel-Buttons (Terms-Suche) vorhanden", true, null));
                return results;
            }
            var source = string.IsNullOrWhiteSpace(exchangeFolder)
                ? string.Empty : Path.Combine(exchangeFolder, VaultFileXamlSubPath, OrigFileXamlName);
            if (!File.Exists(source))
            {
                results.Add(new ChangeResult(rel, "Titel-Buttons fehlen, Original-ADSK.QS.File.xaml im Austauschordner nicht gefunden", false,
                    "WARNUNG: Austauschordner (PDMC-Original) prüfen – dort wird die Datei zur Wiederherstellung gelesen"));
                return results;
            }
            var blocks = RxTitleTriggers.Matches(File.ReadAllText(source, Encoding.UTF8))
                .Cast<Match>().Where(m => m.Value.Contains(TermButtonCommand, StringComparison.Ordinal))
                .Select(m => m.Value.EndsWith('\n') ? m.Value : m.Value + "\r\n").ToList();
            var styleIdx = text.IndexOf(DynBtnCellStyleKey, StringComparison.Ordinal);
            var endIdx   = styleIdx < 0 ? -1 : text.IndexOf("</Style.Triggers>", styleIdx, StringComparison.Ordinal);
            if (blocks.Count == 0 || endIdx < 0)
            {
                results.Add(new ChangeResult(rel, "Titel-Buttons: Original-Block oder Einfügestelle nicht gefunden – manuell prüfen", false,
                    "WARNUNG: XAML-Struktur nicht erkannt"));
                return results;
            }
            if (dryRun)
            {
                results.Add(new ChangeResult(rel, "Titel-\"…\"-Buttons (Terms-Suche) werden wieder eingefügt (Vorschau)", true, "Vorschau – würde ausgeführt"));
                return results;
            }
            var zip = Path.Combine(Path.GetDirectoryName(target)!, $"{AitFileXamlName}_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntryFromFile(target, AitFileXamlName);
            var lineStart = text.LastIndexOf('\n', endIdx) + 1;
            var insertAt  = text.Substring(lineStart, endIdx - lineStart).Trim().Length == 0 ? lineStart : endIdx;
            var insert    = string.Concat(blocks) + (insertAt == endIdx ? "\r\n" : "");
            FileIo.WriteTextRobust(target, text.Insert(insertAt, insert), Encoding.UTF8);
            results.Add(new ChangeResult(rel, "Titel-\"…\"-Buttons (Terms-Suche) wieder eingefügt (ZIP-Backup erstellt)", true, zip));
        }
        catch (Exception ex) { results.Add(new ChangeResult(rel, "Titel-Buttons einfügen fehlgeschlagen", false, ex.Message)); }
        return results;
    }

    /// <summary>Pfad einer benötigten Datei mit dem aktuellen Namen des Term-Konfigurationsordners
    /// (Original: Vault.Custom\Configuration\Term\...).</summary>
    private static string MapTermFolder(string rel, TermCatalogNames names)
    {
        const string prefix = @"Vault.Custom\Configuration\Term\";
        return rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? @"Vault.Custom\Configuration\" + names.Term + "\\" + rel.Substring(prefix.Length)
            : rel;
    }

    private static List<ChangeResult> RunTermCatalogCore(string dsPath, string exchangeFolder, bool dryRun, TermCatalogNames names, Func<string, string>? menuRestoreTransform)
    {
        var results = new List<ChangeResult>();
        var srcOk = !string.IsNullOrWhiteSpace(exchangeFolder) && Directory.Exists(exchangeFolder);
        const string needSrc = "WARNUNG: Austauschordner (PDMC-Original) prüfen – dort wird die Datei zur Wiederherstellung gelesen";

        static void Backup(string file, string zipName)
        {
            if (!File.Exists(file)) return;
            using var zip = ZipFile.Open(zipName, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(file, Path.GetFileName(file));
        }
        static string ZipFor(string file) =>
            Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileName(file)}_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.zip");

        // 0) Konfigurationsordner auf die gewählten Namen umbenennen (vor dem Prüfen der benötigten Dateien)
        results.AddRange(TermCatalogNamesService.RenameFolders(dsPath, names, dryRun));

        // 1) Benötigte Dateien (Module, Menü-PS1, Term-Fenster, Datenblatt)
        foreach (var rel in TermCatalogRequiredFiles)
        {
            var target = Path.Combine(dsPath, MapTermFolder(rel, names));
            var source = srcOk ? Path.Combine(exchangeFolder, rel) : string.Empty;   // Original = englische Ordnernamen
            // Vorschau benennt Ordner nicht um: dort zählt auch die Datei im alten Ordner als vorhanden
            if (File.Exists(target) || (dryRun && File.Exists(Path.Combine(dsPath, rel)))) continue;
            if (!File.Exists(source))
            { results.Add(new ChangeResult(rel, $"{Path.GetFileName(rel)} fehlt und im Austauschordner nicht gefunden", false, needSrc)); continue; }
            if (dryRun)
            { results.Add(new ChangeResult(rel, $"{Path.GetFileName(rel)} wird aus dem Austauschordner bereitgestellt (Vorschau)", true, "Vorschau – würde ausgeführt")); continue; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: false);
                results.Add(new ChangeResult(rel, $"{Path.GetFileName(rel)} bereitgestellt", true, null));
            }
            catch (Exception ex) { results.Add(new ChangeResult(rel, $"{Path.GetFileName(rel)} – Bereitstellen fehlgeschlagen", false, ex.Message)); }
        }

        // 2) Menüs (MenuDefinitions.xml): Original einlesen, ECO/Aufgaben/Klassen-Befehle wieder entfernen
        var menuTarget = Path.Combine(dsPath, MenuDefinitionsSubPath, MenuDefinitionsName);
        var menuSource = srcOk ? Path.Combine(exchangeFolder, MenuDefinitionsSubPath, MenuDefinitionsName) : string.Empty;
        var menuRel    = Path.Combine(MenuDefinitionsSubPath, MenuDefinitionsName);
        var menuText   = File.Exists(menuTarget) ? File.ReadAllText(menuTarget, Encoding.UTF8) : string.Empty;
        if (Regex.IsMatch(menuText, @"<newTermObject\b"))
            results.Add(new ChangeResult(menuRel, $"{MenuDefinitionsName} – Katalog-Menüs (Terme/Ebenen) vorhanden", true, null));
        else if (menuRestoreTransform == null)
            results.Add(new ChangeResult(menuRel, $"{MenuDefinitionsName}: Katalog-Menüs fehlen - automatisches Wiederherstellen ist hier nicht vorgesehen", false,
                "WARNUNG: Menüs (newTermObject usw.) manuell aus dem Original übernehmen"));
        else if (!File.Exists(menuSource))
            results.Add(new ChangeResult(menuRel, $"{MenuDefinitionsName}: Katalog-Menüs fehlen, Original im Austauschordner nicht gefunden", false, needSrc));
        else if (dryRun)
            results.Add(new ChangeResult(menuRel, $"{MenuDefinitionsName}: Katalog-Menüs werden aus dem Original wiederhergestellt (Vorschau)", true, "Vorschau – würde ausgeführt"));
        else
        {
            try
            {
                var restored = menuRestoreTransform(File.ReadAllText(menuSource, Encoding.UTF8));
                var zip = ZipFor(menuTarget);
                Backup(menuTarget, zip);
                FileIo.WriteTextRobust(menuTarget, restored, Encoding.UTF8);
                results.Add(new ChangeResult(menuRel, $"{MenuDefinitionsName}: Katalog-Menüs wiederhergestellt (ZIP-Backup erstellt)", true, zip));
            }
            catch (Exception ex) { results.Add(new ChangeResult(menuRel, $"{MenuDefinitionsName} – Wiederherstellung fehlgeschlagen", false, ex.Message)); }
        }

        return results;
    }

    /// <summary>CAD-Teil des Benennungskatalogs: Titel-"…"-Button in Inventor.xaml (öffnet die Terms-Suche),
    /// aus dem Austauschordner wiederhergestellt, falls früher entfernt – plus Feinschliff der Terms-Maske
    /// (Inventor.xaml, deutscher Hinweistext). Idempotent, mit ZIP-Backup.</summary>
    public static List<ChangeResult> RunCad(string dsPath, string exchangeFolder, bool dryRun, TermCatalogNames names, TermCopyConfig copy)
    {
        var results = RestoreInventorTitleButton(dsPath, exchangeFolder, dryRun);
        results.AddRange(TermCatalogNamesService.ApplyCad(dsPath, names, dryRun, copy));
        results.AddRange(PolishTermSearchMask(dsPath, dryRun, Path.Combine(InventorXamlSubPath, InventorXamlName)));
        return results;
    }

    private static List<ChangeResult> RestoreInventorTitleButton(string dsPath, string exchangeFolder, bool dryRun)
    {
        var results = new List<ChangeResult>();
        var srcOk = !string.IsNullOrWhiteSpace(exchangeFolder) && Directory.Exists(exchangeFolder);
        const string needSrc = "WARNUNG: Austauschordner (PDMC-Original) prüfen – dort wird die Datei zur Wiederherstellung gelesen";
        static void Backup(string file, string zipName)
        {
            if (!File.Exists(file)) return;
            using var zip = ZipFile.Open(zipName, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(file, Path.GetFileName(file));
        }
        static string ZipFor(string file) =>
            Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileName(file)}_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.zip");

        var invTarget = Path.Combine(dsPath, InventorXamlSubPath, InventorXamlName);
        var invSource = srcOk ? Path.Combine(exchangeFolder, InventorXamlSubPath, InventorXamlName) : string.Empty;
        var invRel    = Path.Combine(InventorXamlSubPath, InventorXamlName);
        if (!File.Exists(invTarget))
        {
            results.Add(new ChangeResult(invRel, $"{InventorXamlName} nicht vorhanden – Titel-Button übersprungen (zuerst Austausch ausführen)", true, null));
            return results;
        }
        var invText = File.ReadAllText(invTarget, Encoding.UTF8);
        if (invText.Contains(TermButtonCommand, StringComparison.Ordinal))
        {
            results.Add(new ChangeResult(invRel, $"{InventorXamlName} – Titel-Button (Terms-Suche) vorhanden", true, null));
            return results;
        }
        if (!File.Exists(invSource))
        {
            results.Add(new ChangeResult(invRel, "Titel-Button fehlt, Original-Inventor.xaml im Austauschordner nicht gefunden", false, needSrc));
            return results;
        }
        var trig   = RxTitleBtnTrigger.Match(File.ReadAllText(invSource, Encoding.UTF8));
        var anchor = Regex.Match(invText, @"[ \t]*<DataTrigger Binding=""\{Binding Name\}"" Value=""Document Code"">");
        if (!trig.Success || !trig.Value.Contains(TermButtonCommand, StringComparison.Ordinal) || !anchor.Success)
        {
            results.Add(new ChangeResult(invRel, "Titel-Button: Original-Block oder Einfügestelle nicht gefunden – manuell prüfen", false,
                "WARNUNG: XAML-Struktur nicht erkannt"));
            return results;
        }
        if (dryRun)
        {
            results.Add(new ChangeResult(invRel, "Titel-\"…\"-Button (Terms-Suche) wird wieder eingefügt (Vorschau)", true, "Vorschau – würde ausgeführt"));
            return results;
        }
        try
        {
            var zip = ZipFor(invTarget);
            Backup(invTarget, zip);
            var block = trig.Value.EndsWith('\n') ? trig.Value : trig.Value + "\r\n";
            FileIo.WriteTextRobust(invTarget, invText.Insert(anchor.Index, block), Encoding.UTF8);
            results.Add(new ChangeResult(invRel, "Titel-\"…\"-Button (Terms-Suche) wieder eingefügt (ZIP-Backup erstellt)", true, zip));
        }
        catch (Exception ex) { results.Add(new ChangeResult(invRel, "Titel-Button einfügen fehlgeschlagen", false, ex.Message)); }
        return results;
    }

}
