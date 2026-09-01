$ErrorActionPreference = 'Stop'

# TranscribeGeek ships an Inno Setup installer. The package downloads it from the GitHub release for the
# matching tag and verifies it against a SHA-256 checksum rather than embedding the binary. Because
# nothing is embedded, this package must NOT contain a tools\VERIFICATION.txt - that file is only
# for packages that ship a binary inside the nupkg, and including one is what the USP 8.0.0
# submission was rejected for.
$packageArgs = @{
  packageName    = 'transcribegeek'
  fileType       = 'exe'
  url            = 'https://github.com/techygeekshome/TranscribeGeek/releases/download/v1.1.2/TranscribeGeekSetup.exe'
  checksum       = '2480debb256f141ac3060689859573914f88c6eb049261ae4db92d14a3e0de84'
  checksumType   = 'sha256'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyPackage @packageArgs
