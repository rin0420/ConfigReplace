$ErrorActionPreference = 'Stop'

$script:AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:DataRoot = Join-Path $script:AppRoot 'SimpleProfiles'
$script:ConfigPath = Join-Path $script:DataRoot 'profiles.json'
$script:BackupRoot = Join-Path $script:DataRoot 'Backups'
$script:JsonEncoding = New-Object System.Text.UTF8Encoding($false)

function Ensure-DataDirectories {
    New-Item -ItemType Directory -Path $script:DataRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $script:BackupRoot -Force | Out-Null
}

function Normalize-PathValue([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) { throw 'パスが空です。' }
    return [System.IO.Path]::GetFullPath($PathValue.Trim())
}

function Test-SameOrChildPath([string]$PathValue, [string]$RootValue) {
    $path = (Normalize-PathValue $PathValue).TrimEnd('\', '/')
    $root = (Normalize-PathValue $RootValue).TrimEnd('\', '/')
    if ($path.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $path.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-SafeFolderName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    if ($Name -eq '.' -or $Name -eq '..') { return $false }
    if ($Name.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) { return $false }
    if ($Name.Contains('\') -or $Name.Contains('/')) { return $false }
    return $true
}

function Test-ReparsePoint([string]$PathValue) {
    $attributes = [System.IO.File]::GetAttributes($PathValue)
    return (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-SafeTree([string]$PathValue) {
    if (-not (Test-Path -LiteralPath $PathValue -PathType Container)) { throw "フォルダーがありません: $PathValue" }
    if (Test-ReparsePoint $PathValue) { throw "再解析ポイントは使用できません: $PathValue" }
    foreach ($item in @(Get-ChildItem -LiteralPath $PathValue -Force -Recurse)) {
        if (Test-ReparsePoint $item.FullName) { throw "再解析ポイントは使用できません: $($item.FullName)" }
    }
}

function Get-FolderHash([string]$PathValue) {
    if (-not (Test-Path -LiteralPath $PathValue -PathType Container)) {
        if (Test-Path -LiteralPath $PathValue) { throw "配置先に同名のファイルがあります: $PathValue" }
        return 'MISSING'
    }

    Assert-SafeTree $PathValue
    $root = (Normalize-PathValue $PathValue).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($directory in @(Get-ChildItem -LiteralPath $PathValue -Directory -Force -Recurse)) {
        $relative = $directory.FullName.Substring($root.Length).TrimStart('\', '/')
        $lines.Add("D|$relative")
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $PathValue -File -Force -Recurse)) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $lines.Add("F|$relative|$($file.Length)|$hash")
    }
    $ordered = $lines | Sort-Object
    $text = [string]::Join("`n", @($ordered))
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    return ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes))).Replace('-', '')
}

function Copy-Folder([string]$SourcePath, [string]$DestinationPath) {
    Assert-SafeTree $SourcePath
    New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    & robocopy.exe $SourcePath $DestinationPath /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS | Out-Null
    $code = $LASTEXITCODE
    if ($code -gt 7) { throw "フォルダーコピーに失敗しました: $SourcePath -> $DestinationPath (robocopy=$code)" }
}

function Remove-Folder([string]$PathValue) {
    if (-not (Test-Path -LiteralPath $PathValue)) { return }
    foreach ($file in @(Get-ChildItem -LiteralPath $PathValue -File -Force -Recurse -ErrorAction SilentlyContinue)) {
        try { $file.IsReadOnly = $false } catch { }
    }
    Remove-Item -LiteralPath $PathValue -Recurse -Force
}

function Save-JsonFile($Object, [string]$PathValue) {
    $json = $Object | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($PathValue, $json, $script:JsonEncoding)
}

function Load-Document {
    if (-not (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf)) {
        return [pscustomobject]@{ schemaVersion = 1; profiles = @() }
    }
    $json = [System.IO.File]::ReadAllText($script:ConfigPath, $script:JsonEncoding)
    if ([string]::IsNullOrWhiteSpace($json)) { return [pscustomobject]@{ schemaVersion = 1; profiles = @() } }
    $document = $json | ConvertFrom-Json
    if ($null -eq $document.profiles) { $document | Add-Member -NotePropertyName profiles -NotePropertyValue @() }
    $document.profiles = @($document.profiles)
    return $document
}

function Get-ProfileFolders($Profile) {
    $result = @{}
    foreach ($folder in @($Profile.folders)) {
        $root = Normalize-PathValue $folder.targetRoot
        if (-not (Test-SafeFolderName ([string]$folder.folderName))) { throw "フォルダー名が不正です: $($folder.folderName)" }
        if (-not $result.ContainsKey($root)) { $result[$root] = @{} }
        if ($result[$root].ContainsKey([string]$folder.folderName)) { throw "同じ配置先に同名フォルダーがあります: $root\$($folder.folderName)" }
        $result[$root][[string]$folder.folderName] = $folder
    }
    return $result
}

function Get-ManagedFolders($Document) {
    $result = @{}
    foreach ($profile in @($Document.profiles)) {
        foreach ($group in (Get-ProfileFolders $profile).GetEnumerator()) {
            if (-not $result.ContainsKey($group.Key)) { $result[$group.Key] = @{} }
            foreach ($name in $group.Value.Keys) { $result[$group.Key][$name] = $true }
        }
    }
    return $result
}

function Test-InternalPath([string]$PathValue) {
    return (Test-SameOrChildPath $PathValue $script:DataRoot)
}

function Assert-SafeParent([string]$PathValue) {
    $current = Split-Path -Parent $PathValue
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current -PathType Container) {
            if (Test-ReparsePoint $current) { throw "再解析ポイントの親フォルダーは使用できません: $current" }
            $parent = Split-Path -Parent $current
            if ($parent -eq $current) { break }
            $current = $parent
        } else {
            $current = Split-Path -Parent $current
        }
    }
}

function Ensure-BackupWritable {
    New-Item -ItemType Directory -Path $script:BackupRoot -Force | Out-Null
    $probe = Join-Path $script:BackupRoot ('.write-test-' + [guid]::NewGuid().ToString('N'))
    try { [System.IO.File]::WriteAllText($probe, 'ok', $script:JsonEncoding) }
    finally { if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force } }
}

