param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$ProductId,
    [string]$TenantId,
    [string]$ClientId,
    [string]$ClientSecret,
    [string]$ListingDirectory,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ListingDirectory)) {
    $ListingDirectory = Join-Path $repoRoot "store-listing"
}

function Assert-TextLength {
    param(
        [AllowEmptyString()]
        [string]$Value,
        [int]$Maximum,
        [string]$Field,
        [switch]$Required
    )

    if ($Required -and [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Field must not be empty."
    }
    if ($null -ne $Value -and $Value.Length -gt $Maximum) {
        throw "$Field exceeds the $Maximum character Store limit ($($Value.Length))."
    }
}

function Set-ObjectProperty {
    param(
        [Parameter(Mandatory)]
        [psobject]$Object,
        [Parameter(Mandatory)]
        [string]$Name,
        [AllowNull()]
        $Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $property.Value = $Value
    }
}

function Invoke-DevCenterRequest {
    param(
        [Parameter(Mandatory)]
        [string]$Method,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$AccessToken,
        [Parameter(Mandatory)]
        [string]$DirectoryTenantId,
        [AllowNull()]
        $Body
    )

    $parameters = @{
        Method = $Method
        Uri = "https://manage.devcenter.microsoft.com$Path"
        Headers = @{
            Authorization = "Bearer $AccessToken"
            TenantId = $DirectoryTenantId
        }
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 100 -Compress
    }

    Invoke-RestMethod @parameters
}

function Get-SafeBundlePath {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    if ([IO.Path]::IsPathRooted($RelativePath)) {
        throw "Store asset path must be relative: $RelativePath"
    }

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Store asset path escapes bundle root: $RelativePath"
    }
    $fullPath
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use three-part semantic format: $Version"
}

$manifestPath = Join-Path $ListingDirectory "listing.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Store listing manifest not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$manifest.SchemaVersion -ne 1) {
    throw "Unsupported Store listing schema: $($manifest.SchemaVersion)"
}
if ([string]$manifest.ReleaseVersion -ne $Version) {
    throw "Store release notes target $($manifest.ReleaseVersion), but workflow is building $Version."
}

$screenshots = @($manifest.Screenshots)
if ($screenshots.Count -lt 4 -or $screenshots.Count -gt 10) {
    throw "Store listing must contain 4-10 screenshots; found $($screenshots.Count)."
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem
$resolvedScreenshots = @()
foreach ($screenshot in $screenshots) {
    $relativeSource = [string]$screenshot.Source
    $fileName = [string]$screenshot.FileName
    if ([string]::IsNullOrWhiteSpace($relativeSource) -or
        [string]::IsNullOrWhiteSpace($fileName) -or
        [IO.Path]::GetExtension($fileName) -ine ".png" -or
        [IO.Path]::GetFileName($fileName) -ne $fileName) {
        throw "Invalid Store screenshot entry: $($screenshot | ConvertTo-Json -Compress)"
    }

    $sourcePath = [IO.Path]::GetFullPath((Join-Path $ListingDirectory $relativeSource))
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Store screenshot not found: $sourcePath"
    }
    $file = Get-Item -LiteralPath $sourcePath
    if ($file.Length -gt 50MB) {
        throw "Store screenshot exceeds 50 MB: $sourcePath"
    }

    $image = [System.Drawing.Image]::FromFile($sourcePath)
    try {
        if ($image.Width -lt 1366 -or $image.Height -lt 768) {
            throw "Desktop Store screenshot must be at least 1366x768: $sourcePath is $($image.Width)x$($image.Height)."
        }
        $resolvedScreenshots += [pscustomobject]@{
            SourcePath = $sourcePath
            FileName = $fileName
        }
    }
    finally {
        $image.Dispose()
    }
}

$listings = @($manifest.Listings.PSObject.Properties)
$expectedLocales = @("en-us", "ru-ru")
$actualLocales = @($listings.Name | ForEach-Object { $_.ToLowerInvariant() })
foreach ($locale in $expectedLocales) {
    if ($locale -notin $actualLocales) {
        throw "Required Store listing locale is missing: $locale"
    }
}

