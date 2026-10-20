using System.Text;
using System.Text.RegularExpressions;

namespace AIT.TermCatalog;

/// <summary>
/// Autovervollständigung in der Begriffssuche (Terms-Maske in Inventor und Vault): Beim Tippen im Suchfeld erscheint eine
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
# Autovervollstaendigung der Begriffssuche. Wird vom AIT-Werkzeug geschrieben (Data Standard > Benennungskatalog).
$Global:AITSuggestEnabled = @@ENABLED@@
$Global:AITSuggestCategory = @@CATEGORY@@
$Global:AITSuggestProps = @{ DE = @@PROP_DE@@; EN = @@PROP_EN@@; FR = @@PROP_FR@@; IT = @@PROP_IT@@ }
$Global:AITSuggestCache = $null
$Global:AITSuggestLoaded = [datetime]::MinValue
$Global:AITSuggestBusy = $false

function mAitSuggestHide {
	try { $lb = $dsWindow.FindName("lstTermSuggest"); if ($lb) { $lb.Visibility = "Collapsed" } } catch { }
}

function mAitSuggestLoad {
	# Alle Begriffe einmal laden (Suche + gebuendeltes Lesen der Sprach-Eigenschaften), danach 10 Minuten aus dem Speicher
	if ($Global:AITSuggestCache -and (((Get-Date) - $Global:AITSuggestLoaded).TotalMinutes -lt 10)) { return $true }
	try {
		$mPropDefs = $vault.PropertyService.GetPropertyDefinitionsByEntityClassId("CUSTENT")
		$mIds = @{}
		foreach ($l in 'DE', 'EN', 'FR', 'IT') {
			$d = $mPropDefs | Where-Object { $_.DispName -eq $Global:AITSuggestProps[$l] }
			if ($d) { $mIds[$l] = $d.Id }
		}
		if ($mIds.Count -eq 0) { return $false }

		$mCond = mCreateClsSearchCond "CategoryName" $Global:AITSuggestCategory "AND"
		$mSort = New-Object autodesk.Connectivity.WebServices.SrchSort
		$mStatus = New-Object autodesk.Connectivity.WebServices.SrchStatus
		$mBookmark = ""
		$mEnts = New-Object 'System.Collections.Generic.List[Autodesk.Connectivity.WebServices.CustEnt]'
		while (($mStatus.TotalHits -eq 0) -or ($mEnts.Count -lt $mStatus.TotalHits)) {
			$mPage = $vault.CustomEntityService.FindCustomEntitiesBySearchConditions(@($mCond), @($mSort), [ref]$mBookmark, [ref]$mStatus)
			if (-not $mPage -or $mPage.Count -eq 0) { break }
			$mEnts.AddRange($mPage)
		}

		$mRows = @{}
		foreach ($e in $mEnts) { $mRows[[long]$e.Id] = New-Object CatalogData }
		$mAll = @($mEnts | ForEach-Object { [long]$_.Id })
		$mPids = [long[]]@($mIds.Values)
		for ($i = 0; $i -lt $mAll.Count; $i += 200) {
			$mChunk = [long[]]@($mAll[$i..([Math]::Min($i + 199, $mAll.Count - 1))])
			$mProps = $vault.PropertyService.GetProperties("CUSTENT", $mChunk, $mPids)
			foreach ($p in $mProps) {
				$r = $mRows[[long]$p.EntityId]
				if (-not $r) { continue }
				if ($mIds['DE'] -and $p.PropDefId -eq $mIds['DE']) { $r.Term_DE = [string]$p.Val }
				elseif ($mIds['EN'] -and $p.PropDefId -eq $mIds['EN']) { $r.Term_EN = [string]$p.Val }
				elseif ($mIds['FR'] -and $p.PropDefId -eq $mIds['FR']) { $r.Term_FR = [string]$p.Val }
				elseif ($mIds['IT'] -and $p.PropDefId -eq $mIds['IT']) { $r.Term_IT = [string]$p.Val }
			}
		}

		$mCache = @()
		foreach ($r in $mRows.Values) {
			$key = (@($r.Term_DE, $r.Term_EN, $r.Term_FR, $r.Term_IT) | Where-Object { $_ }) -join '|'
			if ($key) { $mCache += [pscustomobject]@{ Row = $r; Key = $key.ToLowerInvariant() } }
		}
		$Global:AITSuggestCache = $mCache
		$Global:AITSuggestLoaded = Get-Date
		return $true
	}
	catch { $dsDiag.Trace("AIT: Vorschlaege laden fehlgeschlagen: " + $_.Exception.Message); return $false }
}

