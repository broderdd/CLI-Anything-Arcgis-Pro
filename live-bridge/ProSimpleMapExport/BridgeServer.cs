using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ProSimpleMapExport
{
    /// <summary>
    /// In-process bridge: a tiny loopback HTTP server that lets an EXTERNAL process
    /// (Claude / an MCP server) send structured commands to the LIVE ArcGIS Pro session.
    ///
    /// Why this works where external ArcPy can't: this code runs inside Pro, so it can
    /// reach Project.Current / the open maps & layouts and execute on the MCT via
    /// QueuedTask.Run. The user watches the result in the Pro window; the caller gets JSON.
    ///
    /// Uses a raw TcpListener on 127.0.0.1 (no admin / no URL-ACL needed) speaking
    /// minimal HTTP/1.1. Protocol: POST a JSON body {"command": "...", ...params}.
    /// </summary>
    internal static partial class BridgeServer
    {
        public const int Port = 5005;
        public static readonly string LogPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProSimpleMapExport_bridge.log");
        private static TcpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        private static string _token = "";  // shared secret from ARCGIS_BRIDGE_TOKEN; "" = auth disabled

        public static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n"); } catch { }
        }

        public static void Start()
        {
            Log("BridgeServer.Start() called");
            if (_running) { Log("  already running"); return; }
            _token = Environment.GetEnvironmentVariable("ARCGIS_BRIDGE_TOKEN") ?? "";
            Log(_token.Length > 0
                ? "  token auth ENABLED (X-Bridge-Token required)"
                : "  token auth DISABLED (ARCGIS_BRIDGE_TOKEN not set)");
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "ArcGISProBridge" };
                _thread.Start();
                Log($"  listening on 127.0.0.1:{Port}");
            }
            catch (Exception ex)
            {
                Log("  Start FAILED: " + ex);
                throw;
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* ignore */ }
        }

        private static void Loop()
        {
            while (_running)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_running) break; else continue; }
                try { Handle(client); }
                catch { /* never let one request kill the loop */ }
                finally { try { client?.Close(); } catch { } }
            }
        }

        private static void Handle(TcpClient client)
        {
            var stream = client.GetStream();

            // --- read headers until CRLF CRLF ---
            var header = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) break;
                header.Append((char)b);
                if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
            }

            int contentLength = 0;
            string presentedToken = "";
            foreach (var line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                else if (line.StartsWith("X-Bridge-Token:", StringComparison.OrdinalIgnoreCase))
                    presentedToken = line.Substring("X-Bridge-Token:".Length).Trim();
            }

            // Deny-by-default auth: when a token is configured, every request must present it.
            if (_token.Length > 0 && !FixedTimeEquals(presentedToken, _token))
            {
                Log("  rejected request: missing/invalid X-Bridge-Token");
                var denyBody = Encoding.UTF8.GetBytes(Json(false, null, "unauthorized: missing or invalid X-Bridge-Token"));
                var denyHead = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 403 Forbidden\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    $"Content-Length: {denyBody.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(denyHead, 0, denyHead.Length);
                stream.Write(denyBody, 0, denyBody.Length);
                stream.Flush();
                return;
            }

            string body = "";
            if (contentLength > 0)
            {
                var buf = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int r = stream.Read(buf, read, contentLength - read);
                    if (r <= 0) break;
                    read += r;
                }
                body = Encoding.UTF8.GetString(buf, 0, read);
            }

            string json = Dispatch(body);
            var payload = Encoding.UTF8.GetBytes(json);
            var head = "HTTP/1.1 200 OK\r\n" +
                       "Content-Type: application/json; charset=utf-8\r\n" +
                       $"Content-Length: {payload.Length}\r\n" +
                       "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static string Dispatch(string body)
        {
            try
            {
                string command = "ping";
                JsonElement root = default;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    root = doc.RootElement.Clone();
                    if (root.TryGetProperty("command", out var c)) command = c.GetString();
                }

                object data;
                var re = root; // capture for use inside the MCT lambdas
                switch (command)
                {
                    case "ping":
                        data = QueuedTask.Run(() => (object)DoPing()).GetAwaiter().GetResult();
                        break;
                    case "export_layout":
                        data = QueuedTask.Run(() => (object)DoExport(re)).GetAwaiter().GetResult();
                        break;
                    case "zoom_to":
                        data = QueuedTask.Run(() => (object)DoZoomTo(re)).GetAwaiter().GetResult();
                        break;
                    case "query":
                        data = QueuedTask.Run(() => (object)DoQuery(re)).GetAwaiter().GetResult();
                        break;
                    case "run_gp":
                        // ExecuteToolAsync manages its own threading; do NOT wrap in QueuedTask.
                        data = DoRunGp(re).GetAwaiter().GetResult();
                        break;
                    case "rerun_history":
                        // Replays the last run_gp; delegates to DoRunGp -> ExecuteToolAsync.
                        data = DoRerunHistory(re).GetAwaiter().GetResult();
                        break;
                    case "list_layers":
                        // Enumerate a map's layers (name + group-qualified longName + source) so a
                        // headless caller can discover EXACT, resolvable layer references. GP layer
                        // params resolve top-level names against the active map but not group-nested
                        // leaf names, so 'nested' flags which refs need the active map / longName.
                        data = QueuedTask.Run(() => (object)DoListLayers(re)).GetAwaiter().GetResult();
                        break;
                    case "activate_map":
                        // Open/activate a map by name so headless GP resolves ITS layer params.
                        // CreateMapPaneAsync marshals to the UI thread itself; do NOT wrap in QueuedTask.
                        data = DoActivateMap(re).GetAwaiter().GetResult();
                        break;
                    // --- additive live-authoring commands (BridgeAuthoring.cs) ---
                    case "get_cim":
                        data = QueuedTask.Run(() => (object)DoGetCim(re)).GetAwaiter().GetResult();
                        break;
                    case "set_cim":
                        data = QueuedTask.Run(() => (object)DoSetCim(re)).GetAwaiter().GetResult();
                        break;
                    case "rename_layer":
                        data = QueuedTask.Run(() => (object)DoRenameLayer(re)).GetAwaiter().GetResult();
                        break;
                    case "set_definition_query":
                        data = QueuedTask.Run(() => (object)DoSetDefinitionQuery(re)).GetAwaiter().GetResult();
                        break;
                    case "set_visibility":
                        data = QueuedTask.Run(() => (object)DoSetVisibility(re)).GetAwaiter().GetResult();
                        break;
                    case "create_group":
                        data = QueuedTask.Run(() => (object)DoCreateGroup(re)).GetAwaiter().GetResult();
                        break;
                    case "move_layer":
                        data = QueuedTask.Run(() => (object)DoMoveLayer(re)).GetAwaiter().GetResult();
                        break;
                    case "clone_layer":
                        data = QueuedTask.Run(() => (object)DoCloneLayer(re)).GetAwaiter().GetResult();
                        break;
                    case "set_layout_text":
                        data = QueuedTask.Run(() => (object)DoSetLayoutText(re)).GetAwaiter().GetResult();
                        break;
                    case "repoint_datasource":
                        data = QueuedTask.Run(() => (object)DoRepointDatasource(re)).GetAwaiter().GetResult();
                        break;
                    case "save_project":
                        // Project.SaveAsync must run on the MCT; DoSaveProject wraps it in
                        // QueuedTask.Run internally and returns the Task we await here.
                        data = DoSaveProject(re).GetAwaiter().GetResult();
                        break;
                    case "delete_layer":
                        data = QueuedTask.Run(() => (object)DoDeleteLayer(re)).GetAwaiter().GetResult();
                        break;
                    default:
                        return Json(false, null, $"unknown command: {command}");
                }
                return Json(true, data, null);
            }
            catch (Exception ex)
            {
                Log("Dispatch error: " + ex);
                return Json(false, null, ex.Message);
            }
        }

        // ---- command handlers (all run on the MCT) ----

        private static object DoPing()
        {
            var proj = Project.Current;
            return new
            {
                bridge = "ProSimpleMapExport",
                version = "1.0",
                port = Port,
                project = proj?.Name,
                projectPath = proj?.URI,
                maps = proj?.GetItems<MapProjectItem>().Select(m => m.Name).ToArray() ?? Array.Empty<string>(),
                layouts = proj?.GetItems<LayoutProjectItem>().Select(l => l.Name).ToArray() ?? Array.Empty<string>(),
                activeLayout = LayoutView.Active?.Layout?.Name,
                activeMap = MapView.Active?.Map?.Name
            };
        }

        private static object DoExport(JsonElement root)
        {
            string layoutName = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("layout", out var l) ? l.GetString() : null;
            string outPath = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("out", out var o) ? o.GetString() : null;
            int dpi = 300;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("dpi", out var d) && d.TryGetInt32(out var dv)) dpi = dv;

            if (string.IsNullOrWhiteSpace(outPath))
                throw new Exception("missing 'out' (output PDF path)");

            Layout layout = null;
            if (!string.IsNullOrWhiteSpace(layoutName))
            {
                var item = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(i => string.Equals(i.Name, layoutName, StringComparison.OrdinalIgnoreCase));
                layout = item?.GetLayout();
                if (layout == null) throw new Exception($"layout not found: {layoutName}");
            }
            layout ??= LayoutView.Active?.Layout;
            layout ??= Project.Current.GetItems<LayoutProjectItem>().FirstOrDefault()?.GetLayout();
            if (layout == null) throw new Exception("no layout available in this project");

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var pdf = new PDFFormat { OutputFileName = outPath, Resolution = dpi, DoCompressVectorGraphics = true };
            layout.Export(pdf);

            return new
            {
                layout = layout.Name,
                output = outPath,
                dpi,
                exists = File.Exists(outPath),
                bytes = File.Exists(outPath) ? new FileInfo(outPath).Length : 0
            };
        }

        private static object DoZoomTo(JsonElement root)
        {
            string layerName = Str(root, "layer");
            if (string.IsNullOrWhiteSpace(layerName)) throw new Exception("missing 'layer'");
            string where = Str(root, "where");

            var mv = MapView.Active;
            if (mv == null)
                throw new Exception("没有活动的地图视图——请在 ArcGIS Pro 里切到一个地图标签页（不是布局）。");

            var map = mv.Map;
            var layer = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
            if (layer == null)
                throw new Exception($"活动地图「{map.Name}」里找不到要素图层: {layerName}");

            var span = TimeSpan.FromSeconds(1.5);
            if (!string.IsNullOrWhiteSpace(where))
            {
                var sel = layer.Select(new QueryFilter { WhereClause = where });
                long count = sel.GetCount();
                bool z = count > 0 && mv.ZoomToSelected(span);
                return new { map = map.Name, layer = layer.Name, where, selected = count, zoomed = z };
            }
            var extent = layer.QueryExtent();
            bool zoomed = mv.ZoomTo(extent, span);
            return new { map = map.Name, layer = layer.Name, zoomed };
        }

        private static object DoQuery(JsonElement root)
        {
            string layerName = Str(root, "layer");
            if (string.IsNullOrWhiteSpace(layerName)) throw new Exception("missing 'layer'");
            string where = Str(root, "where");
            int limit = IntOr(root, "limit", 50);

            // Find the feature layer: requested map -> active map -> first map.
            Map map = null;
            string mapName = Str(root, "map");
            if (!string.IsNullOrWhiteSpace(mapName))
                map = Project.Current.GetItems<MapProjectItem>()
                    .FirstOrDefault(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase))?.GetMap();
            map ??= MapView.Active?.Map;
            map ??= Project.Current.GetItems<MapProjectItem>().FirstOrDefault()?.GetMap();
            if (map == null) throw new Exception("工程里没有可用的地图");

            var layer = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
            if (layer == null) throw new Exception($"地图「{map.Name}」里找不到要素图层: {layerName}");

            var rows = new List<Dictionary<string, object>>();
            var qf = string.IsNullOrWhiteSpace(where) ? null : new QueryFilter { WhereClause = where };
            using (var cursor = layer.Search(qf))
            {
                int n = 0;
                while (cursor.MoveNext())
                {
                    if (limit > 0 && n >= limit) break;
                    n++;
                    using (var row = cursor.Current)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var f in row.GetFields())
                        {
                            if (f.FieldType == FieldType.Geometry || f.FieldType == FieldType.Blob ||
                                f.FieldType == FieldType.Raster) continue;
                            dict[f.Name] = row[f.Name];
                        }
                        rows.Add(dict);
                    }
                }
            }
            return new { map = map.Name, layer = layer.Name, where, returned = rows.Count, rows };
        }

        // List every layer in a map (flattened through groups). Returns each layer's leaf Name,
        // its group-qualified longName (e.g. "Tenure Context\Crown Title Land"), a 'nested' flag,
        // type, and best-effort data-source path. Lets a headless caller pick layer references that
        // GP can actually resolve. Runs on the MCT (dispatched via QueuedTask).
        private static object DoListLayers(JsonElement root)
        {
            Map map = null;
            string mapName = Str(root, "map");
            if (!string.IsNullOrWhiteSpace(mapName))
                map = Project.Current.GetItems<MapProjectItem>()
                    .FirstOrDefault(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase))?.GetMap();
            map ??= MapView.Active?.Map;
            map ??= Project.Current.GetItems<MapProjectItem>().FirstOrDefault()?.GetMap();
            if (map == null) throw new Exception("工程里没有可用的地图");

            var layers = new List<object>();
            foreach (var l in map.GetLayersAsFlattenedList())
            {
                // Build the group-qualified long name by walking parents.
                var parts = new List<string> { l.Name };
                var parent = l.Parent;
                while (parent is GroupLayer gl) { parts.Insert(0, gl.Name); parent = gl.Parent; }

                string ds = null;
                if (l is FeatureLayer fl)
                {
                    try { ds = fl.GetFeatureClass()?.GetPath()?.ToString(); } catch { }
                }
                layers.Add(new
                {
                    name = l.Name,
                    longName = string.Join("\\", parts),
                    nested = parts.Count > 1,
                    type = l.GetType().Name,
                    isFeatureLayer = l is FeatureLayer,
                    dataSource = ds
                });
            }
            return new { map = map.Name, count = layers.Count, layers };
        }

        // Open (or re-activate) a map by name so it becomes the active view. Headless GP resolves
        // GPLayer/GPFeatureLayer name params against the ACTIVE map, so driving a map-based custom
        // tool requires its source map to be active first. CreateMapPaneAsync marshals to the UI
        // thread internally, so this is NOT wrapped in QueuedTask by the dispatcher.
        private static async System.Threading.Tasks.Task<object> DoActivateMap(JsonElement root)
        {
            string mapName = Str(root, "map");
            if (string.IsNullOrWhiteSpace(mapName)) throw new Exception("missing 'map'");
            var item = Project.Current.GetItems<MapProjectItem>()
                .FirstOrDefault(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase));
            if (item == null) throw new Exception($"map not found: {mapName}");

            Map map = await QueuedTask.Run(() => item.GetMap());
            // CreateMapPaneAsync opens AND activates the map, marshalling to the UI thread itself.
            // (Earlier bug: the existing-pane branch called pane.Activate() on the bridge worker
            // thread - a silent no-op, so activate_map "returned ok" but MapView.Active never
            // changed. Verified: polling the active map after that path showed no switch at all.
            // Always creating a pane is reliable; a duplicate pane is harmless cosmetic clutter.)
            await ProApp.Panes.CreateMapPaneAsync(map);
            return new { activated = map.Name, uri = map.URI };
        }

        private static async System.Threading.Tasks.Task<object> DoRunGp(JsonElement root)
        {
            string tool = Str(root, "tool");
            if (string.IsNullOrWhiteSpace(tool))
                throw new Exception("missing 'tool' (e.g. analysis.Buffer, or a custom-toolbox tool name)");

            // [#1] Resolve a CUSTOM toolbox tool that is NOT on the default GP search path
            // (e.g. the Create Maps .pyt). If 'toolbox' (full path to a .pyt/.atbx/.tbx) is
            // supplied, build the full '<toolbox>\<tool>' reference that ExecuteToolAsync can
            // open. System tools (analysis.Buffer, ...) keep working with no 'toolbox'.
            string toolbox = Str(root, "toolbox");
            string toolRef = string.IsNullOrWhiteSpace(toolbox)
                ? tool
                : toolbox.TrimEnd('\\', '/') + "\\" + tool;

            // [#2] STRUCTURED parameters. Preserve JSON arrays so multiValue params (a list) and
            // ValueTables (a list of rows, each a list of column values) survive instead of being
            // flattened to a single string. Scalars still pass through as strings. This is what
            // lets the 85-param Create Maps tool (buffer/group/extra/legend ValueTables + the 20
            // multiValue Quick-Assign slots) be expressed at all.
            var values = new List<object>();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in p.EnumerateArray())
                    values.Add(JsonToGpValue(el));
            }

            // Use the NON-modal overload: (tool, values, environments, CancellationToken?,
            // GPToolExecuteEventHandler, flags). The 5-arg CancelableProgressor overload would
            // route through eval_modal (a modal progress dialog) and NRE when called headless.
            var valueArray = Geoprocessing.MakeValueArray(values.ToArray());
            var environments = Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);
            var gpResult = await Geoprocessing.ExecuteToolAsync(
                toolRef, valueArray, environments,
                (System.Threading.CancellationToken?)null,
                (GPToolExecuteEventHandler)null,
                GPExecuteToolFlags.AddOutputsToMap);

            // [#4] Record this invocation so 'rerun_history' can replay it verbatim, without the
            // caller rebuilding the (potentially 85-) parameter set.
            _lastGpRun = new GpRunRecord
            {
                Tool = tool,
                Toolbox = toolbox,
                ParamsJson = (root.ValueKind == JsonValueKind.Object &&
                              root.TryGetProperty("params", out var pj)) ? pj.GetRawText() : "[]",
            };

            return new
            {
                tool = toolRef,
                succeeded = !gpResult.IsFailed,
                errorCode = gpResult.ErrorCode,
                returnValue = gpResult.ReturnValue,
                outputs = gpResult.Values,
                messages = gpResult.Messages?.Select(m => m.Text).ToArray(),
                errorMessages = gpResult.ErrorMessages?.Select(m => m.Text).ToArray(),
            };
        }

        // [#2] Convert a JSON value to a GP parameter value. CONFIRMED at runtime: MakeValueArray
        // does NOT accept List<object>/List<List<object>> for multiValue/ValueTable params - it
        // stringifies them and the GP framework then re-splits on spaces, corrupting any value
        // containing a space (e.g. "Land Tenure" -> "Land","Tenure"). So arrays are encoded as the
        // GP delimited-string format instead:
        //   * array of scalars (multiValue) -> values joined by ';'
        //   * array of arrays  (ValueTable) -> rows joined by ';', columns by ' ', each cell
        //                                      single-quoted when empty or containing whitespace.
        // Scalars pass through as their literal text; null -> null.
        private static object JsonToGpValue(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Array:
                    bool isTable = false;
                    foreach (var probe in el.EnumerateArray()) { isTable = probe.ValueKind == JsonValueKind.Array; break; }
                    if (isTable)
                    {
                        var rows = new List<string>();
                        foreach (var row in el.EnumerateArray())
                        {
                            var cells = new List<string>();
                            if (row.ValueKind == JsonValueKind.Array)
                                foreach (var cell in row.EnumerateArray()) cells.Add(GpCell(cell));
                            else
                                cells.Add(GpCell(row));
                            rows.Add(string.Join(" ", cells));
                        }
                        return string.Join(";", rows);
                    }
                    var vals = new List<string>();
                    foreach (var item in el.EnumerateArray()) vals.Add(GpScalarText(item));
                    return string.Join(";", vals);
                case JsonValueKind.String:
                    return el.GetString();
                case JsonValueKind.Null:
                    return null;
                default:
                    return el.ToString();  // numbers / bools as their literal text
            }
        }

        // Raw scalar text for a JSON value (multiValue entries: ';' is the only delimiter, so
        // embedded spaces are fine and need no quoting).
        private static string GpScalarText(JsonElement el) =>
            el.ValueKind == JsonValueKind.Null ? "" :
            el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();

        // One ValueTable cell: single-quote when empty or containing whitespace/quote/semicolon so
        // the GP space-delimited row parser keeps it as a single column. Empty -> '' (empty cell).
        private static string GpCell(JsonElement el)
        {
            string s = GpScalarText(el);
            if (s.Length == 0 || s.IndexOfAny(new[] { ' ', '\t', '\'', '"', ';' }) >= 0)
                return "'" + s.Replace("'", "''") + "'";
            return s;
        }

        // [#4] The most recent run_gp invocation, captured for rerun_history.
        private sealed class GpRunRecord
        {
            public string Tool;
            public string Toolbox;
            public string ParamsJson;
        }
        private static GpRunRecord _lastGpRun;

        // [#4] Replay the most recent run_gp call (same tool/toolbox/params). This sidesteps
        // reconstructing a large parameter set when re-running an analysis. NOTE: it replays the
        // last BRIDGE-invoked run_gp; the Pro SDK exposes no public API to enumerate/re-run the
        // interactive GP *history*, so a UI-initiated run is not visible here. Workflow: drive the
        // tool once via run_gp (structured params), then rerun_history replays it.
        private static async System.Threading.Tasks.Task<object> DoRerunHistory(JsonElement root)
        {
            if (_lastGpRun == null)
                throw new Exception("no prior run_gp invocation recorded to re-run");
            string rebuilt = "{\"tool\":" + JsonSerializer.Serialize(_lastGpRun.Tool ?? "")
                + ",\"toolbox\":" + JsonSerializer.Serialize(_lastGpRun.Toolbox ?? "")
                + ",\"params\":" + (string.IsNullOrEmpty(_lastGpRun.ParamsJson) ? "[]" : _lastGpRun.ParamsJson)
                + "}";
            using var doc = JsonDocument.Parse(rebuilt);
            return await DoRunGp(doc.RootElement.Clone());
        }

        private static string Str(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int IntOr(JsonElement root, string name, int dflt) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : dflt;

        private static string Json(bool ok, object data, string error)
        {
            var obj = new Dictionary<string, object> { ["ok"] = ok };
            if (ok) obj["data"] = data; else obj["error"] = error;
            return JsonSerializer.Serialize(obj);
        }

        // Constant-time string comparison (avoids leaking the token via response timing).
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
