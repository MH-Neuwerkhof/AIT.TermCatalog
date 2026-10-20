using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;


namespace AIT.TermCatalog;

/// <summary>Stellt die Einstellungen für das Übernehmen von Begriffen ("Kopieren" in der Terms-Maske) dar.</summary>
public class TermCopyConfig
{
    /// <summary>"DE", "EN", "FR" oder "IT": Vorbelegung der Titel-Sprache (in der Maske per Radio änderbar).</summary>
    public string TitleLang { get; set; } = "DE";
    public List<TermCopyRow> Rows { get; set; } = new();

    /// <summary>Autovervollständigung in der Begriffssuche (Vorschlagsliste beim Tippen). Standard: an.</summary>
    public bool Autocomplete { get; set; } = true;

    public static TermCopyConfig From(string titleLanguage, IEnumerable<TermCopyRow>? rows, bool autocomplete = true) => new()
    {
        Autocomplete = autocomplete,
        TitleLang = TermCopyRow.Languages.Contains(titleLanguage) ? titleLanguage : "DE",
        Rows = (rows ?? Enumerable.Empty<TermCopyRow>()).Where(r => !string.IsNullOrWhiteSpace(r.Property)).Select(r => r.Clone()).ToList(),
    };

    public string Summary
    {
        get
        {
            var n = Rows.Count;
            return $"Titel in {TitleLang} (per Auswahl in der Maske änderbar) · " +
                   (n == 0 ? "keine weiteren Eigenschaften" : $"{n} weitere Eigenschaft{(n == 1 ? "" : "en")}");
        }
    }

