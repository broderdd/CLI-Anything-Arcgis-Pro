"""MCP server bridging Claude Code to the LIVE ArcGIS Pro session.

Architecture:
    Claude Code  --stdio JSON-RPC-->  this server  --HTTP-->  ProSimpleMapExport
    add-in (inside ArcGIS Pro, 127.0.0.1:5005)  -->  live project (QueuedTask).

Zero third-party deps: implements the MCP stdio protocol by hand using only the
standard library, so it runs on any Python 3 without `pip install`.
"""

import os
import sys
import json
import urllib.request
import urllib.error

# Where the in-Pro add-in bridge listens. Override with ARCGIS_BRIDGE_URL when the
# server and ArcGIS Pro are on different hosts (e.g. running this in a container).
BRIDGE_URL = os.environ.get("ARCGIS_BRIDGE_URL", "http://127.0.0.1:5005/")
# Shared secret matching the in-Pro bridge (ARCGIS_BRIDGE_TOKEN). Sent as the
# X-Bridge-Token header on every request so only this MCP server can drive Pro.
_BRIDGE_TOKEN = os.environ.get("ARCGIS_BRIDGE_TOKEN", "")

# --- Deny-by-default deletion guard -----------------------------------------
# Block any geoprocessing tool whose name looks destructive (Delete*/Truncate*)
# unless the caller explicitly opts in via an `allow_delete` argument or the
# ARCGIS_CLI_ALLOW_DELETE environment variable. Stops run_gp from deleting
# shapefiles / feature classes / geodatabase contents by default.
_DESTRUCTIVE_TOOL_TOKENS = ("delete", "truncate")
_TRUTHY = {"1", "true", "yes", "on", "y", "t"}


def _deletion_allowed(args):
    if str((args or {}).get("allow_delete", "")).strip().lower() in _TRUTHY:
        return True
    return os.environ.get("ARCGIS_CLI_ALLOW_DELETE", "").strip().lower() in _TRUTHY


def _is_destructive_tool(tool):
    t = (tool or "").lower()
    return any(tok in t for tok in _DESTRUCTIVE_TOOL_TOKENS)


def log(msg):
    print(f"[arcgis-mcp] {msg}", file=sys.stderr, flush=True)


