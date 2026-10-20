using System.Text;
using System.Text.RegularExpressions;

namespace AIT.TermCatalog;

/// <summary>
/// Begriffssuche der Terms-Maske (Inventor und Vault): schnelle Suche mit Statusanzeige und Autovervollständigung. Beim Tippen im Suchfeld erscheint eine
/// Vorschlagsliste mit passenden Begriffen (Treffer in allen vier Sprachen). Ein Klick bzw. Enter übernimmt den Begriff in die
/// Trefferliste; "Übernehmen" funktioniert danach wie bisher.
///
/// Technik: Die Begriffe werden einmal je Sitzung (und danach höchstens alle 10 Minuten) mit wenigen Vault-Aufrufen geladen und
/// lokal gefiltert – kein Vault-Aufruf pro Tastendruck. Die Maske bekommt eine ausgeblendete Liste <c>lstTermSuggest</c> unter dem
/// Suchfeld; das Begriffsmodul (psm1) bekommt einen Block zwischen Markern und einen Aufruf beim ersten Öffnen der Maske.
/// Idempotent: der Block wird bei jedem Lauf neu geschrieben.
/// </summary>
public static class TermSuggestService
{
    private const string BlockBegin = "# [AIT-KONFIGURATOR:BEGIN:TERMSUGGEST]";
    private const string BlockEnd   = "# [AIT-KONFIGURATOR:END:TERMSUGGEST]";
    private const string InitCall   = "mAitSuggestInit";

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    // PowerShell-Block; @@...@@ werden mit den Namen bzw. dem Schalter ersetzt. Tabs wie in den übrigen Dateien.
    private const string BlockTemplate = """
# [AIT-KONFIGURATOR:BEGIN:TERMSUGGEST]
# Begriffssuche: Autovervollstaendigung und schnelle Suche. Wird vom AIT-Werkzeug geschrieben (Data Standard > Benennungskatalog).
# Die Begriffe werden gebuendelt geladen (wenige Vault-Aufrufe) und lokal gefiltert; die Original-Suche (mSearchTerms_KoechlOriginal)
# liest dagegen die Eigenschaften je Treffer einzeln und ist bei vielen Begriffen sehr langsam.
$Global:AITSuggestEnabled = @@ENABLED@@
$Global:AITSuggestCategory = @@CATEGORY@@
$Global:AITSuggestNameLang = @@NAMELANG@@
$Global:AITSuggestProps = @{ DE = @@PROP_DE@@; EN = @@PROP_EN@@; FR = @@PROP_FR@@; IT = @@PROP_IT@@ }
$Global:AITSuggestLevels = @(@@LEVELS@@)
$Global:AITSuggestCache = $null
$Global:AITSuggestLoaded = [datetime]::MinValue
$Global:AITSuggestIndexIncomplete = $false
$Global:AITSuggestBusy = $false

function mAitStatus([string]$text) {
	# Statuszeile der Maske (gelbes Feld ueber der Trefferliste) zeigen/ausblenden und sofort zeichnen,
	# damit der Anwender auch waehrend einer laengeren Abfrage sieht, dass etwas laeuft
	try {
		$m = $dsWindow.FindName("txtTermStatusMsg")
		if ($m) {
			if ($text) { $m.Text = $text; $m.Visibility = "Visible" } else { $m.Visibility = "Collapsed" }
		}
		$dsWindow.Dispatcher.Invoke([Action]{ }, [System.Windows.Threading.DispatcherPriority]::Render)
	} catch { }
}

function mAitGrid([bool]$visible, [bool]$clear) {
	# Trefferliste der Maske zeigen/ausblenden (und leeren): waehrend die Vorschlagsliste offen ist, ersetzt sie die Trefferliste
	try {
		$g = $dsWindow.FindName("dataGrdTermsFound")
		if ($g) {
			if ($clear) { $g.ItemsSource = $null }
			$g.Visibility = $(if ($visible) { "Visible" } else { "Collapsed" })
		}
	} catch { }
}

function mAitSuggestHide {
	try { $lb = $dsWindow.FindName("lstTermSuggest"); if ($lb) { $lb.Visibility = "Collapsed" } } catch { }
	mAitGrid $true $false
}

function mAitSuggestLangs {
	# Suchsprachen wie in der Original-Suche: immer die Sprache des Objektnamens, dazu die unter "Zusaetzliche Sprachen" angehakten
	$l = @($Global:AITSuggestNameLang)
	foreach ($c in 'DE', 'EN', 'FR', 'IT') {
		$chk = $dsWindow.FindName("chk$c")
		if ($chk -and $chk.IsChecked -eq $true -and $l -notcontains $c) { $l += $c }
	}
	return , $l
}

function mAitSuggestLoad([switch]$Force) {
	# Alle Begriffe laden (Suche + gebuendeltes Lesen der Sprach- und Ebenen-Eigenschaften); danach 10 Minuten aus dem Speicher
	if (-not $Force -and $Global:AITSuggestCache -and (((Get-Date) - $Global:AITSuggestLoaded).TotalMinutes -lt 10)) { return $true }
	try {
		$mPropDefs = $vault.PropertyService.GetPropertyDefinitionsByEntityClassId("CUSTENT")
		$mMap = @{}   # Eigenschafts-Id -> @{ Kind = 'L' (Sprache) | 'H' (Ebene); Key = ... }
		foreach ($l in 'DE', 'EN', 'FR', 'IT') {
			$d = $mPropDefs | Where-Object { $_.DispName -eq $Global:AITSuggestProps[$l] } | Select-Object -First 1
			if ($d) { $mMap[[long]$d.Id] = @{ Kind = 'L'; Key = $l } }
		}
		if ($mMap.Count -eq 0) { return $false }
		for ($i = 0; $i -lt $Global:AITSuggestLevels.Count; $i++) {
			$nm = $Global:AITSuggestLevels[$i]
			$d = $mPropDefs | Where-Object { $_.DispName -eq $nm } | Select-Object -First 1
			if ($d) { $mMap[[long]$d.Id] = @{ Kind = 'H'; Key = ($i + 1) } }
		}

		$mCond = mCreateClsSearchCond "CategoryName" $Global:AITSuggestCategory "AND"
		$mSort = New-Object autodesk.Connectivity.WebServices.SrchSort
		$mStatus = New-Object autodesk.Connectivity.WebServices.SrchStatus
		$mBookmark = ""
		$mEnts = New-Object 'System.Collections.Generic.List[Autodesk.Connectivity.WebServices.CustEnt]'
		$Global:AITSuggestIndexIncomplete = $false
		while (($mStatus.TotalHits -eq 0) -or ($mEnts.Count -lt $mStatus.TotalHits)) {
			$mPage = $vault.CustomEntityService.FindCustomEntitiesBySearchConditions(@($mCond), @($mSort), [ref]$mBookmark, [ref]$mStatus)
			if ($mStatus -and $mStatus.IndxStatus -ne "IndexingComplete") { $Global:AITSuggestIndexIncomplete = $true }
			if (-not $mPage -or $mPage.Count -eq 0) { break }
			$mEnts.AddRange($mPage)
		}

		$mItems = @{}
		foreach ($e in $mEnts) {
			$mItems[[long]$e.Id] = [pscustomobject]@{
				Row = (New-Object CatalogData)
				Raw = @{ DE = ''; EN = ''; FR = ''; IT = '' }
				H = @{ 1 = ''; 2 = ''; 3 = ''; 4 = '' }
			}
		}
		$mAll = @($mEnts | ForEach-Object { [long]$_.Id })
		$mPids = [long[]]@($mMap.Keys)
		for ($i = 0; $i -lt $mAll.Count; $i += 200) {
			$mChunk = [long[]]@($mAll[$i..([Math]::Min($i + 199, $mAll.Count - 1))])
			$mProps = $vault.PropertyService.GetProperties("CUSTENT", $mChunk, $mPids)
			foreach ($p in $mProps) {
				$it = $mItems[[long]$p.EntityId]
				$m = $mMap[[long]$p.PropDefId]
				if (-not $it -or -not $m) { continue }
				$v = [string]$p.Val
				if ($m.Kind -eq 'L') {
					$it.Raw[$m.Key] = $v
					switch ($m.Key) { 'DE' { $it.Row.Term_DE = $v } 'EN' { $it.Row.Term_EN = $v } 'FR' { $it.Row.Term_FR = $v } 'IT' { $it.Row.Term_IT = $v } }
				}
				else { $it.H[[int]$m.Key] = $v }
			}
		}

		$mCache = @()
		foreach ($it in $mItems.Values) {
			$L = @{}; $W = @{}
			foreach ($c in 'DE', 'EN', 'FR', 'IT') {
				$s = $it.Raw[$c]
				if ($s) {
					$L[$c] = $s.ToLowerInvariant()
					# Wortliste: " rillenkugellager" - Treffer am Wortanfang statt irgendwo mitten im Wort
					$W[$c] = ' ' + ($L[$c] -replace '[\-/,.()_]', ' ')
				}
			}
			if ($L.Count) { $mCache += [pscustomobject]@{ Row = $it.Row; Raw = $it.Raw; L = $L; W = $W; H = $it.H } }
		}
		$Global:AITSuggestCache = $mCache
		$Global:AITSuggestLoaded = Get-Date
		return $true
	}
	catch { $dsDiag.Trace("AIT: Begriffe laden fehlgeschlagen: " + $_.Exception.Message); return $false }
}

function mAitSuggestScore($it, $langs, [string]$ql, [string]$mW) {
	# -1 = kein Treffer; 0 = Begriff beginnt mit dem Suchtext; 1 = ein Wort beginnt damit; 2 = mitten im Wort (ab drei Zeichen)
	$best = -1
	foreach ($lg in $langs) {
		$t = $it.L[$lg]
		if (-not $t) { continue }
		if ($t.StartsWith($ql)) { return 0 }
		if ($it.W[$lg].Contains($mW)) { $best = 1 }
		elseif ($ql.Length -ge 3 -and $best -eq -1 -and $t.Contains($ql)) { $best = 2 }
	}
	return $best
}

function mAitSuggestUpdate {
	$tb = $dsWindow.FindName("mSearchTermText"); $lb = $dsWindow.FindName("lstTermSuggest")
	if (-not $tb -or -not $lb) { return }
	# nur beim Tippen - nicht, wenn die Maske den Text selbst setzt (z. B. beim Oeffnen mit dem Titel)
	if (-not $tb.IsKeyboardFocusWithin) { mAitSuggestHide; return }
	$q = ($tb.Text -replace '\*', '').Trim()
	if ($q.Length -lt 2) { mAitSuggestHide; return }
	if (-not ($Global:AITSuggestCache -and (((Get-Date) - $Global:AITSuggestLoaded).TotalMinutes -lt 10))) {
		mAitStatus "Begriffe werden geladen ..."
		$ok = mAitSuggestLoad
		mAitStatus ""
		if (-not $ok) { mAitSuggestHide; return }
	}
	$ql = $q.ToLowerInvariant()
	$mW = ' ' + $ql
	$langs = mAitSuggestLangs

	# nur in den gewaehlten Sprachen suchen; Treffer am Wortanfang, Teiltreffer mitten im Wort erst ab drei Zeichen
	$mScored = New-Object System.Collections.ArrayList
	foreach ($it in $Global:AITSuggestCache) {
		$s = mAitSuggestScore $it $langs $ql $mW
		if ($s -ge 0) { [void]$mScored.Add([pscustomobject]@{ Score = $s; Item = $it; Sort = [string]$it.Row.("Term_" + $Global:AITSuggestNameLang) }) }
	}
	if ($mScored.Count -eq 0) { mAitSuggestHide; return }
	$mRanked = @($mScored | Sort-Object -Property Score, Sort | Select-Object -First 12)

	$mItems = @()
	foreach ($h in $mRanked) {
		$r = $h.Item.Row
		$primary = [string]$r.("Term_" + $Global:AITSuggestNameLang)
		if (-not $primary) { $primary = @($r.Term_DE, $r.Term_EN, $r.Term_FR, $r.Term_IT | Where-Object { $_ })[0] }
		$others = @()
		foreach ($c in 'DE', 'EN', 'FR', 'IT') {
			$v = [string]$r.("Term_" + $c)
			if ($v -and $v -ne $primary) { $others += ($c + ": " + $v) }
		}
		$mItems += [pscustomobject]@{ Primary = $primary; Secondary = ($others -join "  ·  "); Row = $r }
	}
	$lb.ItemsSource = $mItems
	$lb.Visibility = "Visible"
	mAitGrid $false $true
}

function mAitSuggestAccept {
	try {
		$lb = $dsWindow.FindName("lstTermSuggest"); $mItem = $lb.SelectedItem
		if (-not $mItem) { return }
		mAitGrid $true $false
		$grid = $dsWindow.FindName("dataGrdTermsFound")
		$grid.ItemsSource = @($mItem.Row)
		$grid.SelectedIndex = 0   # aktiviert "Uebernehmen" ueber den SelectionChanged-Handler der Maske
		try { $dsWindow.FindName("txtTermStatusMsg").Visibility = "Collapsed" } catch { }
		$lb.Visibility = "Collapsed"
		$Global:AITSuggestBusy = $true
		try { $dsWindow.FindName("mSearchTermText").Text = $mItem.Primary } finally { $Global:AITSuggestBusy = $false }
		try { $dsWindow.FindName("btnAdopt").Focus() | Out-Null } catch { }
	}
	catch { $dsDiag.Trace("AIT: Vorschlag uebernehmen fehlgeschlagen: " + $_.Exception.Message) }
}

function mSearchTerms {
	# Ersetzt die Original-Suche (mSearchTerms_KoechlOriginal): gleiche Bedienung - Suchtext (* als Platzhalter), Sprachen, Ebenen-Filter -,
	# aber gebuendelt geladen und lokal gefiltert; waehrenddessen eine Statusanzeige und der Warte-Cursor.
	try {
		mAitSuggestHide
		mAitGrid $true $true
		$grid = $dsWindow.FindName("dataGrdTermsFound")
		try { $dsWindow.Cursor = [System.Windows.Input.Cursors]::Wait } catch { }
		mAitStatus "Suche läuft - bitte einen Moment warten ..."

		if (-not (mAitSuggestLoad -Force)) { mAitStatus "Die Begriffe konnten nicht geladen werden. Bitte die Verbindung zu Vault prüfen."; return }

		# Suchtext: * = beliebige Zeichen; ohne * wird "enthaelt" gesucht (wie bisher)
		$t = ($dsWindow.FindName("mSearchTermText").Text).Trim()
		$rx = $null
		if ($t -and $t -ne '*') {
			$parts = @($t -split '\*' | Where-Object { $_ -ne '' } | ForEach-Object { [regex]::Escape($_) })
			if ($parts.Count) { $rx = New-Object System.Text.RegularExpressions.Regex(($parts -join '.*'), 'IgnoreCase') }
		}
		$langs = mAitSuggestLangs
		# Filter der Katalogebenen (Auswahl oben in der Maske)
		$bc = $dsWindow.FindName("wrpClassification"); $mFilter = @{}
		for ($i = 1; $i -le 4; $i++) { if ($bc.Children[$i].SelectedIndex -ge 0) { $mFilter[$i] = [string]$bc.Children[$i].Text } }

		$mRes = New-Object System.Collections.ArrayList
		foreach ($it in $Global:AITSuggestCache) {
			$ok = $true
			foreach ($k in $mFilter.Keys) { if ($it.H[[int]$k] -ne $mFilter[$k]) { $ok = $false; break } }
			if (-not $ok) { continue }
			if ($rx) {
				$ok = $false
				foreach ($lg in $langs) { $raw = $it.Raw[$lg]; if ($raw -and $rx.IsMatch($raw)) { $ok = $true; break } }
				if (-not $ok) { continue }
			}
			[void]$mRes.Add($it.Row)
		}

		if ($mRes.Count -eq 0) {
			$txt = $UIString["ClassTerms_MSG03"]; if (-not $txt) { $txt = "Keine übereinstimmenden Begriffe vorhanden." }
			mAitStatus $txt
			return
		}
		$grid.ItemsSource = @($mRes | Sort-Object -Property { [string]$_.("Term_" + $Global:AITSuggestNameLang) })
		if ($Global:AITSuggestIndexIncomplete) {
			$txt = $UIString["Adsk.QS.Classification_12"]; if (-not $txt) { $txt = "Achtung: Das Ergebnis beruht auf einem unvollständigen Suchindex." }
			mAitStatus $txt
		}
		else { mAitStatus "" }
	}
	catch {
		$dsDiag.Trace("AIT: Fehler in der Begriffssuche: " + $_.Exception.Message)
		mAitStatus "Bei der Suche ist ein Fehler aufgetreten."
	}
	finally { try { $dsWindow.Cursor = $null } catch { } }
}

function mAitSuggestInit {
	# beim ersten Oeffnen der Maske: Ereignisse am Suchfeld, an der Vorschlagsliste und an den Sprach-Haken anbinden
	if (-not $Global:AITSuggestEnabled) { return }
	try {
		$tb = $dsWindow.FindName("mSearchTermText"); $lb = $dsWindow.FindName("lstTermSuggest")
		if (-not $tb -or -not $lb) { return }
		$tb.add_TextChanged({ if (-not $Global:AITSuggestBusy) { try { mAitSuggestUpdate } catch { } } })
		$tb.add_PreviewKeyDown({
			param($sender, $e)
			try {
				$l = $dsWindow.FindName("lstTermSuggest")
				if ($e.Key -eq 'Down' -and $l.Visibility -eq 'Visible' -and $l.Items.Count -gt 0) {
					$l.SelectedIndex = 0
					$li = $l.ItemContainerGenerator.ContainerFromIndex(0)
					if ($li) { $li.Focus() | Out-Null }
					$e.Handled = $true
				}
				elseif ($e.Key -eq 'Escape' -and $l.Visibility -eq 'Visible') { mAitSuggestHide; $e.Handled = $true }
				elseif ($e.Key -eq 'Return') { mAitSuggestHide }
			} catch { }
		})
		$lb.add_PreviewKeyDown({
			param($sender, $e)
			try {
				if ($e.Key -eq 'Return') { mAitSuggestAccept; $e.Handled = $true }
				elseif ($e.Key -eq 'Escape') { mAitSuggestHide; $dsWindow.FindName("mSearchTermText").Focus() | Out-Null; $e.Handled = $true }
			} catch { }
		})
		$lb.add_PreviewMouseLeftButtonUp({
			param($sender, $e)
			try {
				$l = $dsWindow.FindName("lstTermSuggest")
				$src = $e.OriginalSource
				if ($l.SelectedItem -and $src -is [System.Windows.FrameworkElement] -and $src.DataContext -eq $l.SelectedItem) { mAitSuggestAccept }
			} catch { }
		})
		$btn = $dsWindow.FindName("btnSearchTerm")
		if ($btn) { $btn.add_Click({ mAitSuggestHide }) }
		# Sprachwahl geaendert: Vorschlaege passen nicht mehr - Liste schliessen
		foreach ($c in 'DE', 'EN', 'FR', 'IT') {
			$chk = $dsWindow.FindName("chk$c")
			if ($chk) { $chk.add_Checked({ mAitSuggestHide }); $chk.add_Unchecked({ mAitSuggestHide }) }
		}
	}
	catch { $dsDiag.Trace("AIT: Autovervollstaendigung nicht initialisiert: " + $_.Exception.Message) }
}
# [AIT-KONFIGURATOR:END:TERMSUGGEST]
""";