function New-RunDirectory([string]$RunId) {
    $name = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $RunId.Substring(0, 8)
    $path = Join-Path $script:BackupRoot $name
    New-Item -ItemType Directory -Path (Join-Path $path 'folders') -Force | Out-Null
    return $path
}

function Get-RelativeBackupPath([string]$FolderName, [int]$Index) {
    return Join-Path (Join-Path 'folders' ($Index.ToString('D4'))) $FolderName
}

function Get-AbsoluteBackupPath([string]$RunDirectory, [string]$RelativePath) {
    $root = Normalize-PathValue $RunDirectory
    $path = Normalize-PathValue (Join-Path $root $RelativePath)
    if (-not (Test-SameOrChildPath $path $root)) { throw 'バックアップパスが履歴フォルダー外を参照しています。' }
    return $path
}

function Write-Manifest($Manifest, [string]$ManifestPath) {
    Save-JsonFile $Manifest $ManifestPath
}

function Get-CurrentEntry([string]$TargetPath, [string]$ExpectedHash) {
    $actual = Get-FolderHash $TargetPath
    if ($actual -ne $ExpectedHash) { throw "処理中に配置先が変更されました: $TargetPath" }
}

function Invoke-Rollback($Manifest, [string]$ManifestPath, $OldPaths, $StagePaths, [string]$ErrorMessage) {
    $errors = New-Object System.Collections.Generic.List[string]
    if ($null -ne $Manifest) {
        $manifestEntries = @($Manifest.entries)
        for ($entryIndex = $manifestEntries.Count - 1; $entryIndex -ge 0; $entryIndex--) {
            $entry = $manifestEntries[$entryIndex]
            try {
                $old = $OldPaths[[string]$entry.targetPath]
                if ($null -ne $old -and (Test-Path -LiteralPath $old -PathType Container)) {
                    Remove-Folder ([string]$entry.targetPath)
                    Move-Item -LiteralPath $old -Destination ([string]$entry.targetPath)
                } elseif ($entry.applied) {
                    Remove-Folder ([string]$entry.targetPath)
                }
            } catch { $errors.Add("$($entry.targetPath): $($_.Exception.Message)") }
        }
        foreach ($stage in @($StagePaths.Values)) { Remove-Folder ([string]$stage) }
        $Manifest.errorMessage = $ErrorMessage
        if ($errors.Count -eq 0) { $Manifest.status = 'RolledBack' } else { $Manifest.status = 'RollbackFailed' }
        try { Write-Manifest $Manifest $ManifestPath } catch { $errors.Add("履歴保存: $($_.Exception.Message)") }
    }
    return $errors
}