    /// <summary>Null = in Ordnung, sonst die Fehlermeldung.</summary>
    public string? Validate()
    {
        var bad = new[] { '\'', '"', '$', '`', '\n', '\r', '(', ')', '{', '}', ';' };
        foreach (var r in Rows)
        {
            if (string.IsNullOrWhiteSpace(r.Property)) return "Eine Zeile hat keinen Eigenschaftsnamen.";
            if (r.Property.IndexOfAny(bad) >= 0) return $"„{r.Property}“ enthält ein nicht erlaubtes Zeichen (' \" $ ` ( ) {{ }} ;).";
            if (!TermCopyRow.Languages.Contains(r.Lang)) return $"„{r.Property}“: ungültige Sprache.";
        }
        var dup = Rows.GroupBy(r => r.Property.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return dup != null ? $"„{dup.Key}“ kommt mehrfach vor." : null;
    }
}

/// <summary>
/// Ersetzt das Übernehmen eines Begriffs (m_SelectTerm im psm1) durch eine konfigurierbare Variante:
/// Der Titel bekommt genau EINE Sprache (Radio in der Maske, Vorbelegung aus dem Konfigurator), weitere frei
/// definierbare Eigenschaften werden je Zeile mit einer Sprache gefüllt – wahlweise nur, wenn die Sprache unter
/// „Zusätzliche Sprachen“ angehakt ist. Die Köchl-Funktion bleibt als m_SelectTerm_KoechlOriginal erhalten.
/// Außerdem bekommen die Terms-Masken (Inventor.xaml, AIT.File.xaml) die Radio-Buttons für die Titel-Sprache.
/// Idempotent: der Block steht zwischen Markern und wird bei jedem Lauf neu geschrieben.
/// </summary>
public static class TermCopyService
{
    private const string BlockBegin = "# [AIT-KONFIGURATOR:BEGIN:TERMCOPY]";
    private const string BlockEnd   = "# [AIT-KONFIGURATOR:END:TERMCOPY]";

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    private static string BuildBlock(TermCopyConfig cfg, string nl)
    {
        var rows = string.Join("," + nl, cfg.Rows.Select(r =>
            $"\t@{{ Name = {Ps(r.Property.Trim())}; Lang = {Ps(r.Lang)}; OnlyChecked = {(r.OnlyChecked ? "$true" : "$false")} }}"));
        var rowsBlock = cfg.Rows.Count == 0 ? "@()" : "@(" + nl + rows + nl + ")";

        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append(nl);

        L(BlockBegin);
        L("# Begriff übernehmen: Titel in genau EINER Sprache (Radio rbTitleDE/EN/FR/IT in der Maske), weitere Eigenschaften laut Konfigurator.");
        L("# Wird vom AIT-Konfigurator geschrieben – Änderungen dort vornehmen (Karte \"Benennungskatalog\" > Kopier-Zuordnung).");
        L($"$Global:AITTermTitleLang = {Ps(cfg.TitleLang)}");
        L($"$Global:AITTermCopyRows = {rowsBlock}");
        L();
        L("function mAitTermText($item, [string]$lang) {");
        L("\tswitch ($lang) {");
        L("\t\t'DE' { return $item.Term_DE }");
        L("\t\t'EN' { return $item.Term_EN }");
        L("\t\t'FR' { return $item.Term_FR }");
        L("\t\t'IT' { return $item.Term_IT }");
        L("\t}");
        L("}");
        L();
        L("function mAitTermTitleLang {");
        L("\tforeach ($l in 'DE', 'EN', 'FR', 'IT') {");
        L("\t\t$rb = $dsWindow.FindName(\"rbTitle$l\")");
        L("\t\tif ($rb -and $rb.IsChecked) { return $l }");
        L("\t}");
        L("\treturn $Global:AITTermTitleLang");
        L("}");
        L();
        L("function mAitTermInitTooltips {");
        L("\t# Tooltips an den Sprach-Haken: zeigen, welche Eigenschaften beim Kopieren gefuellt werden");
        L("\t$mNames = @{ DE = 'Deutsch'; EN = 'Englisch'; FR = 'Französisch'; IT = 'Italienisch' }");
        L("\tforeach ($l in 'DE', 'EN', 'FR', 'IT') {");
        L("\t\t$chk = $dsWindow.FindName(\"chk$l\")");
        L("\t\tif ($chk) {");
        L("\t\t\t$ifChecked = @($Global:AITTermCopyRows | Where-Object { $_.Lang -eq $l -and $_.OnlyChecked } | ForEach-Object { $_.Name })");
        L("\t\t\t$always = @($Global:AITTermCopyRows | Where-Object { $_.Lang -eq $l -and -not $_.OnlyChecked } | ForEach-Object { $_.Name })");
        L("\t\t\t$lines = @(\"Erweitert die Suche um \" + $mNames[$l] + \".\")");
        L("\t\t\tif ($ifChecked.Count) { $lines += \"Beim Kopieren gefüllt (wenn angehakt): \" + ($ifChecked -join ', ') }");
        L("\t\t\telse { $lines += \"Keine Eigenschaft konfiguriert, die vom Haken abhängt.\" }");
        L("\t\t\tif ($always.Count) { $lines += \"Immer gefüllt: \" + ($always -join ', ') }");
        L("\t\t\t$lines += \"Der Titel wird unabhängig vom Haken über 'Titel in:' gesteuert.\"");
        L("\t\t\t$chk.ToolTip = ($lines -join \"`n\")");
        L("\t\t}");
        L("\t\t$rb = $dsWindow.FindName(\"rbTitle$l\")");
        L("\t\tif ($rb) { $rb.ToolTip = \"Der Titel wird in \" + $mNames[$l] + \" gefüllt.\" }");
        L("\t}");
        L("}");
        L();
        L("function mAitTermInitRadio {");
        L("\t$rb = $dsWindow.FindName(\"rbTitle$($Global:AITTermTitleLang)\")");
        L("\tif ($rb) { $rb.IsChecked = $true }");
        L("\ttry { mAitTermInitTooltips } catch { }");
        L("}");
        L();
        L("function mAitTermLog($lines) {");
        L("\t# Protokoll des letzten Uebernehmens: %TEMP%\\AIT-TermCopy.log (hilft, Eigenschaftsnamen zu finden)");
        L("\ttry { Set-Content -Path (Join-Path $env:TEMP 'AIT-TermCopy.log') -Value $lines -Encoding UTF8 } catch { }");
        L("}");
        L();
        L("function m_SelectTerm {");
        L("\t$dsDiag.Trace(\"AIT: Begriff uebernehmen\")");
        L("\ttry {");
        L("\t\t$mItem = $dsWindow.FindName(\"dataGrdTermsFound\").SelectedItem");
        L("\t\tif (-not $mItem) { return }");
        L("\t\t# Das Setzen von Eigenschaften rendert den Tab neu – Auswahl/Zustaende vorher merken (wie im Original)");
        L("\t\tif ($dsWindow.Name -eq \"InventorWindow\") { $_temp1 = $dsWindow.FindName(\"Categories\").SelectedIndex }");
        L("\t\tif ($dsWindow.Name -eq \"FileWindow\") {");
        L("\t\t\t$_temp10 = $dsWindow.FindName(\"DocTypeCombo\").SelectedIndex");
        L("\t\t\t$_temp40 = $dsWindow.FindName(\"NumSchms\").IsEnabled");
        L("\t\t\t$_temp41 = $dsWindow.FindName(\"btnOK\").IsEnabled");
        L("\t\t}");
        L();
        L("\t\t# Titel: genau eine Sprache");
        L("\t\t$mLang = mAitTermTitleLang");
        L("\t\t$mTitle = mAitTermText $mItem $mLang");
        L("\t\t$mLog = New-Object System.Collections.ArrayList");
        L("\t\t[void]$mLog.Add((\"{0}  Fenster={1}  Titel-Sprache={2}\" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $dsWindow.Name, $mLang))");
        L("\t\t$mTitleKeys = @()");
        L("\t\tswitch ($dsWindow.Name) {");
        L("\t\t\t\"AutoCADWindow\" { foreach ($k in 'GEN-TITLE-DES1', 'Title') { if ($Prop[$k]) { $Prop[$k].Value = $mTitle; $mTitleKeys += $k } } }");
        L("\t\t\t\"InventorWindow\" { if ($Prop[\"Title\"]) { $Prop[\"Title\"].Value = $mTitle; $mTitleKeys += 'Title' } }");
        L("\t\t\tdefault { foreach ($k in '_XLTN_TITLE', 'Titel', 'Title') { if ($Prop[$k]) { $Prop[$k].Value = $mTitle; $mTitleKeys += $k; break } } }");
        L("\t\t}");
        L("\t\t[void]$mLog.Add(\"Titel: \" + $(if ($mTitleKeys.Count) { \"gesetzt ueber \" + ($mTitleKeys -join ', ') + \" = '$mTitle'\" } else { \"KEINE Titel-Eigenschaft gefunden\" }))");
        L("\t\t$mMissing = $false");
        L();
        L("\t\t# Weitere Eigenschaften laut Konfiguration");
        L("\t\tforeach ($row in $Global:AITTermCopyRows) {");
        L("\t\t\tif ($row.OnlyChecked) {");
        L("\t\t\t\t$chk = $dsWindow.FindName(\"chk$($row.Lang)\")");
        L("\t\t\t\tif (-not ($chk -and $chk.IsChecked)) { [void]$mLog.Add((\"{0} ({1}): uebersprungen - Sprache nicht angehakt\" -f $row.Name, $row.Lang)); continue }");
        L("\t\t\t}");
        L("\t\t\ttry {");
        L("\t\t\t\tif ($Prop[$row.Name]) { $Prop[$row.Name].Value = mAitTermText $mItem $row.Lang; [void]$mLog.Add((\"{0} ({1}): gesetzt\" -f $row.Name, $row.Lang)) }");
        L("\t\t\t\telse { $mMissing = $true; [void]$mLog.Add((\"{0} ({1}): NICHT GEFUNDEN - Eigenschaft ist im Fenster nicht vorhanden\" -f $row.Name, $row.Lang)) }");
        L("\t\t\t}");
        L("\t\t\tcatch { [void]$mLog.Add((\"{0} ({1}): FEHLER beim Schreiben - {2}\" -f $row.Name, $row.Lang, $_.Exception.Message)) }");
        L("\t\t}");
        L("\t\tif ($mLog.Count -le 2 -and $Global:AITTermCopyRows.Count -eq 0) { [void]$mLog.Add(\"Keine weiteren Eigenschaften konfiguriert (Kopier-Zuordnung im AIT-Konfigurator)\") }");
        L("\t\tif ($mMissing -or $Global:AITTermCopyRows.Count -eq 0) { try { [void]$mLog.Add(\"Verfuegbare Eigenschaften im Fenster: \" + ((@($Prop.Keys) | Sort-Object) -join ', ')) } catch { } }");
        L("\t\tmAitTermLog $mLog");
        L();
        L("\t\t$dsWindow.FindName(\"btnSearchTerm\").IsDefault = $false");
        L("\t\t$dsWindow.FindName(\"btnOK\").IsDefault = $true");
        L();
        L("\t\tif ($_temp1) { $dsWindow.FindName(\"Categories\").SelectedIndex = $_temp1 }");
        L("\t\tif ($_temp10) { $dsWindow.FindName(\"DocTypeCombo\").SelectedIndex = $_temp10 }");
        L("\t\tif ($_temp40) { $dsWindow.FindName(\"NumSchms\").IsEnabled = $_temp40 }");
        L("\t\tif ($_temp41) { $dsWindow.FindName(\"btnOK\").IsEnabled = $_temp41 }");
        L("\t}");
        L("\tcatch { $dsDiag.Trace(\"AIT: Fehler beim Uebernehmen des Begriffs\"); try { mAitTermLog (\"Fehler: \" + $_.Exception.Message) } catch { } }");
        L();
        L("\ttry { $dsWindow.FindName(\"tabProperties\").IsSelected = $true } catch { }");
        L();
        L("\t# Maske schliessen, falls vorhanden");
        L("\ttry {");
        L("\t\t$dsWindow.FindName(\"expTermSearch\").Visibility = \"Collapsed\"");
        L("\t\t$dsWindow.FindName(\"expTermSearch\").IsExpanded = $false");
        L("\t\t$dsWindow.FindName(\"btnSearchTerm\").IsDefault = $false");
        L("\t}");
        L("\tcatch { $dsDiag.Trace(\"AIT: Term-Maske nicht vorhanden\") }");
        L("}");
        sb.Append(BlockEnd);
        return sb.ToString();
    }

