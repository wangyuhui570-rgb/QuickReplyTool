Set-StrictMode -Version Latest

function Assert-ReleaseSafetyPathExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Description = 'Required path'
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description was not found: $Path"
    }
}

function Get-ReleaseSafetyFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Description = 'Required path'
    )

    Assert-ReleaseSafetyPathExists -Path $Path -Description $Description
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-ReleaseSafetyFileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = Get-ReleaseSafetyFullPath -Path $Path -Description 'File'
    return (Get-FileHash -Path $fullPath -Algorithm SHA256).Hash
}

function Read-ReleaseSafetyJsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = Get-ReleaseSafetyFullPath -Path $Path -Description 'JSON file'
    return Get-Content -Raw -Path $fullPath | ConvertFrom-Json
}

function Get-ReleaseSafetyPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,
        [Parameter(Mandatory = $true)]
        [string]$PropertyName
    )

    $property = $InputObject.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-ReleaseSafetyCanonicalJson {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    return ($Value | ConvertTo-Json -Depth 10 -Compress)
}

function ConvertTo-ReleaseSafetyStableString {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($Value -is [datetime]) {
        return $Value.ToString('O')
    }

    if ($Value -is [datetimeoffset]) {
        return $Value.ToString('O')
    }

    if ($null -eq $Value) {
        return ''
    }

    return [string]$Value
}

function Get-ReleaseSafetyDefaultSigningSecret {
    return 'quickreply-smoke-signing-secret-v1'
}

function New-ReleaseSafetySignaturePayload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArtifactPath,
        [string]$MetadataPath = '',
        [string]$SignerName = 'QuickReplyTool Smoke Signer',
        [string]$KeyId = 'quickreply-smoke-key-v1',
        [string]$SignatureAlgorithm = 'HMACSHA256'
    )

    $artifactFullPath = Get-ReleaseSafetyFullPath -Path $ArtifactPath -Description 'Artifact'
    $artifactInfo = Get-Item -LiteralPath $artifactFullPath
    $metadataPayload = $null

    if (-not [string]::IsNullOrWhiteSpace($MetadataPath)) {
        $metadataFullPath = Get-ReleaseSafetyFullPath -Path $MetadataPath -Description 'Package metadata'
        $metadataInfo = Get-Item -LiteralPath $metadataFullPath
        $metadataPayload = [ordered]@{
            fileName = $metadataInfo.Name
            sha256 = Get-ReleaseSafetyFileSha256 -Path $metadataFullPath
            sizeBytes = [int64]$metadataInfo.Length
        }
    }

    return [ordered]@{
        schemaVersion = 'quickreply.release-signature.v1'
        smokeOnly = $true
        signatureAlgorithm = $SignatureAlgorithm
        keyId = $KeyId
        signerName = $SignerName
        generatedAt = (Get-Date).ToString('O')
        artifact = [ordered]@{
            fileName = $artifactInfo.Name
            sha256 = Get-ReleaseSafetyFileSha256 -Path $artifactFullPath
            sizeBytes = [int64]$artifactInfo.Length
        }
        metadata = $metadataPayload
    }
}

function Get-ReleaseSafetySignatureValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$PayloadRecord,
        [Parameter(Mandatory = $true)]
        [string]$SigningSecret
    )

    $normalizedMetadata = $null
    if ($null -ne $PayloadRecord.metadata) {
        $normalizedMetadata = [ordered]@{
            fileName = [string]$PayloadRecord.metadata.fileName
            sha256 = [string]$PayloadRecord.metadata.sha256
            sizeBytes = [int64]$PayloadRecord.metadata.sizeBytes
        }
    }

    $normalizedPayload = [ordered]@{
        schemaVersion = ConvertTo-ReleaseSafetyStableString -Value $PayloadRecord.schemaVersion
        smokeOnly = [bool]$PayloadRecord.smokeOnly
        signatureAlgorithm = ConvertTo-ReleaseSafetyStableString -Value $PayloadRecord.signatureAlgorithm
        keyId = ConvertTo-ReleaseSafetyStableString -Value $PayloadRecord.keyId
        signerName = ConvertTo-ReleaseSafetyStableString -Value $PayloadRecord.signerName
        generatedAt = ConvertTo-ReleaseSafetyStableString -Value $PayloadRecord.generatedAt
        artifact = [ordered]@{
            fileName = ConvertTo-ReleaseSafetyStableString -Value $PayloadRecord.artifact.fileName
            sha256 = ConvertTo-ReleaseSafetyStableString -Value $PayloadRecord.artifact.sha256
            sizeBytes = [int64]$PayloadRecord.artifact.sizeBytes
        }
        metadata = $normalizedMetadata
    }

    $payloadJson = ConvertTo-ReleaseSafetyCanonicalJson -Value $normalizedPayload
    $secretBytes = [System.Text.Encoding]::UTF8.GetBytes($SigningSecret)
    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payloadJson)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($secretBytes)

    try {
        $signatureBytes = $hmac.ComputeHash($payloadBytes)
    }
    finally {
        $hmac.Dispose()
    }

    return [Convert]::ToBase64String($signatureBytes)
}

