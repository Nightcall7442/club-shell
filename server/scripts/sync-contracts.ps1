<#
.SYNOPSIS
    Vendors the club-contracts bundle into server/contracts (docs/server/DESIGN.md 2.1, D-3).

.DESCRIPTION
    Takes openapi/openapi.yaml and asyncapi/asyncapi.yaml from a club-contracts commit (git blobs, so line endings
    are exactly the committed ones), writes server/contracts/REF with the full commit sha and regenerates
    openapi.json for the schema tests. Needs git and python with PyYAML.

.EXAMPLE
    ./server/scripts/sync-contracts.ps1                       # ../club-contracts at HEAD
    ./server/scripts/sync-contracts.ps1 -Ref shell-changes-p0-p1
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '../../../club-contracts'),
    [string]$Ref = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$contracts = Join-Path $PSScriptRoot '../contracts'
$python = if (Get-Command python -ErrorAction SilentlyContinue) { 'python' } else { 'python3' }

$script = @'
import json, subprocess, sys, yaml
source, ref, out = sys.argv[1:4]
sha = subprocess.check_output(['git', '-C', source, 'rev-parse', ref + '^{commit}'], text=True).strip()
for path, name in (('openapi/openapi.yaml', 'openapi.yaml'), ('asyncapi/asyncapi.yaml', 'asyncapi.yaml')):
    with open(f'{out}/{name}', 'wb') as f:
        f.write(subprocess.check_output(['git', '-C', source, 'show', f'{sha}:{path}']))
with open(f'{out}/openapi.yaml', encoding='utf-8') as f:
    document = yaml.safe_load(f)
with open(f'{out}/openapi.json', 'w', encoding='utf-8', newline='\n') as f:
    json.dump(document, f, ensure_ascii=False)
with open(f'{out}/REF', 'w', newline='\n') as f:
    f.write(sha + '\n')
print(f'ok: server/contracts <- club-contracts@{sha}')
'@

$script | & $python - $Source $Ref $contracts
if ($LASTEXITCODE -ne 0) { throw "sync-contracts failed ($LASTEXITCODE)" }