function New-SwitchEntries($Document, $Profile) {
    $managed = Get-ManagedFolders $Document
    $desired = Get-ProfileFolders $Profile
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($rootKey in @($managed.Keys | Sort-Object)) {
        $root = [string]$rootKey
        if (Test-InternalPath $root) { throw "SimpleProfiles配下は配置先にできません: $root" }
        if (Test-Path -LiteralPath $root -PathType Leaf) { throw "配置先がファイルです: $root" }
        Assert-SafeParent $root
        if ((Test-Path -LiteralPath $root -PathType Container) -and (Test-ReparsePoint $root)) { throw "再解析ポイントの配置先は使用できません: $root" }
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            $parent = Split-Path -Parent $root
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            New-Item -ItemType Directory -Path $root -Force | Out-Null
        }
        foreach ($name in @($managed[$root].Keys | Sort-Object)) {
            $target = Join-Path $root $name
            $beforeHash = Get-FolderHash $target
            $desiredItem = $null
            if ($desired.ContainsKey($root) -and $desired[$root].ContainsKey($name)) { $desiredItem = $desired[$root][$name] }
            if ($null -ne $desiredItem) {
                $source = Normalize-PathValue ([string]$desiredItem.sourcePath)
                if (Test-InternalPath $source) { throw "SimpleProfiles配下をコピー元にはできません: $source" }
                Assert-SafeTree $source
            }
            $entries.Add([pscustomobject]@{
                targetRoot = $root
                folderName = $name
                targetPath = $target
                beforeExisted = ($beforeHash -ne 'MISSING')
                beforeTreeHash = $beforeHash
                desiredExisted = ($null -ne $desiredItem)
                sourcePath = if ($null -ne $desiredItem) { Normalize-PathValue ([string]$desiredItem.sourcePath) } else { '' }
                afterTreeHash = if ($null -ne $desiredItem) { '' } else { 'MISSING' }
                applied = $false
            })
        }
    }
    if ($entries.Count -eq 0) { throw '管理対象のフォルダーがありません。' }
    return $entries
}

