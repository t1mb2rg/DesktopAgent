param(
    [Parameter(Mandatory=$true)][ValidateSet('list','dump','invoke','settext','expand','read','select','selectname','scrollname','check')][string]$Action,
    [string]$AutomationId,
    [string]$NameContains,
    [string]$TextFile,
    [string]$ProcessName = 'DesktopAgent.WinUI',
    [string]$TitleContains = '',
    [int]$Index = 0,
    [int]$Depth = 12,
    [int]$Limit = 600,
    [int]$TailChars = 4000
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient | Out-Null
Add-Type -AssemblyName UIAutomationTypes | Out-Null

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$IP = [System.Windows.Automation.InvokePattern]
$VP = [System.Windows.Automation.ValuePattern]
$EP = [System.Windows.Automation.ExpandCollapsePattern]

function Get-AppWindow {
    $procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    if (-not $procs) { throw "$ProcessName is not running." }
    foreach ($p in $procs) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
        $windows = $AE::RootElement.FindAll($TS::Children, $cond)
        foreach ($w in $windows) {
            if ([string]::IsNullOrEmpty($TitleContains)) { return $w }
            if ($w.Current.Name -and $w.Current.Name.Contains($TitleContains)) { return $w }
        }
    }
    throw "No top-level window for $ProcessName (title contains '$TitleContains')."
}

function Walk($el, $level, $path) {
    if ($level -gt $Depth) { return }
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $child = $walker.GetFirstChild($el)
    while ($child -ne $null) {
        $ct = $child.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $id = $child.Current.AutomationId
        $nm = $child.Current.Name
        $en = $child.Current.IsEnabled
        $off = $child.Current.IsOffscreen
        if ($script:All -or $id -or ($ct -in @('Button','Edit','ComboBox','CheckBox','Text','ListItem','TabItem','Hyperlink','RadioButton'))) {
            $nmShort = if ($nm -and $nm.Length -gt 70) { $nm.Substring(0,70) } else { $nm }
            "{0}{1}|id={2}|name={3}|enabled={4}|off={5}" -f ('  ' * $level), $ct, $id, $nmShort, $en, $off
        }
        if ($script:count -lt $Limit) {
            $script:count++
            Walk $child ($level + 1) $path
        }
        $child = $walker.GetNextSibling($child)
    }
}

function Find-All($root, $cond) {
    return @($root.FindAll($TS::Descendants, $cond))
}

$window = Get-AppWindow
$script:count = 0
$script:All = ($Action -eq 'dump' -or $Action -eq 'list')