function Test-ReleaseSafetySignatureRecord {
    param(
        [Parameter(Mandatory = $true)]
        [object]$SignatureRecord,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactPath,
        [string]$MetadataPath = '',
        [Parameter(Mandatory = $true)]
        [string]$SigningSecret
    )

    $errors = [System.Collections.Generic.List[string]]::new()
    $artifactFullPath = Get-ReleaseSafetyFullPath -Path $ArtifactPath -Description 'Artifact'
    $artifactInfo = Get-Item -LiteralPath $artifactFullPath
    $artifactHash = Get-ReleaseSafetyFileSha256 -Path $artifactFullPath

    if ($SignatureRecord.payload.artifact.fileName -ne $artifactInfo.Name) {
        $errors.Add("Artifact file name mismatch: expected $($SignatureRecord.payload.artifact.fileName), got $($artifactInfo.Name)")
    }

    if ($SignatureRecord.payload.artifact.sha256 -ne $artifactHash) {
        $errors.Add("Artifact sha256 mismatch: expected $($SignatureRecord.payload.artifact.sha256), got $artifactHash")
    }

    if ([int64]$SignatureRecord.payload.artifact.sizeBytes -ne [int64]$artifactInfo.Length) {
        $errors.Add("Artifact size mismatch: expected $($SignatureRecord.payload.artifact.sizeBytes), got $($artifactInfo.Length)")
    }

    $metadataHash = ''
    if ($null -ne $SignatureRecord.payload.metadata) {
        if ([string]::IsNullOrWhiteSpace($MetadataPath)) {
            $errors.Add('Signature requires package metadata, but no MetadataPath was supplied.')
        }
        else {
            $metadataFullPath = Get-ReleaseSafetyFullPath -Path $MetadataPath -Description 'Package metadata'
            $metadataInfo = Get-Item -LiteralPath $metadataFullPath
            $metadataHash = Get-ReleaseSafetyFileSha256 -Path $metadataFullPath

            if ($SignatureRecord.payload.metadata.fileName -ne $metadataInfo.Name) {
                $errors.Add("Metadata file name mismatch: expected $($SignatureRecord.payload.metadata.fileName), got $($metadataInfo.Name)")
            }

            if ($SignatureRecord.payload.metadata.sha256 -ne $metadataHash) {
                $errors.Add("Metadata sha256 mismatch: expected $($SignatureRecord.payload.metadata.sha256), got $metadataHash")
            }

            if ([int64]$SignatureRecord.payload.metadata.sizeBytes -ne [int64]$metadataInfo.Length) {
                $errors.Add("Metadata size mismatch: expected $($SignatureRecord.payload.metadata.sizeBytes), got $($metadataInfo.Length)")
            }
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($MetadataPath)) {
        $errors.Add('MetadataPath was supplied, but the signature payload does not describe package metadata.')
    }

    $expectedSignature = Get-ReleaseSafetySignatureValue -PayloadRecord $SignatureRecord.payload -SigningSecret $SigningSecret
    if ($SignatureRecord.signature -ne $expectedSignature) {
        $errors.Add('Signature digest mismatch.')
    }

    return [pscustomobject]@{
        isValid = ($errors.Count -eq 0)
        errors = @($errors)
        expectedSignature = $expectedSignature
        artifactSha256 = $artifactHash
        metadataSha256 = $metadataHash
    }
}

function Resolve-ReleaseSafetyPackageKind {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Metadata
    )

    foreach ($propertyName in @('packageKind', 'latestAppPackageKind')) {
        $value = Get-ReleaseSafetyPropertyValue -InputObject $Metadata -PropertyName $propertyName
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [string]$value
        }
    }

    return 'unknown'
}

function Resolve-ReleaseSafetyVersionLabel {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Metadata
    )

    foreach ($propertyName in @('patchVersion', 'appVersion', 'latestAppVersion', 'baseProjectVersion', 'versionLabel')) {
        $value = Get-ReleaseSafetyPropertyValue -InputObject $Metadata -PropertyName $propertyName
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [string]$value
        }
    }

    return 'unknown-version'
}

function Resolve-ReleaseSafetyReleaseChannel {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Metadata
    )

    foreach ($propertyName in @('releaseChannel', 'latestAppReleaseChannel')) {
        $value = Get-ReleaseSafetyPropertyValue -InputObject $Metadata -PropertyName $propertyName
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [string]$value
        }
    }

    return 'stable'
}

function Test-ReleaseSafetyRollbackPlan {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Plan
    )

    $errors = [System.Collections.Generic.List[string]]::new()

    if ([string]::IsNullOrWhiteSpace($Plan.schemaVersion)) {
        $errors.Add('Rollback plan schemaVersion is required.')
    }

    if ([string]::IsNullOrWhiteSpace($Plan.rollbackMode)) {
        $errors.Add('Rollback plan rollbackMode is required.')
    }

    if ($null -eq $Plan.targetPackage) {
        $errors.Add('Rollback plan targetPackage is required.')
    }
    else {
        foreach ($propertyName in @('packageKind', 'versionLabel', 'artifactFileName', 'artifactSha256', 'metadataFileName', 'metadataSha256')) {
            if ([string]::IsNullOrWhiteSpace($Plan.targetPackage.$propertyName)) {
                $errors.Add("Rollback plan targetPackage.$propertyName is required.")
            }
        }
    }

    if ($null -eq $Plan.rollbackSteps -or @($Plan.rollbackSteps).Count -lt 4) {
        $errors.Add('Rollback plan must define at least four rollbackSteps.')
    }

    return [pscustomobject]@{
        isValid = ($errors.Count -eq 0)
        errors = @($errors)
    }
}