function Invoke-SwitchProfile($Document, $Profile) {
    $entries = @(New-SwitchEntries $Document $Profile)
    Write-Host ''
    Write-Host ("プロファイル「{0}」へ切り替えます。管理対象 {1} フォルダー" -f $Profile.name, $entries.Count) -ForegroundColor Cyan
    foreach ($entry in $entries) {
        $action = if ($entry.desiredExisted) { '配置' } else { '削除' }
        Write-Host ("  [{0}] {1}" -f $action, $entry.targetPath)
    }
    if ((Read-Host '続行しますか？ (Y/N)') -notmatch '^(Y|y|はい)$') { return }

    Ensure-BackupWritable
    $runId = [guid]::NewGuid().ToString('N')
    $runDirectory = New-RunDirectory $runId
    $manifestPath = Join-Path $runDirectory 'manifest.json'
    $manifest = [pscustomobject]@{
        schemaVersion = 2; runId = $runId; createdAt = (Get-Date).ToString('o'); operationKind = 'ProfileSwitch'; status = 'Prepared'
        profileId = [string]$Profile.id; profileName = [string]$Profile.name; sourceManifestPath = $null; errorMessage = $null; entries = @()
    }
    $oldPaths = @{}
    $stagePaths = @{}
    try {
        for ($i = 0; $i -lt $entries.Count; $i++) {
            $entry = $entries[$i]
            Write-Host ("バックアップ中 ({0}/{1}): {2}" -f ($i + 1), $entries.Count, $entry.targetPath)
            $backupRelative = Get-RelativeBackupPath $entry.folderName $i
            if ($entry.beforeExisted) { Copy-Folder $entry.targetPath (Get-AbsoluteBackupPath $runDirectory $backupRelative) }
            $entry | Add-Member -NotePropertyName backupRelativePath -NotePropertyValue $backupRelative
            $manifest.entries += [pscustomobject]@{
                targetRootPath = $entry.targetRoot; folderName = $entry.folderName; targetPath = $entry.targetPath; backupRelativePath = $backupRelative
                beforeExisted = $entry.beforeExisted; desiredExisted = $entry.desiredExisted; beforeTreeHash = $entry.beforeTreeHash; afterTreeHash = $entry.afterTreeHash; applied = $false
            }
        }
        Write-Manifest $manifest $manifestPath
        $manifest.status = 'InProgress'
        Write-Manifest $manifest $manifestPath

        for ($i = 0; $i -lt $entries.Count; $i++) {
            $entry = $entries[$i]
            if (-not $entry.desiredExisted) { continue }
            Write-Host ("新しいフォルダーを準備中 ({0}/{1}): {2}" -f ($i + 1), $entries.Count, $entry.targetPath)
            $stage = $entry.targetPath + '.configreplace-stage-' + $runId + '-' + $i
            $stagePaths[$entry.targetPath] = $stage
            Copy-Folder $entry.sourcePath $stage
            $entry.afterTreeHash = Get-FolderHash $stage
            if ($entry.afterTreeHash -eq 'MISSING') { throw "コピー先を確認できません: $stage" }
            $manifest.entries[$i].afterTreeHash = $entry.afterTreeHash
            Write-Manifest $manifest $manifestPath
        }

        for ($i = 0; $i -lt $manifest.entries.Count; $i++) {
            $entry = $manifest.entries[$i]
            Write-Host ("切替中 ({0}/{1}): {2}" -f ($i + 1), $manifest.entries.Count, $entry.targetPath)
            Get-CurrentEntry $entry.targetPath $entry.beforeTreeHash
            $old = $entry.targetPath + '.configreplace-old-' + $runId + '-' + $i
            $oldPaths[$entry.targetPath] = $old
            if (Test-Path -LiteralPath $entry.targetPath -PathType Container) { Move-Item -LiteralPath $entry.targetPath -Destination $old }
            if ($entry.desiredExisted) { Move-Item -LiteralPath $stagePaths[$entry.targetPath] -Destination $entry.targetPath }
            $entry.applied = $true
            Write-Manifest $manifest $manifestPath
        }
        $manifest.status = 'Completed'
        Write-Manifest $manifest $manifestPath
        foreach ($old in @($oldPaths.Values)) { Remove-Folder $old }
        foreach ($stage in @($stagePaths.Values)) { Remove-Folder $stage }
        Write-Host ("切替が完了しました。履歴: {0}" -f $manifestPath) -ForegroundColor Green
    } catch {
        $rollbackErrors = Invoke-Rollback $manifest $manifestPath $oldPaths $stagePaths $_.Exception.Message
        if ($rollbackErrors.Count -eq 0) { Write-Host ("切替に失敗しましたが、変更は元に戻しました: {0}" -f $_.Exception.Message) -ForegroundColor Red }
        else { Write-Host ("切替と復元に失敗しました。履歴を確認してください: {0}" -f $runDirectory) -ForegroundColor Red; $rollbackErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red } }
    }
}

function Get-HistoryItems {
    $items = New-Object System.Collections.Generic.List[object]
    foreach ($directory in @(Get-ChildItem -LiteralPath $script:BackupRoot -Directory | Sort-Object CreationTime -Descending)) {
        $manifestPath = Join-Path $directory.FullName 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }
        try {
            $manifest = (Get-Content -LiteralPath $manifestPath -Raw) | ConvertFrom-Json
            if ($manifest.schemaVersion -lt 2 -or @($manifest.entries).Count -eq 0) { continue }
            $valid = ($manifest.status -eq 'Completed')
            $message = ''
            foreach ($entry in @($manifest.entries)) {
                if ($entry.beforeExisted) {
                    $backupPath = Get-AbsoluteBackupPath $directory.FullName $entry.backupRelativePath
                    if (-not (Test-Path -LiteralPath $backupPath -PathType Container)) { $valid = $false; $message = "バックアップがありません: $($entry.targetPath)"; break }
                }
            }
            $items.Add([pscustomobject]@{ path = $manifestPath; manifest = $manifest; canRestore = $valid; message = $message })
        } catch {
            $items.Add([pscustomobject]@{ path = $manifestPath; manifest = $null; canRestore = $false; message = "履歴を読み取れません: $($_.Exception.Message)" })
        }
    }
    return $items
}

