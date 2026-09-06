param(
    [string]$Revision = "main",
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\Captail\Assets\GameCatalog.json")
)

$ErrorActionPreference = "Stop"
$repository = "https://github.com/markterence/discord-quest-completer.git"

if ($Revision -eq "main") {
    $remote = git ls-remote $repository refs/heads/main
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remote)) {
        throw "Could not resolve game catalog revision."
    }
    $Revision = ($remote -split "\s+")[0]
}

if ($Revision -notmatch "^[0-9a-f]{40}$") {
    throw "Revision must be a 40-character Git commit SHA or 'main'."
}

$sourceUrl = "https://raw.githubusercontent.com/markterence/discord-quest-completer/$Revision/src/assets/gamelist.json"
$temporaryPath = Join-Path ([IO.Path]::GetTempPath()) "captail-gamelist-$Revision.json"

try {
    Invoke-WebRequest -UseBasicParsing $sourceUrl -OutFile $temporaryPath
    $sourceGames = Get-Content -Raw $temporaryPath | ConvertFrom-Json
    $rows = foreach ($game in $sourceGames) {
        $name = [string]$game.name
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $executables = @(
            $game.executables |
                Where-Object {
                    $_.os -eq "win32" -and
                    -not $_.is_launcher -and
                    $_.name -match "(?i)\.exe$"
                } |
                ForEach-Object {
                    [IO.Path]::GetFileName(
                        ([string]$_.name).Replace("/", "\")).ToLowerInvariant()
                } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Sort-Object -Unique
        )
        if ($executables.Count -gt 0) {
            [pscustomobject]@{
                Name = $name.Trim()
                Executables = $executables
            }
        }
    }

    $games = @(
        $rows |
            Group-Object Name |
            ForEach-Object {
                [ordered]@{
                    n = $_.Group[0].Name
                    e = @(
                        $_.Group.Executables |
                            ForEach-Object { $_ } |
                            Sort-Object -Unique
                    )
                }
            } |
            Sort-Object { $_.n }
    )

    $catalog = [ordered]@{
        source = $sourceUrl
        revision = $Revision
        games = $games
    }
    $json = $catalog | ConvertTo-Json -Depth 5 -Compress
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) |
        Out-Null
    [IO.File]::WriteAllText(
        $resolvedOutput,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Write-Output "Game catalog: $($games.Count) games, revision $Revision"
    Write-Output "Output: $resolvedOutput"
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}