function mAitSuggestUpdate {
	$tb = $dsWindow.FindName("mSearchTermText"); $lb = $dsWindow.FindName("lstTermSuggest")
	if (-not $tb -or -not $lb) { return }
	# nur beim Tippen – nicht, wenn die Maske den Text selbst setzt (z. B. beim Oeffnen mit dem Titel)
	if (-not $tb.IsKeyboardFocusWithin) { mAitSuggestHide; return }
	$q = ($tb.Text -replace '\*', '').Trim()
	if ($q.Length -lt 2) { mAitSuggestHide; return }
	if (-not (mAitSuggestLoad)) { mAitSuggestHide; return }
	$ql = $q.ToLowerInvariant()
	$mHits = @($Global:AITSuggestCache | Where-Object { $_.Key.Contains($ql) })
	if ($mHits.Count -eq 0) { mAitSuggestHide; return }

	# Beginn eines Begriffs vor Teiltreffern, dann alphabetisch
	$mRanked = $mHits | Sort-Object -Property @(
		@{ Expression = { $r = $_.Row; if (@($r.Term_DE, $r.Term_EN, $r.Term_FR, $r.Term_IT) | Where-Object { $_ -and $_.ToLowerInvariant().StartsWith($ql) }) { 0 } else { 1 } } },
		@{ Expression = { $_.Row.Term_DE } }
	) | Select-Object -First 12

	$mItems = @()
	foreach ($h in $mRanked) {
		$r = $h.Row
		$primary = if ($r.Term_DE) { $r.Term_DE } elseif ($r.Term_EN) { $r.Term_EN } elseif ($r.Term_FR) { $r.Term_FR } else { $r.Term_IT }
		$others = @()
		if ($r.Term_EN -and $r.Term_EN -ne $primary) { $others += "EN: " + $r.Term_EN }
		if ($r.Term_FR -and $r.Term_FR -ne $primary) { $others += "FR: " + $r.Term_FR }
		if ($r.Term_IT -and $r.Term_IT -ne $primary) { $others += "IT: " + $r.Term_IT }
		$mItems += [pscustomobject]@{ Primary = $primary; Secondary = ($others -join "  ·  "); Row = $r }
	}
	$lb.ItemsSource = $mItems
	$lb.Visibility = "Visible"
}

function mAitSuggestAccept {
	try {
		$lb = $dsWindow.FindName("lstTermSuggest"); $mItem = $lb.SelectedItem
		if (-not $mItem) { return }
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

function mAitSuggestInit {
	# beim ersten Oeffnen der Maske: Ereignisse am Suchfeld und an der Vorschlagsliste anbinden
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
        if (xaml.Contains("x:Name=\"lstTermSuggest\"", StringComparison.Ordinal)) return xaml;
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
        var ind = new string(' ', 40);
        var sb = new StringBuilder();
        void L(string s) => sb.Append(ind).Append(s).Append(nl);
        L("<ListBox x:Name=\"lstTermSuggest\" Grid.Row=\"1\" Grid.ColumnSpan=\"2\" Visibility=\"Collapsed\" MaxHeight=\"190\" Margin=\"0,3,0,0\" IsTabStop=\"False\" HorizontalContentAlignment=\"Stretch\">");
        L("    <ListBox.ItemTemplate>");
        L("        <DataTemplate>");
        L("            <StackPanel Orientation=\"Horizontal\" Margin=\"2,1\">");
        L("                <TextBlock Text=\"{Binding Primary}\" FontWeight=\"SemiBold\" Margin=\"0,0,12,0\"/>");
        L("                <TextBlock Text=\"{Binding Secondary}\" Opacity=\"0.65\" TextTrimming=\"CharacterEllipsis\"/>");
        L("            </StackPanel>");
        L("        </DataTemplate>");
        L("    </ListBox.ItemTemplate>");
        L("</ListBox>");
        return result.Insert(insertAt, sb.ToString());
    }
}