function Invoke-Restore($HistoryItem) {
    if (-not $HistoryItem.canRestore -or $null -eq $HistoryItem.manifest) { Write-Host 'この履歴は復元できません。' -ForegroundColor Yellow; return }
    $sourceManifest = $HistoryItem.manifest
    $sourceDirectory = Split-Path -Parent $HistoryItem.path
    $conflicts = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @($sourceManifest.entries)) {
        try { $actual = Get-FolderHash $entry.targetPath; if ($actual -ne $entry.afterTreeHash) { $conflicts.Add("外部変更: $($entry.targetPath)") } } catch { $conflicts.Add("確認失敗: $($entry.targetPath) - $($_.Exception.Message)") }
        if ($entry.beforeExisted) {
            try { $backupPath = Get-AbsoluteBackupPath $sourceDirectory $entry.backupRelativePath; if ((Get-FolderHash $backupPath) -ne $entry.beforeTreeHash) { $conflicts.Add("バックアップ破損: $($entry.targetPath)") } } catch { $conflicts.Add("バックアップ確認失敗: $($entry.targetPath)") }
        }
    }
    if ($conflicts.Count -gt 0) { Write-Host '外部変更またはバックアップ不備があるため復元を中止しました。' -ForegroundColor Red; $conflicts | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }; return }
    if ((Read-Host 'この履歴を復元しますか？ (Y/N)') -notmatch '^(Y|y|はい)$') { return }

    Ensure-BackupWritable
    $runId = [guid]::NewGuid().ToString('N')
    $runDirectory = New-RunDirectory $runId
    $manifestPath = Join-Path $runDirectory 'manifest.json'
    $manifest = [pscustomobject]@{
        schemaVersion = 2; runId = $runId; createdAt = (Get-Date).ToString('o'); operationKind = 'Restore'; status = 'Prepared'
        profileId = $sourceManifest.profileId; profileName = $sourceManifest.profileName; sourceManifestPath = $HistoryItem.path; errorMessage = $null; entries = @()
    }
    $oldPaths = @{}
    $stagePaths = @{}
    try {
        for ($i = 0; $i -lt @($sourceManifest.entries).Count; $i++) {
            $sourceEntry = $sourceManifest.entries[$i]
            $currentHash = Get-FolderHash $sourceEntry.targetPath
            $backupRelative = Get-RelativeBackupPath $sourceEntry.folderName $i
            if ($currentHash -ne 'MISSING') { Copy-Folder $sourceEntry.targetPath (Get-AbsoluteBackupPath $runDirectory $backupRelative) }
            $manifest.entries += [pscustomobject]@{
                targetRootPath = $sourceEntry.targetRootPath; folderName = $sourceEntry.folderName; targetPath = $sourceEntry.targetPath; backupRelativePath = $backupRelative
                beforeExisted = ($currentHash -ne 'MISSING'); desiredExisted = $sourceEntry.beforeExisted; beforeTreeHash = $currentHash; afterTreeHash = $sourceEntry.beforeTreeHash; applied = $false
            }
        }
        Write-Manifest $manifest $manifestPath
        $manifest.status = 'InProgress'
        Write-Manifest $manifest $manifestPath
        for ($i = 0; $i -lt @($sourceManifest.entries).Count; $i++) {
            $sourceEntry = $sourceManifest.entries[$i]
            if (-not $sourceEntry.beforeExisted) { continue }
            $stage = $sourceEntry.targetPath + '.configreplace-stage-' + $runId + '-' + $i
            $stagePaths[$sourceEntry.targetPath] = $stage
            Copy-Folder (Get-AbsoluteBackupPath $sourceDirectory $sourceEntry.backupRelativePath) $stage
        }
        for ($i = 0; $i -lt $manifest.entries.Count; $i++) {
            $entry = $manifest.entries[$i]
            Get-CurrentEntry $entry.targetPath $entry.beforeTreeHash
            $old = $entry.targetPath + '.configreplace-old-' + $runId + '-' + $i
            $oldPaths[$entry.targetPath] = $old
            if (Test-Path -LiteralPath $entry.targetPath -PathType Container) { Move-Item -LiteralPath $entry.targetPath -Destination $old }
            if ($entry.desiredExisted) { Move-Item -LiteralPath $stagePaths[$entry.targetPath] -Destination $entry.targetPath }
            $entry.applied = $true
            Write-Manifest $manifest $manifestPath
        }
        $manifest.status = 'Completed'
        Write-Manifest $manifest $manifestPath
        foreach ($old in @($oldPaths.Values)) { Remove-Folder $old }
        foreach ($stage in @($stagePaths.Values)) { Remove-Folder $stage }
        Write-Host ("復元が完了しました。履歴: {0}" -f $manifestPath) -ForegroundColor Green
    } catch {
        $rollbackErrors = Invoke-Rollback $manifest $manifestPath $oldPaths $stagePaths $_.Exception.Message
        Write-Host '復元に失敗しました。' -ForegroundColor Red
        if ($rollbackErrors.Count -eq 0) { Write-Host '変更は元に戻しました。' -ForegroundColor Yellow }
        else { Write-Host ("ロールバックにも失敗しました。バックアップを確認してください: {0}" -f $runDirectory) -ForegroundColor Red }
    }
}

