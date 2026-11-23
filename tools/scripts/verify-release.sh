#!/usr/bin/env bash
# Verifies a release directory the way the PCs and the server read it, with the public key of the repository
# (config/update-public.pem), independently of the PowerShell signer:
#   - components.<c>.signature: RSA-PSS-SHA256 over the package bytes (UpdateDownloader on the agent);
#   - manifestSignatures.<c>: RSA-PSS-SHA256 over Signing.ManifestCanonicalBytes, i.e.
#     {"component","version","url","sha256","size","publishedAt"} with the manifest's own publishedAt;
#   - sha256 and size of every package.
# Usage: verify-release.sh <release dir> [public key pem]
set -euo pipefail
dir=${1:?release directory}
key=${2:-"$(dirname "$0")/../../config/update-public.pem"}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

python3 - "$dir" "$work" <<'PY'
import json, base64, hashlib, os, sys
d, w = sys.argv[1], sys.argv[2]
m = json.load(open(os.path.join(d, 'manifest.json'), encoding='utf-8'))
for name, c in m['components'].items():
    package = m['packages'][name]
    data = open(os.path.join(d, package), 'rb').read()
    if hashlib.sha256(data).hexdigest() != c['sha256'] or len(data) != c['size']:
        sys.exit(f'{name}: {package} does not match its sha256/size in manifest.json')
    canonical = json.dumps({k: c[k] for k in ('component', 'version', 'url', 'sha256', 'size', 'publishedAt')},
                           separators=(',', ':'), ensure_ascii=True)
    open(os.path.join(w, f'{name}.canonical'), 'w', encoding='utf-8').write(canonical)
    open(os.path.join(w, f'{name}.package'), 'w').write(os.path.join(d, package))
    open(os.path.join(w, f'{name}.filesig'), 'wb').write(base64.b64decode(c['signature']))
    open(os.path.join(w, f'{name}.manifestsig'), 'wb').write(base64.b64decode(m['manifestSignatures'][name]))
PY

fail=0
for canonical in "$work"/*.canonical; do
  name=$(basename "$canonical" .canonical)
  package=$(cat "$work/$name.package")
  for check in "file:$package:$work/$name.filesig" "manifest:$canonical:$work/$name.manifestsig"; do
    IFS=: read -r what data sig <<<"$check"
    if openssl dgst -sha256 -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:32 -verify "$key" -signature "$sig" "$data" >/dev/null 2>&1; then
      echo "ok   $name $what signature"
    else
      echo "FAIL $name $what signature"; fail=1
    fi
  done
done
exit $fail
