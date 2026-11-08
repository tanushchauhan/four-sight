"""Bundle only coordinator settings, never the complete environment file."""
import os
import plistlib
import subprocess
from pathlib import Path

settings = {}
env_file = Path(__file__).resolve().parents[2] / 'agent' / '.env'
if env_file.exists():
    for line in env_file.read_text().splitlines():
        key, separator, value = line.strip().removeprefix('export ').partition('=')
        if separator and key.strip() == 'AGENT_URL':
            settings[key.strip()] = value.strip().strip('\"\'')
for key in ('AGENT_URL',):
    if key in os.environ:
        settings[key] = os.environ[key]
if not settings.get('AGENT_URL', '').strip():
    host = subprocess.check_output(['/usr/sbin/scutil', '--get', 'LocalHostName'], text=True).strip()
    settings['AGENT_URL'] = f'http://{host}.local:8788'
output = Path(os.environ['TARGET_BUILD_DIR']) / os.environ['UNLOCALIZED_RESOURCES_FOLDER_PATH'] / 'AgentConfiguration.plist'
output.parent.mkdir(parents=True, exist_ok=True)
with output.open('wb') as destination:
    plistlib.dump(settings, destination)