function Select-Profile($Document, [string]$Prompt = 'プロファイル番号') {
    $profiles = @($Document.profiles)
    if ($profiles.Count -eq 0) { Write-Host 'プロファイルがありません。' -ForegroundColor Yellow; return $null }
    for ($i = 0; $i -lt $profiles.Count; $i++) { Write-Host ("{0}: {1} ({2} フォルダー)" -f ($i + 1), $profiles[$i].name, @($profiles[$i].folders).Count) }
    $input = Read-Host $Prompt
    $number = 0
    if (-not [int]::TryParse($input, [ref]$number) -or $number -lt 1 -or $number -gt $profiles.Count) { Write-Host '番号が不正です。' -ForegroundColor Yellow; return $null }
    return $profiles[$number - 1]
}

function Edit-Profile($Document, $ExistingProfile = $null) {
    if ($null -eq $ExistingProfile) {
        $profile = [pscustomobject]@{ id = [guid]::NewGuid().ToString('N'); name = ''; createdAt = (Get-Date).ToString('o'); updatedAt = (Get-Date).ToString('o'); folders = @() }
    } else { $profile = $ExistingProfile }
    $name = Read-Host ("プロファイル名 [{0}]" -f $profile.name)
    if ([string]::IsNullOrWhiteSpace($name)) { $name = [string]$profile.name }
    if ([string]::IsNullOrWhiteSpace($name)) { throw 'プロファイル名を入力してください。' }
    $oldFolders = @($profile.folders)
    $countText = Read-Host ("登録するフォルダー数 [{0}]" -f $oldFolders.Count)
    $count = 0
    if ([string]::IsNullOrWhiteSpace($countText)) { $count = $oldFolders.Count } elseif (-not [int]::TryParse($countText, [ref]$count) -or $count -lt 1) { throw 'フォルダー数が不正です。' }
    $folders = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $count; $i++) {
        $old = if ($i -lt $oldFolders.Count) { $oldFolders[$i] } else { $null }
        $targetDefault = if ($null -ne $old) { [string]$old.targetRoot } else { '' }
        $sourceDefault = if ($null -ne $old) { [string]$old.sourcePath } else { '' }
        $target = Read-Host ("[{0}] 配置先ルート [{1}]" -f ($i + 1), $targetDefault)
        if ([string]::IsNullOrWhiteSpace($target)) { $target = $targetDefault }
        $source = Read-Host ("[{0}] コピー元フォルダー [{1}]" -f ($i + 1), $sourceDefault)
        if ([string]::IsNullOrWhiteSpace($source)) { $source = $sourceDefault }
        $target = Normalize-PathValue $target
        $source = Normalize-PathValue $source
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "コピー元フォルダーがありません: $source" }
        if (Test-InternalPath $source) { throw 'SimpleProfiles配下をコピー元にはできません。' }
        Assert-SafeTree $source
        $leaf = Split-Path -Leaf $source.TrimEnd('\', '/')
        $folderName = Read-Host ("[{0}] 配置フォルダー名 [{1}]" -f ($i + 1), $leaf)
        if ([string]::IsNullOrWhiteSpace($folderName)) { $folderName = $leaf }
        if (-not (Test-SafeFolderName $folderName)) { throw "フォルダー名が不正です: $folderName" }
        if (@($folders | Where-Object { $_.targetRoot -ieq $target -and $_.folderName -ieq $folderName }).Count -gt 0) { throw "同じ配置先に同名フォルダーがあります: $target\$folderName" }
        $folders.Add([pscustomobject]@{ targetRoot = $target; sourcePath = $source; folderName = $folderName })
    }
    $profile.name = $name.Trim(); $profile.folders = $folders.ToArray(); $profile.updatedAt = (Get-Date).ToString('o')
    return $profile
}