foreach ($listingProperty in $listings) {
    $locale = $listingProperty.Name.ToLowerInvariant()
    $listing = $listingProperty.Value
    Assert-TextLength -Value ([string]$listing.Description) -Maximum 10000 `
        -Field "$locale Description" -Required
    Assert-TextLength -Value ([string]$listing.ShortDescription) -Maximum 1000 `
        -Field "$locale ShortDescription" -Required
    Assert-TextLength -Value ([string]$listing.ReleaseNotes) -Maximum 1500 `
        -Field "$locale ReleaseNotes" -Required
    Assert-TextLength -Value ([string]$listing.LicenseTerms) -Maximum 10000 `
        -Field "$locale LicenseTerms" -Required
    Assert-TextLength -Value ([string]$listing.CopyrightAndTrademarkInfo) -Maximum 200 `
        -Field "$locale CopyrightAndTrademarkInfo"
    Assert-TextLength -Value ([string]$listing.DevStudio) -Maximum 255 `
        -Field "$locale DevStudio"

    $features = @($listing.Features)
    if ($features.Count -eq 0 -or $features.Count -gt 20) {
        throw "$locale must contain 1-20 product features."
    }
    for ($i = 0; $i -lt $features.Count; $i++) {
        Assert-TextLength -Value ([string]$features[$i]) -Maximum 200 `
            -Field "$locale Features[$i]" -Required
    }

    $keywords = @($listing.Keywords)
    if ($keywords.Count -eq 0 -or $keywords.Count -gt 7) {
        throw "$locale must contain 1-7 keywords."
    }
    for ($i = 0; $i -lt $keywords.Count; $i++) {
        Assert-TextLength -Value ([string]$keywords[$i]) -Maximum 40 `
            -Field "$locale Keywords[$i]" -Required
    }
    $keywordWords = @($keywords | ForEach-Object { ([string]$_ -split '\s+') } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($keywordWords.Count -gt 21) {
        throw "$locale keywords exceed the 21-word combined limit."
    }
}

Write-Host "Validated Store listing ${Version}: $($listings.Count) locales, $($screenshots.Count) screenshots."
if ($ValidateOnly) {
    return
}

if ([string]::IsNullOrWhiteSpace($ProductId)) {
    $ProductId = [Environment]::GetEnvironmentVariable("STORE_PRODUCT_ID")
}
if ([string]::IsNullOrWhiteSpace($TenantId)) {
    $TenantId = [Environment]::GetEnvironmentVariable("STORE_TENANT_ID")
}
if ([string]::IsNullOrWhiteSpace($ClientId)) {
    $ClientId = [Environment]::GetEnvironmentVariable("STORE_CLIENT_ID")
}
if ([string]::IsNullOrWhiteSpace($ClientSecret)) {
    $ClientSecret = [Environment]::GetEnvironmentVariable("STORE_CLIENT_SECRET")
}

$requiredSecrets = @{
    ProductId = $ProductId
    TenantId = $TenantId
    ClientId = $ClientId
    ClientSecret = $ClientSecret
}
$missing = @($requiredSecrets.GetEnumerator() |
    Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) } |
    ForEach-Object { $_.Key })
if ($missing.Count -gt 0) {
    throw "Missing Store publishing values: $($missing -join ', ')"
}

$tokenResponse = Invoke-RestMethod -Method Post `
    -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -ContentType "application/x-www-form-urlencoded" `
    -Body @{
        client_id = $ClientId
        client_secret = $ClientSecret
        grant_type = "client_credentials"
        scope = "https://manage.devcenter.microsoft.com/.default"
    }
$accessToken = [string]$tokenResponse.access_token
if ([string]::IsNullOrWhiteSpace($accessToken)) {
    throw "Microsoft Entra token response did not contain an access token."
}

$application = $null
$applicationId = $null
$submissionId = $null
for ($attempt = 1; $attempt -le 7; $attempt++) {
    $application = Invoke-DevCenterRequest -Method Get `
        -Path "/v1.0/my/applications/$ProductId" `
        -AccessToken $accessToken -DirectoryTenantId $TenantId -Body $null
    $applicationId = [string]$application.Id
    $pendingProperty = $application.PSObject.Properties["PendingApplicationSubmission"]
    if ($null -ne $pendingProperty -and $null -ne $pendingProperty.Value) {
        $submissionId = [string]$pendingProperty.Value.Id
    }
    if (-not [string]::IsNullOrWhiteSpace($applicationId) -and
        -not [string]::IsNullOrWhiteSpace($submissionId)) {
        break
    }
    if ($attempt -lt 7) {
        Start-Sleep -Seconds 5
    }
}
if ([string]::IsNullOrWhiteSpace($applicationId) -or
    [string]::IsNullOrWhiteSpace($submissionId)) {
    throw "Partner Center has no pending draft. Upload the package with msstore publish -nc first."
}

