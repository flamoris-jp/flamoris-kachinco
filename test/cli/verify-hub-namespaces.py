#!/usr/bin/env python3
"""Use the real Hub parser to protect independent Windows/Linux routing."""
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(sys.argv[1]) / 'src'))
from flamoris_mcp_hub.config import Catalog, UpstreamConfig

tools = json.loads(pathlib.Path(sys.argv[2]).read_text())
windows = UpstreamConfig.model_validate(dict(id='kachinco', namespace='kachinco', transport='reverse-websocket',
    product_id='flamoris.kachinco', connector_token_env='KACHINCO_CONNECTOR_TOKEN', tools=tools))
linux = windows.model_copy(update=dict(id='kachinco-lime', namespace='kachinco_lime', connector_token_env='KACHINCO_LIME_CONNECTOR_TOKEN'))
combined = Catalog([windows, linux])
assert len(combined.tools) == 2 * len(tools)
for tool in tools:
    assert combined.tools['kachinco.' + tool['name']][0].id == 'kachinco'
    assert combined.tools['kachinco_lime.' + tool['name']][0].id == 'kachinco-lime'
assert windows.connector_token_env != linux.connector_token_env
print(json.dumps(dict(coexisting_tools=len(combined.tools), windows_id=windows.id, linux_id=linux.id, live_connection_verified=False)))
