# 0.14: sankofa on Windows. Publishes the command and the daemon for win-x64 (self-contained, no
# Native AOT, to keep it quick), then drives them in a temporary repo: the ledger verbs, the
# daemon on a Unix socket under the temp folder, a claim, an ask that says held, a done.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$out = Join-Path ([System.IO.Path]::GetTempPath()) ("sankofa-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($p in 'sankofa-cli', 'sankofad') {
  & dotnet publish "$root/daemon/$p" -c Release -r win-x64 -p:PublishAot=false --self-contained -o "$out/bin" --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw "publish $p failed" }
}
$sankofa = "$out/bin/sankofa.exe"
$sankofad = "$out/bin/sankofad.exe"
if (-not (Test-Path $sankofa)) { throw "no sankofa.exe in $out/bin" }
if (-not (Test-Path $sankofad)) { throw "no sankofad.exe in $out/bin" }

function Assert-Eq($got, $want, $what) { if ("$got" -ne "$want") { throw "${what}: expected [$want] got [$got]" } }
function Assert-Like($got, $pattern, $what) { if ("$got" -notlike $pattern) { throw "${what}: [$got] does not match [$pattern]" } }

$repo = "$out/repo"
New-Item -ItemType Directory -Force "$repo/src" | Out-Null
Set-Location $repo
& git init -q
"int a;" | Set-Content "$repo/src/a.c"
$env:SANKOFA_SOCKET = "$out/sankofad.sock"
$env:SANKOFA_DB = "$out/sankofad.db"
$env:SANKOFA_SESSION = 'smoke'

# the ledger, with no daemon yet
Assert-Like (& $sankofa version) 'sankofa *' 'version'
& $sankofa init | Out-Null
if (-not (Test-Path "$repo/.sankofa/ledger.jsonl")) { throw 'init made no ledger' }
$id1 = (& $sankofa todo 'write the tests').Trim()
$id2 = (& $sankofa awaiting 'sign the release').Trim()
Assert-Eq $id1 'e1' 'todo id'
Assert-Eq $id2 'e2' 'awaiting id'
$open = (& $sankofa open) -join "`n"
Assert-Like $open '*e1 *write the tests*' 'open lists the todo'
Assert-Like $open '*e2 *sign the release*' 'open lists the awaiting item'
& $sankofa done e1 'tests written' | Out-Null
$open = (& $sankofa open) -join "`n"
if ($open -like '*e1 *') { throw 'done left e1 open' }
Assert-Like ((& $sankofa last 1) -join "`n") '*done*(e1)*' 'last shows the done with its ref'

# the daemon, on a socket under the temp folder
$daemon = Start-Process -FilePath $sankofad -ArgumentList 'serve', '--socket', $env:SANKOFA_SOCKET, '--db', $env:SANKOFA_DB -PassThru -WindowStyle Hidden -RedirectStandardError "$out/sankofad.err"
try {
  $up = $false
  for ($i = 0; $i -lt 100 -and -not $up; $i++) {
    & $sankofa daemon *> $null
    if ($LASTEXITCODE -eq 0) { $up = $true } else { Start-Sleep -Milliseconds 100 }
  }
  if (-not $up) { Get-Content "$out/sankofad.err" -ErrorAction SilentlyContinue; throw 'sankofad did not answer' }
  Assert-Like ((& $sankofa daemon) -join "`n") 'sankofad *' 'daemon answers'

  $claim = (& $sankofa claim 'src/**') -join "`n"
  if ($LASTEXITCODE -ne 0) { throw "claim failed: $claim" }
  Assert-Like $claim '*held*src/**' 'claim is held'
  $lease = ($claim -split '\s+')[0]
  Assert-Like ((& $sankofa ask 'src/a.c') -join "`n") 'held*src/a.c*' 'ask says held'
  & $sankofa done $lease | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'done on the lease failed' }
  Assert-Like ((& $sankofa ask 'src/a.c') -join "`n") 'free*src/a.c*' 'ask says free after done'
  $n = (& $sankofa todo 'one more through the daemon').Trim()
  Assert-Like $n 'e*' 'a write through the daemon gets an id'
}
finally {
  if ($daemon -and -not $daemon.HasExited) { Stop-Process -Id $daemon.Id -Force }
  Set-Location $root
}
Write-Host 'ok smoke (windows)'
