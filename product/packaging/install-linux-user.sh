#!/usr/bin/env bash
# Run as the user who will own the project, never via sudo.
set -euo pipefail
if [[ "$(id -u)" == 0 ]]; then printf 'Run this installer as your normal user.\n' >&2; exit 2; fi
bundle="$(cd "$(dirname "$0")" && pwd)"
config="${XDG_CONFIG_HOME:-$HOME/.config}/FLAMORIS/Kachinco"
data="${XDG_DATA_HOME:-$HOME/.local/share}/FLAMORIS/Kachinco"
mkdir -p "$config" "$data" "$HOME/.config/systemd/user"
chmod 700 "$config"
release="$data/releases/$(date -u +%Y%m%dT%H%M%S)-$$"
mkdir -p "$release"
cp -a "$bundle/." "$release/"
if [[ ! -e "$data/project.fkproj" ]]; then "$release/kachinco" project new --output "$data/project.fkproj" --name 'LIME Studio'; fi
if [[ ! -e "$config/hub-token" ]]; then (umask 077; : > "$config/hub-token"); fi
if [[ ! -e "$config/service.json" ]]; then
  python3 - "$config" "$data" <<'PY'
import json, pathlib, sys
config, data = map(pathlib.Path, sys.argv[1:])
with (config / 'service.json').open('x') as output:
    json.dump({'project': str(data / 'project.fkproj'),
               'hub-endpoint': 'wss://studio.flamoris.net/desktop/kachinco-lime',
               'credential-file': str(config / 'hub-token'), 'permission': 'edit',
               'output-root': '/srv/lime/generated/kachinco'}, output, indent=2)
PY
fi
chmod 600 "$config/hub-token" "$config/service.json"
# JSON-style quoting plus %% is compatible with these systemd absolute path arguments.
python3 - "$release/kachinco" "$config/service.json" "$HOME/.config/systemd/user/flamoris-kachinco.service" <<'PY'
import json, pathlib, sys
binary, config, unit = sys.argv[1:]
def quote(value):
    if any(ord(c) < 32 for c in value): raise ValueError('Control character in path')
    return json.dumps(value, ensure_ascii=False).replace('%', '%%')
pathlib.Path(unit).write_text(f'''[Unit]
Description=FLAMORIS Kachinco headless editor
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=5

[Service]
Type=simple
ExecStart={quote(binary)} serve --config {quote(config)}
Restart=on-failure
RestartSec=10
SuccessExitStatus=130
TimeoutStopSec=90
KillMode=control-group
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
MemoryMax=8G
TasksMax=128

[Install]
WantedBy=default.target
''')
PY
systemctl --user daemon-reload
printf 'Installed. Configure %s/service.json and %s/hub-token, then run:\n' "$config" "$config"
printf '  systemctl --user enable --now flamoris-kachinco\n'
printf 'For startup without login: sudo loginctl enable-linger %s\n' "$(id -un)"