switch ($Action) {
    'list' {
        Walk $window 0 ''
    }
    'dump' {
        Walk $window 0 ''
    }
    'invoke' {
        if ($TextFile) {
            $target = [System.IO.File]::ReadAllText($TextFile, [System.Text.Encoding]::UTF8).Trim()
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $target)
            $AutomationId = "name:$target"
        } elseif ($AutomationId) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $AutomationId)
        } else {
            throw '-AutomationId or -TextFile is required for invoke.'
        }
        $hits = Find-All $window $cond
        if ($hits.Count -eq 0) { throw "No element matching '$AutomationId'." }
        $el = $null
        for ($i = 0; $i -lt $hits.Count; $i++) {
            $c = $hits[$i]
            if ($c.Current.IsEnabled -and -not $c.Current.IsOffscreen) { $el = $c; break }
        }
        if ($el -eq $null) { $el = $hits[0] }
        try { $el.SetFocus() } catch { }
        Start-Sleep -Milliseconds 120
        $done = $false
        try { $el.GetCurrentPattern($IP::Pattern).Invoke(); $done = $true } catch { }
        if (-not $done) { try { $el.GetCurrentPattern($EP::Pattern).Expand(); $done = $true } catch { } }
        if (-not $done) { throw "Element '$AutomationId' supports neither Invoke nor Expand." }
        "invoked: $AutomationId (count=$($hits.Count))"
    }
    'expand' {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $AutomationId)
        $hits = Find-All $window $cond
        if ($hits.Count -eq 0) { throw "No element with AutomationId '$AutomationId'." }
        $el = $hits[0]
        try { $el.GetCurrentPattern($EP::Pattern).Expand(); "expanded: $AutomationId" }
        catch { throw "Element '$AutomationId' does not support Expand." }
    }
    'settext' {
        if (-not $AutomationId) { throw '-AutomationId is required for settext.' }
        if (-not $TextFile) { throw '-TextFile is required for settext.' }
        $text = [System.IO.File]::ReadAllText($TextFile, [System.Text.Encoding]::UTF8)
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $AutomationId)
        $hits = Find-All $window $cond
        if ($hits.Count -eq 0) { throw "No element with AutomationId '$AutomationId'." }
        $el = $hits[0]
        try { $el.SetFocus() } catch { }
        Start-Sleep -Milliseconds 120
        $vp = $el.GetCurrentPattern($VP::Pattern)
        $vp.SetValue($text)
        "set text ({0} chars) into {1}" -f $text.Length, $AutomationId
    }
    'select' {
        if (-not $AutomationId) { throw '-AutomationId (list) is required for select.' }
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $AutomationId)
        $hits = Find-All $window $cond
        if ($hits.Count -eq 0) { throw "No element with AutomationId '$AutomationId'." }
        $list = $hits[0]
        $itemCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $items = @($list.FindAll($TS::Descendants, $itemCond))
        "items found: $($items.Count)"
        if ($Index -ge $items.Count) { throw "Index $Index out of range ($($items.Count) items)." }
        $item = $items[$Index]
        $item.SetFocus()
        Start-Sleep -Milliseconds 150
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        "selected item ${Index}: $($item.Current.Name)"
    }
    'selectname' {
        if (-not $TextFile) { throw '-TextFile (substring of the ListItem name) is required for selectname.' }
        $needle = [System.IO.File]::ReadAllText($TextFile, [System.Text.Encoding]::UTF8).Trim()
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $items = @($window.FindAll($TS::Descendants, $cond))
        "list items found: $($items.Count)"
        $hit = $null
        for ($i = 0; $i -lt $items.Count; $i++) {
            if ($items[$i].Current.Name -like "*$needle*") { $hit = $items[$i]; break }
        }
        if ($hit -eq $null) { throw "No ListItem containing '$needle'." }
        $hit.SetFocus()
        Start-Sleep -Milliseconds 150
        $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        "selected list item: $($hit.Current.Name)"
    }
    'scrollname' {
        if (-not $TextFile) { throw '-TextFile (substring of the element name) is required for scrollname.' }
        $needle = [System.IO.File]::ReadAllText($TextFile, [System.Text.Encoding]::UTF8).Trim()
        $btnCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $hits = @($window.FindAll($TS::Descendants, $btnCond))
        $hit = $null
        for ($i = 0; $i -lt $hits.Count; $i++) { if ($hits[$i].Current.Name -like "*$needle*") { $hit = $hits[$i]; break } }
        if ($hit -eq $null) { throw "No Button containing '$needle'." }
        try { $hit.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); "scrolled into view: $($hit.Current.Name)" }
        catch { throw "Button '$needle' does not support ScrollItem." }
    }
    'check' {
        if (-not $TextFile) { throw '-TextFile (substring of the checkbox name) is required for check.' }
        $needle = [System.IO.File]::ReadAllText($TextFile, [System.Text.Encoding]::UTF8).Trim()
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
        $boxes = @($window.FindAll($TS::Descendants, $cond))
        "checkboxes found: $($boxes.Count)"
        $hit = $null
        for ($i = 0; $i -lt $boxes.Count; $i++) {
            if ($boxes[$i].Current.Name -like "*$needle*") { $hit = $boxes[$i]; break }
        }
        if ($hit -eq $null) { throw "No checkbox containing '$needle'." }
        $tp = $hit.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $state = $tp.Current.ToggleState
        $tp.Toggle()
        Start-Sleep -Milliseconds 150
        $after = $tp.Current.ToggleState
        "toggled '$($hit.Current.Name)': $state -> $after"
    }
    'read' {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $AutomationId)
        $hits = Find-All $window $cond
        if ($hits.Count -eq 0) { throw "No element with AutomationId '$AutomationId'." }
        for ($i = 0; $i -lt $hits.Count; $i++) {
            $e = $hits[$i]
            $ct = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            "--- hit ${i}: $ct enabled=$($e.Current.IsEnabled)"
            try { "value: " + $e.GetCurrentPattern($VP::Pattern).Current.Value } catch { }
            "name: " + $e.Current.Name
            try {
                $tp = $e.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
                $full = $tp.DocumentRange.GetText(400000)
                "text-length: " + $full.Length
                if ($TailChars -gt 0 -and $full.Length -gt $TailChars) {
                    "text-tail:"
                    $full.Substring($full.Length - $TailChars)
                } else {
                    "text: " + $full
                }
            } catch { }
        }
    }
}