    private static string BuildBlock(TermCatalogNames names, bool enabled, string nl)
    {
        var block = BlockTemplate
            .Replace("@@ENABLED@@", enabled ? "$true" : "$false")
            .Replace("@@CATEGORY@@", Ps(names.Term))
            .Replace("@@NAMELANG@@", Ps(names.NameLang))
            .Replace("@@LEVELS@@", string.Join(", ", new[] { names.Segment, names.MainGroup, names.Group, names.SubGroup }.Select(Ps)))
            .Replace("@@PROP_DE@@", Ps(names.TermDE)).Replace("@@PROP_EN@@", Ps(names.TermEN))
            .Replace("@@PROP_FR@@", Ps(names.TermFR)).Replace("@@PROP_IT@@", Ps(names.TermIT));
        return Regex.Replace(block, @"\r?\n", nl);
    }

    /// <summary>Baut den psm1-Text um: alten Block entfernen, Aufruf beim ersten Öffnen der Maske einhängen, Block neu anhängen.</summary>
    public static string TransformPsm1(string text, TermCatalogNames names, bool enabled)
    {
        var nl = FileIo.DetectNewLine(text);

        // 1) alten Block entfernen
        text = Regex.Replace(text,
            @"(?s)(\r?\n)?" + Regex.Escape(BlockBegin) + ".*?" + Regex.Escape(BlockEnd) + @"[ \t]*(\r?\n)?", "");

        // 1b) Original-Suche umbenennen (langsam: liest die Eigenschaften je Treffer einzeln); unsere schnelle Suche steht im Block
        text = Regex.Replace(text, @"(?m)^([ \t]*)function\s+mSearchTerms\b", "$1function mSearchTerms_KoechlOriginal");

        // 2) Aufruf beim ersten Öffnen der Maske (vor der Markierung "Maske initialisiert")
        if (!Regex.IsMatch(text, @"(?m)^[ \t]*" + InitCall + @"\b"))
            text = Regex.Replace(text, @"(?m)^([ \t]*)\$Global:mTermCatalogInitialized\s*=\s*\$true",
                m => m.Groups[1].Value + InitCall + nl + m.Value);

        // 3) Block anhängen
        if (!text.EndsWith(nl, StringComparison.Ordinal)) text += nl;
        return text + nl + BuildBlock(names, enabled, nl) + nl;
    }

