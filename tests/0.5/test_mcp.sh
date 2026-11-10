#!/bin/sh
# runs the MCP client test (python, no dependencies; meta-validates schemas when jsonschema is importable)
exec python3 "$(dirname "$0")/test_mcp.py"
