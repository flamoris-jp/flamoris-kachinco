#!/usr/bin/env python3
"""Convert the CLI's reviewed catalog to an independent Hub descriptor (JSON is YAML)."""
import json
import pathlib
import sys

if len(sys.argv) != 3:
    raise SystemExit('Usage: create-linux-hub-descriptor.py catalog.json kachinco-lime.yaml')
catalog = json.loads(pathlib.Path(sys.argv[1]).read_text())
tools = []
for item in catalog:
    name = item['name']
    query = name in {'mcp.context', 'get_project', 'clapper_resolve', 'recipe_validate', 'preview_frame', 'export_status', 'export_read'}
    tools.append(dict(item, annotations={'readOnlyHint': query, 'destructiveHint': not query, 'openWorldHint': False}))
descriptor = dict(id='kachinco-lime', namespace='kachinco_lime', transport='reverse-websocket',
                  product_id='flamoris.kachinco', connector_token_env='KACHINCO_LIME_CONNECTOR_TOKEN', tools=tools)
with pathlib.Path(sys.argv[2]).open('x') as output:
    json.dump(descriptor, output, indent=2)
