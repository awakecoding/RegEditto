#Requires -Version 7.0
param(
    [string]$ScreenshotPath = (Join-Path $env:TEMP 'registry-editor-csharp.png'),
    [switch]$ParityWorkflows
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$app = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\win-x64\RegEditto.exe'))
if (-not (Test-Path -LiteralPath $app)) { throw "Build the bundle first: $app" }
$session = 'registry-editor-smoke-' + [Guid]::NewGuid().ToString('N')
$subkey = 'Software\RegEditto.Tests.' + $PID + '.' + [Guid]::NewGuid().ToString('N')
$path = 'HKEY_CURRENT_USER\' + $subkey
$root = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
$launched = $null
$exportFile = [IO.Path]::Combine([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ScreenshotPath)), 'registry editor cli ' + [Guid]::NewGuid().ToString('N') + '.reg')
$legacyFile = [IO.Path]::ChangeExtension($exportFile, '.legacy.reg')
$batch = $null
$prompted = $null
$hklmLaunch = $null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RegistryEditorIconProbe {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    public static extern uint Count(string file, int index, IntPtr large, IntPtr small, uint count);
}
'@
if ([RegistryEditorIconProbe]::Count($app, -1, [IntPtr]::Zero, [IntPtr]::Zero, 0) -eq 0) {
    throw 'The published executable has no Windows icon resource.'
}
function Invoke-Driver([string]$Tool, [hashtable]$Arguments) {
    $Arguments.session = $session
    $PSNativeCommandUseErrorActionPreference = $false
    $json = & cua-driver $Tool ($Arguments | ConvertTo-Json -Compress -Depth 8)
    if ($LASTEXITCODE -ne 0) { throw "GUI driver $Tool failed: $json" }
    $result = $json | ConvertFrom-Json
    if ($result.isError) { throw "GUI driver $Tool refused the operation: $json" }
    return $result
}
function Wait-AppState([scriptblock]$Expectation) {
    $deadline = [DateTime]::UtcNow.AddSeconds(6)
    do {
        $snapshot = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $window; include_screenshot = $false; max_elements = 500; max_depth = 18; timeout_ms = 5000 }
        if (& $Expectation $snapshot) { return $snapshot }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $snapshot.tree_markdown | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\parity-unsettled.txt')
    throw 'The application did not reach the expected UI state within six seconds.'
}
function Wait-AppWindow([string]$Title, [int]$TimeoutSeconds = 6) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $windows = Invoke-Driver 'list_windows' @{ pid = $launched.pid }
        $found = $windows.windows | Where-Object title -eq $Title | Select-Object -First 1
        if ($found) { return $found }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}