    /// <summary>Baut den psm1-Text um: Block entfernen, Köchl-Funktion umbenennen, Radio-Vorbelegung einhängen, Block neu anhängen.</summary>
    public static string TransformPsm1(string text, TermCopyConfig cfg)
    {
        var nl = FileIo.DetectNewLine(text);

        // 1) alten Block entfernen
        text = Regex.Replace(text,
            @"(?s)(\r?\n)?" + Regex.Escape(BlockBegin) + ".*?" + Regex.Escape(BlockEnd) + @"[ \t]*(\r?\n)?", "");

        // 2) Originalfunktion umbenennen (nur solange sie noch so heißt)
        text = Regex.Replace(text, @"(?m)^([ \t]*)function\s+m_SelectTerm\s*\{", "$1function m_SelectTerm_KoechlOriginal {");

        // 3) Radio-Vorbelegung beim ersten Öffnen der Maske
        if (!text.Contains("mAitTermInitRadio", StringComparison.Ordinal))
            text = Regex.Replace(text, @"(?m)^([ \t]*)\$Global:mTermCatalogInitialized\s*=\s*\$true",
                m => m.Groups[1].Value + "mAitTermInitRadio" + nl + m.Value);

        // 4) Block anhängen
        if (!text.EndsWith(nl, StringComparison.Ordinal)) text += nl;
        return text + nl + BuildBlock(cfg, nl) + nl;
    }