$submissionPath = "/v1.0/my/applications/$applicationId/submissions/$submissionId"
$submission = Invoke-DevCenterRequest -Method Get -Path $submissionPath `
    -AccessToken $accessToken -DirectoryTenantId $TenantId -Body $null
if ([string]::IsNullOrWhiteSpace([string]$submission.FileUploadUrl)) {
    throw "Pending Store submission has no file upload URL."
}

foreach ($listingProperty in $listings) {
    $locale = $listingProperty.Name.ToLowerInvariant()
    $sourceListing = $listingProperty.Value
    $remoteListingProperty = $submission.Listings.PSObject.Properties |
        Where-Object { $_.Name.Equals($locale, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $remoteListingProperty -or $null -eq $remoteListingProperty.Value.BaseListing) {
        throw "Partner Center draft does not contain required locale '$locale'. Add it once in Partner Center before using automation."
    }

    $baseListing = $remoteListingProperty.Value.BaseListing
    Set-ObjectProperty -Object $baseListing -Name Description `
        -Value ([string]$sourceListing.Description)
    Set-ObjectProperty -Object $baseListing -Name ShortDescription `
        -Value ([string]$sourceListing.ShortDescription)
    Set-ObjectProperty -Object $baseListing -Name Features `
        -Value @($sourceListing.Features)
    Set-ObjectProperty -Object $baseListing -Name Keywords `
        -Value @($sourceListing.Keywords)
    Set-ObjectProperty -Object $baseListing -Name ReleaseNotes `
        -Value ([string]$sourceListing.ReleaseNotes)
    Set-ObjectProperty -Object $baseListing -Name LicenseTerms `
        -Value ([string]$sourceListing.LicenseTerms)
    Set-ObjectProperty -Object $baseListing -Name CopyrightAndTrademarkInfo `
        -Value ([string]$sourceListing.CopyrightAndTrademarkInfo)
    Set-ObjectProperty -Object $baseListing -Name DevStudio `
        -Value ([string]$sourceListing.DevStudio)

    $nonScreenshotImages = @($baseListing.Images |
        Where-Object { [string]$_.ImageType -ne "Screenshot" })
    $newScreenshots = @($resolvedScreenshots | ForEach-Object {
        [pscustomobject]@{
            FileName = Join-Path $locale $_.FileName
            FileStatus = "PendingUpload"
            Id = $null
            ImageType = "Screenshot"
        }
    })
    Set-ObjectProperty -Object $baseListing -Name Images `
        -Value @($nonScreenshotImages + $newScreenshots)
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ("Captail-StoreListing-" + [Guid]::NewGuid().ToString("N"))
$downloadedBundle = Join-Path $temporaryRoot "current-upload.zip"
$bundleRoot = Join-Path $temporaryRoot "bundle"
$updatedBundle = Join-Path $temporaryRoot "updated-upload.zip"

try {
    New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
    $downloadUrl = ([string]$submission.FileUploadUrl).Replace("+", "%2B")
    Invoke-WebRequest -Uri $downloadUrl `
        -OutFile $downloadedBundle -UseBasicParsing `
        -MaximumRetryCount 3 -RetryIntervalSec 2
    Expand-Archive -LiteralPath $downloadedBundle -DestinationPath $bundleRoot -Force

    foreach ($listingProperty in $listings) {
        $locale = $listingProperty.Name.ToLowerInvariant()
        foreach ($screenshot in $resolvedScreenshots) {
            $relativePath = Join-Path $locale $screenshot.FileName
            $destination = Get-SafeBundlePath -Root $bundleRoot -RelativePath $relativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $screenshot.SourcePath -Destination $destination -Force
        }
    }

    $updatedSubmission = Invoke-DevCenterRequest -Method Put -Path $submissionPath `
        -AccessToken $accessToken -DirectoryTenantId $TenantId -Body $submission
    $uploadUrl = if ([string]::IsNullOrWhiteSpace([string]$updatedSubmission.FileUploadUrl)) {
        [string]$submission.FileUploadUrl
    }
    else {
        [string]$updatedSubmission.FileUploadUrl
    }

    [IO.Compression.ZipFile]::CreateFromDirectory(
        $bundleRoot,
        $updatedBundle,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    $uploadUrl = $uploadUrl.Replace("+", "%2B")
    Invoke-WebRequest -Method Put -Uri $uploadUrl -InFile $updatedBundle `
        -ContentType "application/zip" `
        -Headers @{
            "x-ms-blob-type" = "BlockBlob"
            "x-ms-version" = "2021-12-02"
        } `
        -UseBasicParsing -MaximumRetryCount 3 -RetryIntervalSec 2 | Out-Null
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Synchronized Store listing $Version for $($listings.Count) locales. Draft remains uncommitted."
