"""Bundle only coordinator settings, never the complete environment file."""
import os
import plistlib
from pathlib import Path

settings = {}
env_file = Path(__file__).resolve().parents[2] / 'agent' / '.env'
if env_file.exists():
    for line in env_file.read_text().splitlines():
        key, separator, value = line.strip().removeprefix('export ').partition('=')
        if separator and key.strip() in {'AGENT_URL', 'AGENT_TOKEN'}:
            settings[key.strip()] = value.strip().strip('\"\'')
for key in ('AGENT_URL', 'AGENT_TOKEN'):
    if key in os.environ:
        settings[key] = os.environ[key]
output = Path(os.environ['TARGET_BUILD_DIR']) / os.environ['UNLOCALIZED_RESOURCES_FOLDER_PATH'] / 'AgentConfiguration.plist'
output.parent.mkdir(parents=True, exist_ok=True)
with output.open('wb') as destination:
    plistlib.dump(settings, destination)