try {
    $key = $root.CreateSubKey($subkey)
    try {
        $key.SetValue('', 'Registry Editor demonstration', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('DisplayName', 'Classic Registry Editor', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('InstallPath', '%SystemRoot%\System32', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        $key.SetValue('Enabled', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('Sequence', [long]4294967296, [Microsoft.Win32.RegistryValueKind]::QWord)
        $key.SetValue('Servers', [string[]]@('server-a', 'server-b'), [Microsoft.Win32.RegistryValueKind]::MultiString)
        $key.SetValue('Options', [byte[]]@(0, 1, 127, 128, 255), [Microsoft.Win32.RegistryValueKind]::Binary)
        $child = $key.CreateSubKey('Settings'); $child.Dispose()
    }
    finally { $key.Dispose() }
    $launched = Invoke-Driver 'launch_app' @{ path = $app; additional_arguments = @($path) }
    if ($launched.windows.Count -eq 0) { throw 'The bundle did not create a window.' }
    $modules = (Get-Process -Id $launched.pid).Modules
    if ($modules | Where-Object ModuleName -in @('coreclr.dll', 'clr.dll')) {
        throw 'The published RegEditto loaded a JIT CLR; build the NativeAOT bundle first.'
    }
    if ((Test-Path -LiteralPath (Join-Path (Split-Path $app) 'RegEditto.dll')) -or
        (Test-Path -LiteralPath (Join-Path (Split-Path $app) 'RegistryEditor.dll')) -or
        (Test-Path -LiteralPath (Join-Path (Split-Path $app) 'RegistryEditor.exe'))) {
        throw 'The NativeAOT bundle contains a stale managed application DLL or old executable.'
    }
    $window = $launched.windows[0].window_id
    Start-Sleep -Milliseconds 900
    $state = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $window; max_elements = 400; max_depth = 12; screenshot_out_file = [IO.Path]::GetFullPath($ScreenshotPath); max_image_dimension = 0 }
    $appWindows = Invoke-Driver 'list_windows' @{ pid = $launched.pid }
    if (-not ($appWindows.windows | Where-Object title -eq 'RegEditto')) { throw 'The application window does not use the RegEditto name.' }
    $version = (Get-Item -LiteralPath $app).VersionInfo
    if ($version.ProductName -ne 'RegEditto') { throw 'The executable product metadata does not use the RegEditto name.' }
    $address = $state.elements | Where-Object role -eq 'Edit' | Select-Object -First 1
    if ($address.value -ne ('Computer\' + $path)) { throw "Address navigation failed: $($address.value)" }
    foreach ($expected in @('DisplayName', 'REG_EXPAND_SZ', 'REG_DWORD', 'REG_QWORD', 'REG_MULTI_SZ', 'REG_BINARY', '0x00000001 (1)')) {
        if (-not $state.tree_markdown.Contains($expected)) { throw "Missing rendered value: $expected" }
    }
    foreach ($expected in @('File', 'Edit', 'View', 'Favorites', 'Help', 'Name', 'Type', 'Data')) {
        if (-not ($state.elements | Where-Object label -eq $expected)) { throw "Missing conventional UI surface: $expected" }
    }
    if ($ParityWorkflows) {
        $missingImport = Join-Path $env:TEMP ('registry editor missing ' + [Guid]::NewGuid().ToString('N') + '.reg')
        $batch = Start-Process -FilePath $app -ArgumentList @('/s', ('"' + $missingImport + '"')) -PassThru
        if (-not $batch.WaitForExit(5000)) { throw 'A missing silent-import file opened a blocking UI instead of exiting.' }
        if ($batch.ExitCode -ne 1) { throw 'A missing silent-import file did not return the documented failure code.' }
        $batch = $null
        $exportProcess = Start-Process -FilePath $app -ArgumentList @('-E', ('"' + $exportFile + '"'), ('"' + $path + '"')) -PassThru -Wait
        if ($exportProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $exportFile)) { throw 'Command-line selected-branch export failed.' }
        $legacyProcess = Start-Process -FilePath $app -ArgumentList @('/A', ('"' + $legacyFile + '"'), ('"' + $path + '"')) -PassThru -Wait
        if ($legacyProcess.ExitCode -ne 0 -or -not [IO.File]::ReadAllText($legacyFile).StartsWith("REGEDIT4`r`n")) {
            throw 'Command-line ANSI export did not produce a REGEDIT4 file.'
        }
        $key = $root.OpenSubKey($subkey, $true)
        try { $key.SetValue('DisplayName', 'Changed by isolated CLI test', [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $key.Dispose() }
        $importProcess = Start-Process -FilePath $app -ArgumentList @('/mS', ('"' + $exportFile + '"'), ('"' + $legacyFile + '"')) -PassThru -Wait
        if ($importProcess.ExitCode -ne 0) { throw 'Command-line import of the isolated exported fixture failed.' }
        $key = $root.OpenSubKey($subkey)
        try { if ($key.GetValue('DisplayName') -ne 'Classic Registry Editor') { throw 'Command-line import did not restore the fixture value.' } }
        finally { $key.Dispose() }
        $promptLaunch = Start-Process -FilePath $app -ArgumentList @(('"' + $exportFile + '"'), ('"' + $legacyFile + '"')) -PassThru
        $prompted = @{ pid = $promptLaunch.Id }
        foreach ($expectedFile in @($exportFile, $legacyFile)) {
            $deadline = [DateTime]::UtcNow.AddSeconds(6)
            $declined = $false
            do {
                $promptWindows = Invoke-Driver 'list_windows' @{ pid = $prompted.pid }
                foreach ($candidate in $promptWindows.windows) {
                    $promptState = Invoke-Driver 'get_window_state' @{ pid = $prompted.pid; window_id = $candidate.window_id; include_screenshot = $false; max_elements = 120; max_depth = 10 }
                    $no = $promptState.elements | Where-Object { $_.role -eq 'Button' -and $_.label -eq 'No' } | Select-Object -First 1
                    if ($no -and $promptState.tree_markdown.Contains($expectedFile)) {
                        Invoke-Driver 'click' @{ pid = $prompted.pid; window_id = $candidate.window_id; element_token = $no.element_token; delivery_mode = 'background' } | Out-Null
                        $declined = $true
                        break
                    }
                }
                if (-not $declined) { Start-Sleep -Milliseconds 100 }
            } while (-not $declined -and [DateTime]::UtcNow -lt $deadline)
            if (-not $declined) {
                $promptState.tree_markdown | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\cli-prompt-state.txt')
                throw 'Prompted multi-file import did not offer the next file in order.'
            }
        }
        $promptProcess = Get-Process -Id $prompted.pid -ErrorAction SilentlyContinue
        if ($promptProcess -and -not $promptProcess.WaitForExit(5000)) { throw 'Prompted import did not exit after declining both files.' }
        $prompted = $null
        # Changes and ACL snapshots are confined to this exact fixture.
        $key = $root.OpenSubKey($subkey, $true)
        try { $key.SetValue('ExternalChange', 'Observed automatically', [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $key.Dispose() }
        Start-Sleep -Milliseconds 500
        $state = Wait-AppState { param($snapshot) $snapshot.tree_markdown.Contains('ExternalChange') }
        if (-not $state.tree_markdown.Contains('ExternalChange')) { throw 'Native change notification did not refresh the value pane.' }
        $edit = $state.elements | Where-Object label -eq 'Edit' | Select-Object -First 1
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $edit.element_token; action = 'expand'; delivery_mode = 'background' } | Out-Null
        Start-Sleep -Milliseconds 150
        $state = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $window; include_screenshot = $false; max_elements = 500; max_depth = 18 }
        $permissions = $state.elements | Where-Object { $_.label -match '^Permissions' } | Select-Object -First 1
        if (-not $permissions) {
            $state.tree_markdown | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\parity-menu.txt')
            throw 'Permissions menu was not exposed in the app snapshot.'
        }
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $permissions.element_token; delivery_mode = 'background' } | Out-Null
        Start-Sleep -Milliseconds 250
        $windows = Invoke-Driver 'list_windows' @{ pid = $launched.pid }
        $windows | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\parity-windows.json')
        $permissionWindow = $windows.windows | Where-Object { $_.title -match 'Permission|Security' } | Select-Object -First 1
        if (-not $permissionWindow) { throw 'Native permissions dialog did not open.' }
        $permissionState = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $permissionWindow.window_id; max_elements = 150; max_depth = 10;
            screenshot_out_file = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($ScreenshotPath), '.permissions.png') }
        $cancel = $permissionState.elements | Where-Object { $_.role -eq 'Button' -and $_.label -eq 'Cancel' } | Select-Object -First 1
        if (-not $cancel) { throw 'Native permissions dialog did not expose Cancel.' }
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $permissionWindow.window_id; element_token = $cancel.element_token; delivery_mode = 'background' } | Out-Null
        Start-Sleep -Milliseconds 150
        $state = Wait-AppState { param($snapshot) $snapshot.tree_markdown.Contains('ExternalChange') -and ($snapshot.elements | Where-Object { $_.role -eq 'ListItem' -and $_.label.StartsWith('Options,') }) }
        if (-not $state.tree_markdown.Contains('ExternalChange')) { throw 'Returning from permissions lost the selected registry values.' }
        $options = $state.elements | Where-Object { $_.role -eq 'ListItem' -and $_.label.StartsWith('Options,') } | Select-Object -First 1
        if (-not $options) {
            $state.tree_markdown | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\parity-values.txt')
            throw 'Options value label was not exposed after closing permissions.'
        }
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $options.element_token; delivery_mode = 'background' } | Out-Null
        $state = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $window; include_screenshot = $false; max_elements = 500; max_depth = 14 }
        $edit = $state.elements | Where-Object label -eq 'Edit' | Select-Object -First 1
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $edit.element_token; action = 'expand'; delivery_mode = 'background' } | Out-Null
        $state = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $window; include_screenshot = $false; max_elements = 500; max_depth = 18 }
        $modify = $state.elements | Where-Object { $_.label -match '^Modify Binary Data' } | Select-Object -First 1
        if (-not $modify) {
            $state.tree_markdown | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\parity-menu.txt')
            throw 'Modify Binary Data was not exposed in the Edit menu.'
        }
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $modify.element_token; delivery_mode = 'background' } | Out-Null
        Start-Sleep -Milliseconds 150
        $windows = Invoke-Driver 'list_windows' @{ pid = $launched.pid }
        $binaryWindow = Wait-AppWindow 'Edit Binary Value'
        if (-not $binaryWindow) { throw 'The binary value editor did not open.' }
        $binaryState = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $binaryWindow.window_id; max_elements = 150; max_depth = 10;
            screenshot_out_file = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($ScreenshotPath), '.binary.png') }
        if (-not ($binaryState.elements | Where-Object value -eq 'Options')) { throw 'The binary dialog is editing the wrong value.' }
        $cancel = $binaryState.elements | Where-Object { $_.role -eq 'Button' -and $_.label -eq 'Cancel' } | Select-Object -First 1
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $binaryWindow.window_id; element_token = $cancel.element_token; delivery_mode = 'background' } | Out-Null
        $key = $root.OpenSubKey($subkey)
        try {
            if ([Convert]::ToHexString($key.GetValue('Options')) -ne '00017F80FF') { throw 'Cancelling the binary dialog changed fixture bytes.' }
        }
        finally { $key.Dispose() }
        $state = Wait-AppState { param($snapshot) $snapshot.tree_markdown.Contains('ExternalChange') }
        $fileMenu = $state.elements | Where-Object label -eq 'File' | Select-Object -First 1
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $fileMenu.element_token; action = 'expand'; delivery_mode = 'background' } | Out-Null
        $state = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $window; include_screenshot = $false; max_elements = 500; max_depth = 18 }
        $print = $state.elements | Where-Object { $_.label -match '^Print' } | Select-Object -First 1
        if (-not $print) { throw 'The conventional Print command was not exposed.' }
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $print.element_token; delivery_mode = 'background' } | Out-Null
        Start-Sleep -Milliseconds 200
        $windows = Invoke-Driver 'list_windows' @{ pid = $launched.pid }
        $printWindow = Wait-AppWindow 'Print'
        if (-not $printWindow) {
            $windows | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\parity-print-windows.json')
            foreach ($other in $windows.windows) {
                $otherState = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $other.window_id; include_screenshot = $false; max_elements = 80; max_depth = 8 }
                $otherState.tree_markdown | Add-Content (Join-Path $PSScriptRoot '..\.avalonia\parity-print-state.txt')
            }
            throw 'The native print dialog did not open. No print job was requested.'
        }
        $printState = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $printWindow.window_id; max_elements = 180; max_depth = 12 }
        $cancel = $printState.elements | Where-Object { $_.role -eq 'Button' -and $_.label -eq 'Cancel' } | Select-Object -First 1
        if (-not $cancel) { throw 'The print dialog did not expose Cancel; no print job was requested.' }
        Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $printWindow.window_id; element_token = $cancel.element_token; delivery_mode = 'background' } | Out-Null
        $state = Wait-AppState { param($snapshot) $snapshot.tree_markdown.Contains('ExternalChange') }
        Write-Host 'Parity smoke passed: dash/quoted Unicode and ANSI export, combined multi-file silent import, ordered prompted import cancellation/exit, silent failure, automatic refresh, native permissions and print cancellation, and binary cancellation without byte changes.'
    }
    # Selecting the root avoids persisting the temporary fixture as the last visited key.
    $computer = $state.elements | Where-Object role -eq 'TreeItem' | Select-Object -First 1
    Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $computer.element_token; delivery_mode = 'background' } | Out-Null
    Start-Sleep -Milliseconds 200
    $state = Invoke-Driver 'get_window_state' @{ pid = $launched.pid; window_id = $window; include_screenshot = $false; max_elements = 400; max_depth = 12 }
    $address = $state.elements | Where-Object role -eq 'Edit' | Select-Object -First 1
    if ($address.value -ne 'Computer') { throw 'Computer selection did not clear the address and value view.' }
    foreach ($previousValue in @('DisplayName', 'InstallPath', 'Sequence', 'Servers', 'Options')) {
        if ($state.elements | Where-Object label -eq $previousValue) { throw "Computer selection retained a value row: $previousValue" }
    }
    $close = $state.elements | Where-Object { $_.role -eq 'Button' -and $_.label -eq 'Close' } | Select-Object -First 1
    Invoke-Driver 'click' @{ pid = $launched.pid; window_id = $window; element_token = $close.element_token; delivery_mode = 'background' } | Out-Null
    $process = Get-Process -Id $launched.pid -ErrorAction SilentlyContinue
    if ($process -and -not $process.WaitForExit(5000)) { throw 'The application did not close naturally.' }
    $hklmLaunch = Invoke-Driver 'launch_app' @{ path = $app; additional_arguments = @('HKEY_LOCAL_MACHINE\SYSTEM') }
    if ($hklmLaunch.windows.Count -eq 0) { throw 'The application did not open the HKLM test path.' }
    $hklmState = Invoke-Driver 'get_window_state' @{ pid = $hklmLaunch.pid; window_id = $hklmLaunch.windows[0].window_id; max_elements = 250; max_depth = 14 }
    $hklmAddress = $hklmState.elements | Where-Object { $_.role -eq 'Edit' -and $_.value -eq 'Computer\HKEY_LOCAL_MACHINE\SYSTEM' } | Select-Object -First 1
    if (-not $hklmAddress -or $hklmState.tree_markdown.Contains('unauthorized operation')) {
        $hklmState.tree_markdown | Set-Content (Join-Path $PSScriptRoot '..\.avalonia\hklm-ui-failure.txt')
        throw 'The real HKLM\SYSTEM path did not load through the registry tree.'
    }
    $hklmProcess = Get-Process -Id $hklmLaunch.pid -ErrorAction SilentlyContinue
    if ($hklmProcess) { Stop-Process -Id $hklmLaunch.pid; $hklmProcess.WaitForExit(5000) }
    $hklmLaunch = $null
    Write-Host 'Read-only HKLM smoke passed: the native app opened HKLM\SYSTEM with the current token.'
    Write-Host "Bundle smoke passed: native icon resource, NativeAOT without CLR, classic menus, address navigation, lazy tree, all six value types, Computer selection and natural shutdown."
    Write-Host "Screenshot: $ScreenshotPath"
}
finally {
    if ($prompted -and (Get-Process -Id $prompted.pid -ErrorAction SilentlyContinue)) { Stop-Process -Id $prompted.pid }
    if ($batch -and -not $batch.HasExited) { Stop-Process -Id $batch.Id }
    if ($hklmLaunch -and (Get-Process -Id $hklmLaunch.pid -ErrorAction SilentlyContinue)) { Stop-Process -Id $hklmLaunch.pid }
    if ($launched -and (Get-Process -Id $launched.pid -ErrorAction SilentlyContinue)) {
        # Terminate only a failed test's own launch, never another registry editor.
        Stop-Process -Id $launched.pid -ErrorAction SilentlyContinue
    }
    $root.DeleteSubKeyTree($subkey, $false)
    $root.Dispose()
    if (Test-Path -LiteralPath $exportFile) { Remove-Item -LiteralPath $exportFile }
    if (Test-Path -LiteralPath $legacyFile) { Remove-Item -LiteralPath $legacyFile }
    & cua-driver end_session (@{ session = $session } | ConvertTo-Json -Compress) | Out-Null
}