function Show-HistoryMenu {
    $items = @(Get-HistoryItems)
    if ($items.Count -eq 0) { Write-Host '復元可能な履歴がありません。' -ForegroundColor Yellow; return }
    for ($i = 0; $i -lt $items.Count; $i++) {
        $item = $items[$i]
        $name = if ($null -eq $item.manifest) { '破損履歴' } else { "$($item.manifest.profileName) / $($item.manifest.operationKind)" }
        $state = if ($item.canRestore) { '復元可' } else { '復元不可' }
        Write-Host ("{0}: {1} {2} [{3}]" -f ($i + 1), $item.manifest.createdAt, $name, $state)
        if (-not [string]::IsNullOrWhiteSpace($item.message)) { Write-Host "    $($item.message)" -ForegroundColor Yellow }
    }
    $input = Read-Host '復元する履歴番号（0で戻る）'
    $number = 0
    if (-not [int]::TryParse($input, [ref]$number) -or $number -eq 0) { return }
    if ($number -lt 1 -or $number -gt $items.Count) { Write-Host '番号が不正です。' -ForegroundColor Yellow; return }
    Invoke-Restore $items[$number - 1]
}

function Show-MainMenu {
    Ensure-DataDirectories
    while ($true) {
        Clear-Host
        $document = Load-Document
        Write-Host 'ConfigReplace 簡易版' -ForegroundColor Cyan
        Write-Host "設定: $script:ConfigPath"
        Write-Host ''
        Write-Host '1. プロファイルを切り替える'
        Write-Host '2. プロファイルを新規作成する'
        Write-Host '3. プロファイルを編集する'
        Write-Host '4. プロファイルを削除する'
        Write-Host '5. 切替履歴から復元する'
        Write-Host '6. profiles.jsonをメモ帳で開く'
        Write-Host '0. 終了する'
        $choiceValue = Read-Host '番号'
        if ($null -eq $choiceValue) { return }
        $choice = $choiceValue.Trim()
        if ([string]::IsNullOrWhiteSpace($choice)) { return }
        try {
            switch ($choice) {
                '1' { $profile = Select-Profile $document; if ($null -ne $profile) { Invoke-SwitchProfile $document $profile }; Read-Host 'Enterで戻る' | Out-Null }
                '2' { $profile = Edit-Profile $document; $document.profiles = @($document.profiles) + $profile; Save-JsonFile $document $script:ConfigPath; Write-Host '保存しました。' -ForegroundColor Green; Read-Host 'Enterで戻る' | Out-Null }
                '3' { $profile = Select-Profile $document; if ($null -ne $profile) { $edited = Edit-Profile $document $profile; $index = [array]::IndexOf(@($document.profiles), $profile); $document.profiles[$index] = $edited; Save-JsonFile $document $script:ConfigPath; Write-Host '保存しました。' -ForegroundColor Green }; Read-Host 'Enterで戻る' | Out-Null }
                '4' { $profile = Select-Profile $document; if ($null -ne $profile -and (Read-Host "「$($profile.name)」を削除しますか？ (Y/N)") -match '^(Y|y|はい)$') { $document.profiles = @($document.profiles | Where-Object { $_.id -ne $profile.id }); Save-JsonFile $document $script:ConfigPath; Write-Host '削除しました。' -ForegroundColor Green }; Read-Host 'Enterで戻る' | Out-Null }
                '5' { Show-HistoryMenu; Read-Host 'Enterで戻る' | Out-Null }
                '6' { Start-Process notepad.exe $script:ConfigPath }
                '0' { return }
                default { Write-Host '番号が不正です。' -ForegroundColor Yellow; Start-Sleep -Seconds 1 }
            }
        } catch { Write-Host "エラー: $($_.Exception.Message)" -ForegroundColor Red; Read-Host 'Enterで戻る' | Out-Null }
    }
}

Show-MainMenu