    /// <summary>Fügt unter dem Suchfeld der Terms-Maske die (ausgeblendete) Vorschlagsliste ein (Inventor.xaml, AIT.File.xaml).</summary>
    public static string TransformMask(string xaml)
    {
        if (xaml.Contains("x:Name=\"lstTermSuggest\"", StringComparison.Ordinal))
        {
            // schon eingebaut: die Liste auf die aktuelle Fassung bringen (Layout wurde überarbeitet)
            var m = Regex.Match(xaml, @"(?s)([ \t]*)<ListBox x:Name=""lstTermSuggest"".*?</ListBox>[ \t]*(\r?\n)?");
            if (!m.Success) return xaml;
            var nl0 = FileIo.DetectNewLine(xaml);
            var ind0 = m.Groups[1].Value;
            var current = BuildListBox(ind0, nl0);
            var existing = m.Value.EndsWith("\n") ? m.Value : m.Value + nl0;
            return existing == current ? xaml : xaml.Remove(m.Index, m.Length).Insert(m.Index, current);
        }
        var gi = xaml.IndexOf("x:Name=\"grdTermSearch\"", StringComparison.Ordinal);
        if (gi < 0) return xaml;
        var nl = FileIo.DetectNewLine(xaml);

        // Zweite Zeile im Such-Grid
        const string rdClose = "</Grid.RowDefinitions>";
        var rd = xaml.IndexOf(rdClose, gi, StringComparison.Ordinal);
        if (rd < 0) return xaml;
        var rdLineStart = xaml.LastIndexOf('\n', rd) + 1;
        var rdIndent = xaml.Substring(rdLineStart, rd - rdLineStart);
        if (rdIndent.Trim().Length != 0) return xaml;   // unerwartetes Layout
        var rowLine = rdIndent + "    <RowDefinition Height=\"Auto\"/>" + nl;
        var result = xaml.Insert(rdLineStart, rowLine);

        // Liste nach dem Such-Button einfügen
        var bi = result.IndexOf("x:Name=\"btnSearchTerm\"", gi, StringComparison.Ordinal);
        if (bi < 0) return xaml;
        const string btnClose = "</Button>";
        var be = result.IndexOf(btnClose, bi, StringComparison.Ordinal);
        if (be < 0) return xaml;
        var lineEnd = result.IndexOf('\n', be);
        var insertAt = lineEnd < 0 ? result.Length : lineEnd + 1;
        return result.Insert(insertAt, BuildListBox(new string(' ', 40), nl));
    }

