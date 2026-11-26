<#
.SYNOPSIS
  Builds Tundy and publishes it to its public GitHub repository: the source code (AGPL-3.0 requires it), plus the
  Dalamud plugin repository files (pluginmaster.json and plugins/Tundy/latest.zip), all in one commit.

.DESCRIPTION
  Uses GitHub CLI's own sign-in (gh auth login). Creates the repository the first time.
  Raise <Version> in Tundy.csproj before publishing an update, or Dalamud won't offer it.

.EXAMPLE
  .\repo\publish.ps1
  .\repo\publish.ps1 -Message "Tundy 0.3.1: fix ..."
#>
param(
    [string]$Owner = "Clair-songs",
    [string]$Repo = "Tundy",
    [string]$Branch = "main",
    [string]$Message = ""
)
# gh writes expected "Not Found" answers to stderr; exit codes are checked instead.
$ErrorActionPreference = "Continue"
$gh ="$env:ProgramFiles\GitHub CLI\gh.exe"
$project = Split-Path -Parent $PSScriptRoot
$tmp = Join-Path $env:TEMP "tundy-publish"
New-Item -ItemType Directory -Force $tmp | Out-Null

# Each call is tried up to 4 times (waiting 5, 10, 20 s): a network hiccup or GitHub briefly slowing down many uploads
# in a row shouldn't stop a publish halfway. Nothing goes live until the last step moves the branch.
function Invoke-Gh([string]$method, [string]$path, $body) {
    $file = $null
    if ($null -ne $body) {
        $file = Join-Path $tmp ("body-" + [guid]::NewGuid().ToString("N") + ".json")
        [IO.File]::WriteAllText($file, ($body | ConvertTo-Json -Depth 10 -Compress), (New-Object Text.UTF8Encoding $false))
    }
    try {
        foreach ($try in 1..4) {
            $out = if ($null -eq $file) { & $gh api --method $method $path } else { & $gh api --method $method $path --input $file }
            if ($LASTEXITCODE -eq 0) { return $out | ConvertFrom-Json }
            if ($try -lt 4) {
                $wait = 5 * [Math]::Pow(2, $try - 1)
                Write-Host "  GitHub API $method $path failed, trying again in $wait s..."
                Start-Sleep -Seconds $wait
            }
        }
        throw "GitHub API $method $path failed."
    } finally { if ($null -ne $file) { Remove-Item $file -Force } }
}

# 1. Build
Write-Host "Building Tundy..."
& dotnet build (Join-Path $project "Tundy.csproj") -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$package = Join-Path $project "bin\Release\Tundy"
$manifest = Get-Content (Join-Path $package "Tundy.json") -Raw | ConvertFrom-Json

# 2. pluginmaster.json
$raw = "https://raw.githubusercontent.com/$Owner/$Repo/$Branch"
$zipUrl = "$raw/plugins/Tundy/latest.zip"
$entry = [ordered]@{
    Author = $manifest.Author; Name = $manifest.Name; InternalName = $manifest.InternalName
    AssemblyVersion = $manifest.AssemblyVersion; Description = $manifest.Description; Punchline = $manifest.Punchline
    ApplicableVersion = "any"; RepoUrl = "https://github.com/$Owner/$Repo"; Tags = @($manifest.Tags)
    DalamudApiLevel = $manifest.DalamudApiLevel; LoadRequiredState = 0; LoadSync = $false; LoadPriority = 0
    CanUnloadAsync = $false; AcceptsFeedback = $false; IsHide = $false; IsTestingExclusive = $false; DownloadCount = 0
    LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    DownloadLinkInstall = $zipUrl; DownloadLinkUpdate = $zipUrl; DownloadLinkTesting = $zipUrl
    IconUrl = $manifest.IconUrl
}
$pluginmaster = Join-Path $tmp "pluginmaster.json"
[IO.File]::WriteAllText($pluginmaster, (ConvertTo-Json -InputObject @($entry) -Depth 5), (New-Object Text.UTF8Encoding $false))

# 3. Files: the source (not build output), plus the plugin repository files
$files = [ordered]@{}
# Never the build output or the local Git history (.git).
Get-ChildItem $project -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\(bin|obj|\.vs|\.git)\\' } | ForEach-Object {
    $files[$_.FullName.Substring($project.Length + 1).Replace('\', '/')] = $_.FullName
}
$files["pluginmaster.json"] = $pluginmaster
$files["plugins/Tundy/latest.zip"] = Join-Path $package "latest.zip"

# 4. Repository (created once, with a first commit so files can be added in one go)
# A network hiccup also fails this check, so try a few times before deciding the repository doesn't exist.
$exists = $false
foreach ($try in 1..3) {
    $null = & $gh api "repos/$Owner/$Repo" 2>$null
    if ($LASTEXITCODE -eq 0) { $exists = $true; break }
    Start-Sleep -Seconds 3
}
if (-not $exists) {
    & $gh repo create "$Owner/$Repo" --public --add-readme --description "Tundy: a Dalamud (FFXIV) plugin that plays songs people send you by /tell, for you and your Lightless pairs. TellTunes + SoundyQ + Soundy's import tools. AGPL-3.0." | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't create the repository." }
    Write-Host "Created https://github.com/$Owner/$Repo"
    Start-Sleep -Seconds 3
}

# 5. One commit with every file
$head = (Invoke-Gh GET "repos/$Owner/$Repo/git/ref/heads/$Branch" $null).object.sha
$baseTree = (Invoke-Gh GET "repos/$Owner/$Repo/git/commits/$head" $null).tree.sha
$tree = @()
foreach ($path in $files.Keys) {
    $blob = Invoke-Gh POST "repos/$Owner/$Repo/git/blobs" @{ content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($files[$path])); encoding = "base64" }
    $tree += @{ path = $path; mode = "100644"; type = "blob"; sha = $blob.sha }
    Write-Host "  $path"
}
$newTree = Invoke-Gh POST "repos/$Owner/$Repo/git/trees" @{ base_tree = $baseTree; tree = $tree }
if ($Message -eq "") { $Message = "Tundy $($manifest.AssemblyVersion)" }
$commit = Invoke-Gh POST "repos/$Owner/$Repo/git/commits" @{ message = $Message; tree = $newTree.sha; parents = @($head) }
$null = Invoke-Gh PATCH "repos/$Owner/$Repo/git/refs/heads/$Branch" @{ sha = $commit.sha }

Write-Host ""
Write-Host "Published Tundy $($manifest.AssemblyVersion): https://github.com/$Owner/$Repo"
Write-Host "Plugin repository URL for /xlsettings: $raw/pluginmaster.json"
