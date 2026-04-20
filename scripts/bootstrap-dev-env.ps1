param(
    [switch]$IncludeOptionalTools
)

$ErrorActionPreference = "Stop"

function Test-CommandExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Install-WithWinget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Id
    )

    Write-Host "Installing $Id ..."
    winget install --id $Id -e --accept-package-agreements --accept-source-agreements --disable-interactivity
}

if (-not (Test-CommandExists "winget")) {
    throw "winget is required to bootstrap the development environment."
}

$requiredPackages = @(
    @{ Id = "Microsoft.DotNet.SDK.8"; Check = { Test-CommandExists "dotnet" } },
    @{ Id = "Git.Git"; Check = { Test-CommandExists "git" } },
    @{ Id = "BurntSushi.ripgrep.MSVC"; Check = { Test-CommandExists "rg" } },
    @{ Id = "Microsoft.PowerShell"; Check = { Test-CommandExists "pwsh" } },
    @{ Id = "JRSoftware.InnoSetup"; Check = {
            (Test-Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") -or
            (Test-Path "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") -or
            (Test-Path "C:\Program Files\Inno Setup 6\ISCC.exe")
        }
    }
)

$optionalPackages = @(
    @{ Id = "sharkdp.fd"; Check = { Test-CommandExists "fd" } },
    @{ Id = "sharkdp.bat"; Check = { Test-CommandExists "bat" } },
    @{ Id = "junegunn.fzf"; Check = { Test-CommandExists "fzf" } },
    @{ Id = "Microsoft.VisualStudioCode"; Check = { (Test-CommandExists "code") -or (Test-Path "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe") } }
)

foreach ($package in $requiredPackages) {
    if (-not (& $package.Check)) {
        Install-WithWinget -Id $package.Id
    }
    else {
        Write-Host "$($package.Id) already installed."
    }
}

if ($IncludeOptionalTools) {
    foreach ($package in $optionalPackages) {
        if (-not (& $package.Check)) {
            Install-WithWinget -Id $package.Id
        }
        else {
            Write-Host "$($package.Id) already installed."
        }
    }
}

Write-Host "Development environment bootstrap completed."