    /// <summary>Die Vorschlagsliste: zweizeilige Einträge (Begriff, darunter die Übersetzungen), keine waagerechte Bildlaufleiste.</summary>
    private static string BuildListBox(string ind, string nl)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(ind).Append(s).Append(nl);
        L("<ListBox x:Name=\"lstTermSuggest\" Grid.Row=\"1\" Grid.ColumnSpan=\"2\" Visibility=\"Collapsed\" MaxHeight=\"230\" Margin=\"0,3,0,0\" IsTabStop=\"False\" HorizontalContentAlignment=\"Stretch\" ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\">");
        L("    <ListBox.ItemTemplate>");
        L("        <DataTemplate>");
        L("            <StackPanel Margin=\"4,2\">");
        L("                <TextBlock Text=\"{Binding Primary}\" FontWeight=\"SemiBold\" TextTrimming=\"CharacterEllipsis\"/>");
        L("                <TextBlock Text=\"{Binding Secondary}\" Opacity=\"0.65\" FontSize=\"11\" TextTrimming=\"CharacterEllipsis\"/>");
        L("            </StackPanel>");
        L("        </DataTemplate>");
        L("    </ListBox.ItemTemplate>");
        L("</ListBox>");
        return sb.ToString().TrimEnd('\r', '\n') + nl;
    }
}