def call_bridge(payload, timeout=180):
    """POST a command to the in-Pro bridge and return its parsed JSON."""
    data = json.dumps(payload).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if _BRIDGE_TOKEN:
        headers["X-Bridge-Token"] = _BRIDGE_TOKEN
    req = urllib.request.Request(BRIDGE_URL, data=data, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.URLError as e:
        return {
            "ok": False,
            "error": (
                f"无法连接 ArcGIS Pro 桥 ({BRIDGE_URL}): {e}. "
                "请确认 ArcGIS Pro 正在运行且已加载 ProSimpleMapExport add-in。"
            ),
        }
    except Exception as e:  # noqa: BLE001
        return {"ok": False, "error": f"桥调用失败: {e}"}


TOOLS = [
    {
        "name": "arcgis_ping",
        "description": (
            "读取当前打开的 ArcGIS Pro 工程状态：工程名、所有地图、所有布局、"
            "以及当前活动的地图/布局。发其它命令前先用它了解打开的是什么工程。"
        ),
        "inputSchema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "arcgis_export_layout",
        "description": (
            "把活着的 ArcGIS Pro 工程里的某个布局导出为 PDF。导出在用户正开着的 "
            "Pro 实例内执行（用户能看到），返回输出路径与文件大小。"
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "out": {
                    "type": "string",
                    "description": r"输出 PDF 的绝对路径，例如 C:\temp\map.pdf",
                },
                "layout": {
                    "type": "string",
                    "description": "布局名（可选；缺省用活动布局，否则用第一个布局）。",
                },
                "dpi": {"type": "integer", "description": "分辨率 DPI（默认 300）。"},
            },
            "required": ["out"],
        },
    },
    {
        "name": "arcgis_zoom_to",
        "description": (
            "把活动地图视图缩放到某个要素图层（用户能在窗口里看到地图动）。"
            "可选 where 条件：会先选中匹配要素再缩放到选集。需要 Pro 当前处于地图视图（非布局）。"
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "要缩放到的要素图层名。"},
                "where": {
                    "type": "string",
                    "description": "可选 SQL 条件，如 \"POP > 1000\"；给了就缩放到选中要素。",
                },
            },
            "required": ["layer"],
        },
    },
    {
        "name": "arcgis_query",
        "description": (
            "查询活工程里某要素图层的属性，返回结构化的行（不含几何）。可按 where 过滤、限制行数。"
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "要素图层名。"},
                "where": {"type": "string", "description": "可选 SQL where 条件。"},
                "map": {"type": "string", "description": "可选地图名（缺省用活动/第一个地图）。"},
                "limit": {"type": "integer", "description": "最多返回多少行（默认 50，0=不限）。"},
            },
            "required": ["layer"],
        },
    },
    {
        "name": "arcgis_run_gp",
        "description": (
            "在活工程上运行任意 ArcGIS 地理处理工具（整个 ArcToolbox：分析/管理/转换/栅格…）。"
            "输出图层会自动加到当前地图（用户能看到）。tool 用点号写法如 'analysis.Buffer'、"
            "'management.Clip'；params 是按工具签名顺序排列的位置参数字符串数组。"
            "例：tool='analysis.Buffer'，params=['roads','roads_buf','100 Meters']。"
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "tool": {
                    "type": "string",
                    "description": "工具名，点号写法，如 analysis.Buffer / management.Dissolve / sa.Slope。",
                },
                "params": {
                    "type": "array",
                    "items": {"type": "string"},
                    "description": (
                        "按工具参数顺序的位置参数（字符串）。"
                        "输入/输出强烈建议用数据集全路径（如 C:\\...\\x.gdb\\fc），"
                        "图层名在后台 GP 里解析不可靠。距离等参数如 '500 Meters'。"
                    ),
                },
                "allow_delete": {
                    "type": "boolean",
                    "description": (
                        "Safety opt-in. Destructive tools (Delete*/Truncate*) are BLOCKED by "
                        "default to protect shapefiles, feature classes and geodatabases. Set "
                        "true ONLY when you intend to delete or truncate data."
                    ),
                },
            },
            "required": ["tool", "params"],
        },
    },
    # ---- additive live-authoring tools (forwarded to BridgeAuthoring.cs) ----
    {
        "name": "arcgis_get_cim",
        "description": (
            "Read the CIM definition (as JSON) of a target in the live project. target: "
            "omit/'layer' for a layer (give 'layer'); 'map'; 'layout' (give 'layout'); "
            "'element' (give 'layout' + 'element'). Workhorse for inspecting symbology, "
            "renderer labels, definition queries, data connections before editing."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "target": {"type": "string", "description": "layer|map|layout|element (default layer)."},
                "layer": {"type": "string", "description": "Layer name (when target=layer)."},
                "map": {"type": "string", "description": "Map name (optional)."},
                "layout": {"type": "string", "description": "Layout name (when target=layout|element)."},
                "element": {"type": "string", "description": "Layout element name (when target=element)."},
            },
            "required": [],
        },
    },
    {
        "name": "arcgis_set_cim",
        "description": (
            "Replace the CIM definition of a target from JSON (same shape arcgis_get_cim "
            "returns, passed back under 'cim'). One mechanism for: definition-query edits, "
            "layer rename, renderer class-label edits (e.g. CIMUniqueValueRenderer group/class "
            ".Label text), and datasource repointing. target as in arcgis_get_cim."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "cim": {"type": "object", "description": "The full CIM definition JSON to apply."},
                "target": {"type": "string", "description": "layer|map|layout|element (default layer)."},
                "layer": {"type": "string", "description": "Layer name (when target=layer)."},
                "map": {"type": "string", "description": "Map name (optional)."},
                "layout": {"type": "string", "description": "Layout name (when target=layout|element)."},
                "element": {"type": "string", "description": "Layout element name (when target=element)."},
            },
            "required": ["cim"],
        },
    },
    {
        "name": "arcgis_rename_layer",
        "description": "Rename a layer in the live project.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "Current layer name."},
                "name": {"type": "string", "description": "New layer name."},
                "map": {"type": "string", "description": "Map name (optional)."},
            },
            "required": ["layer", "name"],
        },
    },
    {
        "name": "arcgis_set_definition_query",
        "description": (
            "Set (or clear) a feature layer's active definition query. Empty/missing 'query' clears it."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "Feature layer name."},
                "query": {"type": "string", "description": "SQL where clause; empty clears the definition query."},
                "map": {"type": "string", "description": "Map name (optional)."},
            },
            "required": ["layer"],
        },
    },
    {
        "name": "arcgis_set_visibility",
        "description": "Show/hide a layer in the table of contents.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "Layer name."},
                "visible": {"type": "boolean", "description": "true to show, false to hide."},
                "map": {"type": "string", "description": "Map name (optional)."},
            },
            "required": ["layer", "visible"],
        },
    },
    {
        "name": "arcgis_create_group",
        "description": "Create a new empty group layer in a map.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "New group layer name."},
                "map": {"type": "string", "description": "Map name (optional)."},
                "index": {"type": "integer", "description": "TOC insert index (default 0 = top)."},
            },
            "required": ["name"],
        },
    },
    {
        "name": "arcgis_move_layer",
        "description": (
            "Move/reorder a layer in the TOC and/or into a group. 'group' empty/missing moves to "
            "map root; 'index' is the target position (0 = top)."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "Layer name to move."},
                "group": {"type": "string", "description": "Destination group layer name (optional)."},
                "index": {"type": "integer", "description": "Target position (default 0)."},
                "map": {"type": "string", "description": "Map name (optional)."},
            },
            "required": ["layer"],
        },
    },
    {
        "name": "arcgis_clone_layer",
        "description": (
            "Duplicate a layer (full CIM: symbology + label classes + definition queries) one or "
            "more times into the same map, optionally into a group. 'count'>1 appends an index to "
            "'name'."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "Source layer name."},
                "name": {"type": "string", "description": "New layer name (base name if count>1)."},
                "count": {"type": "integer", "description": "How many clones (default 1)."},
                "group": {"type": "string", "description": "Destination group layer name (optional)."},
                "index": {"type": "integer", "description": "Insert index within container (default 0)."},
                "map": {"type": "string", "description": "Map name (optional)."},
            },
            "required": ["layer"],
        },
    },
    {
        "name": "arcgis_set_layout_text",
        "description": "Set the text of a named layout text element (e.g. 'Title', 'Title Description').",
        "inputSchema": {
            "type": "object",
            "properties": {
                "element": {"type": "string", "description": "Layout element name."},
                "text": {"type": "string", "description": "New text."},
                "layout": {"type": "string", "description": "Layout name (optional; else active/first)."},
            },
            "required": ["element", "text"],
        },
    },
    {
        "name": "arcgis_repoint_datasource",
        "description": (
            "Point a layer at a different feature class in a file geodatabase (e.g. a dissolved copy)."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "Layer name."},
                "gdb": {"type": "string", "description": r"File geodatabase path, e.g. C:\d.gdb"},
                "dataset": {"type": "string", "description": "Feature class name within the gdb."},
                "map": {"type": "string", "description": "Map name (optional)."},
            },
            "required": ["layer", "gdb", "dataset"],
        },
    },
    {
        "name": "arcgis_save_project",
        "description": (
            "Save the open ArcGIS Pro project (Project.Current.SaveAsync), so live edits "
            "persist without a manual Ctrl+S. Returns the project path."
        ),
        "inputSchema": {"type": "object", "properties": {}, "required": []},
    },
    {
        "name": "arcgis_delete_layer",
        "description": (
            "Remove a single, explicitly-named layer from a map (map authoring; does NOT "
            "delete data on disk). Errors if the name does not resolve to exactly one layer "
            "(no wildcards, never bulk-removes)."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "layer": {"type": "string", "description": "Exact layer name to remove."},
                "map": {"type": "string", "description": "Map name (optional; default = active map)."},
            },
            "required": ["layer"],
        },
    },
]