    /// <summary>Fügt die Radio-Buttons "Titel in:" unter die Sprach-Haken der Terms-Maske ein (Inventor.xaml, AIT.File.xaml).</summary>
    public static string TransformMask(string xaml)
    {
        if (xaml.Contains("x:Name=\"rbTitleDE\"", StringComparison.Ordinal)) return xaml;
        var nl = FileIo.DetectNewLine(xaml);
        var gi = xaml.IndexOf("<Grid x:Name=\"grdLanguages\"", StringComparison.Ordinal);
        if (gi < 0) return xaml;
        const string cdClose = "</Grid.ColumnDefinitions>";
        var cd = xaml.IndexOf(cdClose, gi, StringComparison.Ordinal);
        if (cd < 0) return xaml;
        var chk = Regex.Match(xaml[cd..], @"[ \t]*<CheckBox x:Name=""chkIT""[^>]*/>[ \t]*\r?\n?");
        if (!chk.Success) return xaml;

        // Einrückung der Checkbox-Zeile übernehmen
        var indent = new string(chk.Value.TakeWhile(c => c == ' ' || c == '\t').ToArray());
        var radios = new StringBuilder();
        void R(string s) => radios.Append(indent).Append(s).Append(nl);
        R("<StackPanel x:Name=\"pnlTitleLang\" Grid.Row=\"1\" Grid.ColumnSpan=\"4\" Orientation=\"Horizontal\" Margin=\"0,8,0,0\">");
        R("    <TextBlock Text=\"Titel in:\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\"/>");
        R("    <RadioButton x:Name=\"rbTitleDE\" GroupName=\"grpTitleLang\" Content=\"{Binding UIString[ClassTerms_02], FallbackValue=German}\" Margin=\"0,0,14,0\"/>");
        R("    <RadioButton x:Name=\"rbTitleEN\" GroupName=\"grpTitleLang\" Content=\"{Binding UIString[ClassTerms_03], FallbackValue=English}\" Margin=\"0,0,14,0\"/>");
        R("    <RadioButton x:Name=\"rbTitleFR\" GroupName=\"grpTitleLang\" Content=\"{Binding UIString[ClassTerms_04], FallbackValue=French}\" Margin=\"0,0,14,0\"/>");
        R("    <RadioButton x:Name=\"rbTitleIT\" GroupName=\"grpTitleLang\" Content=\"{Binding UIString[ClassTerms_05], FallbackValue=Italian}\"/>");
        R("</StackPanel>");

        var afterChk = cd + chk.Index + chk.Length;
        var chkLine = chk.Value.EndsWith('\n') ? "" : nl;
        var result = xaml.Insert(afterChk, chkLine + radios);

        // Zeilen im Sprach-Grid: Zeile 0 = Haken, Zeile 1 = Radios
        var rowDefs = nl + indent + "<Grid.RowDefinitions>" + nl + indent + "    <RowDefinition Height=\"Auto\"/>" + nl +
                      indent + "    <RowDefinition Height=\"Auto\"/>" + nl + indent + "</Grid.RowDefinitions>";
        return result.Insert(cd + cdClose.Length, rowDefs);
    }
}