def handle(req):
    method = req.get("method")
    rid = req.get("id")

    if method == "initialize":
        client_ver = (req.get("params") or {}).get("protocolVersion", "2024-11-05")
        return ok(rid, {
            "protocolVersion": client_ver,
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "arcgis-pro-bridge", "version": "1.0.0"},
        })

    if method in ("notifications/initialized", "initialized"):
        return None  # notification, no response

    if method == "ping":
        return ok(rid, {})

    if method == "tools/list":
        return ok(rid, {"tools": TOOLS})

    if method == "tools/call":
        params = req.get("params") or {}
        name = params.get("name")
        args = params.get("arguments") or {}
        command_map = {
            "arcgis_ping": "ping",
            "arcgis_export_layout": "export_layout",
            "arcgis_zoom_to": "zoom_to",
            "arcgis_query": "query",
            "arcgis_run_gp": "run_gp",
            # additive live-authoring commands (BridgeAuthoring.cs)
            "arcgis_get_cim": "get_cim",
            "arcgis_set_cim": "set_cim",
            "arcgis_rename_layer": "rename_layer",
            "arcgis_set_definition_query": "set_definition_query",
            "arcgis_set_visibility": "set_visibility",
            "arcgis_create_group": "create_group",
            "arcgis_move_layer": "move_layer",
            "arcgis_clone_layer": "clone_layer",
            "arcgis_set_layout_text": "set_layout_text",
            "arcgis_repoint_datasource": "repoint_datasource",
            "arcgis_save_project": "save_project",
            "arcgis_delete_layer": "delete_layer",
        }
        cmd = command_map.get(name)
        if cmd is None:
            return err(rid, -32601, f"unknown tool: {name}")
        # Deny-by-default: block destructive geoprocessing unless explicitly allowed.
        if cmd == "run_gp" and _is_destructive_tool(args.get("tool")) and not _deletion_allowed(args):
            blocked = (
                f"Refused to run destructive tool {args.get('tool')!r}: it deletes or truncates "
                "data (shapefiles, feature classes, geodatabase contents) and is blocked by "
                "default. To proceed intentionally, set allow_delete=true in the tool arguments "
                "or set ARCGIS_CLI_ALLOW_DELETE=1 in the server environment."
            )
            return ok(rid, {"content": [{"type": "text", "text": blocked}], "isError": True})
        payload = {"command": cmd}
        payload.update(args)
        result = call_bridge(payload)
        text = json.dumps(result, ensure_ascii=False, indent=2)
        return ok(rid, {
            "content": [{"type": "text", "text": text}],
            "isError": not result.get("ok", False),
        })

    if rid is not None:
        return err(rid, -32601, f"method not found: {method}")
    return None


def ok(rid, result):
    return {"jsonrpc": "2.0", "id": rid, "result": result}


def err(rid, code, message):
    return {"jsonrpc": "2.0", "id": rid, "error": {"code": code, "message": message}}


# BOM / zero-width codepoints some pipes prepend to the first line.
_BOM_CODEPOINTS = (0xFEFF, 0xFFFE, 0x200B)


def main():
    # Claude Code speaks UTF-8 over stdio. On a non-UTF-8 locale (e.g. Chinese GBK)
    # Python would otherwise mis-decode the stream — force UTF-8 both ways.
    try:
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    log(f"started, bridging to {BRIDGE_URL}")
    for raw in sys.stdin:
        line = raw.strip()
        while line and ord(line[0]) in _BOM_CODEPOINTS:
            line = line[1:]
        if not line:
            continue
        try:
            req = json.loads(line)
        except Exception as e:  # noqa: BLE001
            log(f"bad json: {e}")
            continue
        try:
            resp = handle(req)
        except Exception as e:  # noqa: BLE001
            rid = req.get("id")
            resp = err(rid, -32603, str(e)) if rid is not None else None
        if resp is not None:
            sys.stdout.write(json.dumps(resp) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
